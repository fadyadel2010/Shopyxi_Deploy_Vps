# Admin AI product generation — production configuration

## Scope

The Admin **Create Product with AI** workflow calls
`POST /api/admin/ai/products/generate`. The API maps the request through
`AdminProductGeneratorService` and `AIProviderTransport` to the configured
OpenRouter chat-completions endpoint.

## Proven production failure

The deployed API pod mounted `shopixy-api-json` with this effective
configuration:

```json
"CommerceAI": { "Enabled": false }
```

No `CommerceAI__Enabled`, `CommerceAI__ApiKey` or other `CommerceAI__*`
environment variable existed on `deployment/shopixy-api`. The request failed in
4–14 ms before any outbound HTTP call, proving the 503 came from the API's own
pre-flight guard, not from OpenRouter, Traefik, Cloudflare or an upstream
gateway.

The production API error normalizer intentionally replaces all 5xx bodies with
`{"code":"INTERNAL_ERROR","message":"An unexpected error occurred."}`. The
underlying status is still the API's `503` from
`AdminProductGenerationException`; the real error code remains in the
application code and is not exposed to the browser.

## Configuration contract

Non-secret values are stored in `configmap/shopixy-api-json`, key
`appsettings.json`:

| Setting | Value |
| --- | --- |
| `CommerceAI:Enabled` | `true` |
| `CommerceAI:Provider` | `openrouter` |
| `CommerceAI:Model` | `openrouter/free` |
| `CommerceAI:FreeModelsOnly` | `true` |
| `CommerceAI:Endpoint` | `https://openrouter.ai/api/v1/chat/completions` |
| `CommerceAI:TimeoutSeconds` | `90` |
| `CommerceAI:MaxTokens` | `8192` |
| `CommerceAI:Temperature` | `0.3` |

The credential is stored only in `secret/shopixy-api-commerceai-v1`, key
`api-key`, and is bound to the API container as the environment variable
`CommerceAI__ApiKey` through a `secretKeyRef`. The value is never placed in a
ConfigMap, manifest, log, report or Git object.

`Provider`, `Model` and `Endpoint` are explicitly pinned so a future change to
the compiled defaults cannot silently change the provider or model. There is no
fallback model or fallback provider.

## Apply

The key is read from stdin only:

```bash
./apply-production-config.sh < /secure/path/openrouter.key.txt
```

The script:

1. writes rollback snapshots under `/var/lib/shopixy/ai-product-generation-<UTC>/`;
2. merges the non-secret `CommerceAI` settings into the existing ConfigMap
   without dropping unrelated keys;
3. creates/updates `secret/shopixy-api-commerceai-v1` from stdin;
4. adds only `CommerceAI__ApiKey` to the existing `api` container using a
   strategic merge patch;
5. waits for the API rollout and verifies the non-secret values and the
   presence (never the value) of the injected credential.

Only `deployment/shopixy-api` is restarted. No image rebuild is required for a
configuration-only correction. Web, CommerceHub, Storefront, database,
message-broker and storage workloads are untouched.

## Rollback

```bash
./rollback.sh
```

The script restores the captured ConfigMap and Deployment, restores the prior
secret if one existed, or deletes the new secret if it did not, then waits for
the API rollout.

## Verification

1. `kubectl -n shopixy-app rollout status deployment/shopixy-api`
2. `kubectl -n shopixy-app get pods -l app=shopixy-api` — readiness and restart
   count.
3. Confirm the non-secret ConfigMap values and that the running container sees
   `CommerceAI__ApiKey` without printing it.
4. Run a real generation from `https://admin.shopyxi.com/products/new/edit`.
5. Confirm the generated draft populates the form and no product is saved.

## Redis runtime ACL

The mounted `redis-ha-auth` ACL for `shopixy-api-runtime` did not allow the
`ai:ratelimit:*` or `ai:usage:*` key namespaces. Redis therefore rejected the
rate-limiter Lua script with `RedisServerException`; `AIRateLimiter.CheckAsync`
failed closed and returned `429`. The ACL is updated with the two exact key
patterns and the Redis pods are rolled one at a time so the ACL file is
reloaded. The existing `ratelimit:*` namespace remains unchanged.

## OpenRouter egress

The API pod has no direct internet egress. Its `HTTPS_PROXY` points at the
allowlisted `shopixy-r2-egress` CONNECT proxy, which previously accepted only
the exact R2 host. That caused `HttpRequestException` on the OpenRouter call.
`proxy.cjs` now accepts an ordered allowlist: the existing R2 host plus the
exact host `openrouter.ai` (port 443 only), still with public-IPv4 validation
and no wildcard or redirect following. The proxy ConfigMap is patched and the
proxy deployment is restarted; the API deployment does not need a rebuild.

## Frontend `reportAllChanges` exception

`reportAllChanges` is not present in the application source or in the deployed
application bundle. It is an option/function of Cloudflare Web Analytics'
`beacon.min.js`, which Cloudflare injects into the HTML response. The current
production CSP blocks that script. The known third-party failure mode is a
missing `entries[0]` navigation/TTFB entry being dereferenced as
`undefined.startTime`.

The application must not add the beacon origin to `script-src` to silence the
CSP violation, because that would allow the failing third-party code to execute.
The correct mitigation is to keep the beacon blocked on admin origins (already
enforced) and disable automatic Web Analytics injection for the admin hostname
in Cloudflare if the exception is still observed in a browser that bypasses the
CSP. This is an external Cloudflare zone setting, not an application contract.
