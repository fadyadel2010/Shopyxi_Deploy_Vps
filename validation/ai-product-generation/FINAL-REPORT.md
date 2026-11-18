# AI Product Generation — Production Failure Forensic Report

**Date:** 2026-11-18
**Surface:** `https://admin.shopyxi.com/products/new/edit`
**Endpoint:** `POST /api/admin/ai/products/generate`
**Branch:** `ai/product-generation-production-fix-20261118`

## 1. Original browser evidence

Authenticated production browser, real user session, real product form. Clicking
**Create Product with AI → Generate Product** produced a 503 in about 4 ms.

Screenshot: `evidence/01-original-503-modal.png`

The modal displayed the production-normalized message: **“An unexpected error
occurred.”** No generated preview appeared. The form stayed responsive and the
modal could be closed.

## 2. Exact API failure and response

Request:

```http
POST /api/admin/ai/products/generate
```

Payload:

```json
{
  "productIdea": "Wireless RGB gaming mouse",
  "language": "English",
  "country": "",
  "tone": "marketing",
  "featureCount": 5,
  "faqCount": 3
}
```

Response:

```http
HTTP/1.1 503 Service Unavailable
```

```json
{
  "code": "INTERNAL_ERROR",
  "message": "An unexpected error occurred.",
  "traceId": "6b545bc2-8504-427b-9c22-e85161ed2483"
}
```

API access log correlation:

```text
2026-11-18T18:25:34.6798Z  StatusCode=503  LatencyMs=4.1942
```

The 4 ms latency proved the failure occurred **before any outbound provider
request**. Cloudflare, Traefik and OpenRouter were not in the failing call path.

## 3. Proven source of the 503

The 503 came from our API application, specifically
`PointOfSale.API/Services/AI/Admin/AdminProductGeneratorService.cs`:

```csharp
if (!_options.Enabled)
{
    throw new AdminProductGenerationException(
        StatusCodes.Status503ServiceUnavailable,
        "AI is disabled",
        "Commerce AI is currently unavailable for product generation.");
}
```

The mounted production `appsettings.json` contained only:

```json
"CommerceAI": { "Enabled": false }
```

`AdminProductGenerationException` returned 503. The production
`ApiErrorResponseNormalizationFilter` intentionally replaces every 5xx body
with `INTERNAL_ERROR / An unexpected error occurred.` while preserving the HTTP
status. That is why the browser did not see `AI is disabled`.

## 4. Local versus production configuration

| Item | Production before fix | Repository default | Finding |
| --- | --- | --- | --- |
| `CommerceAI:Enabled` | `false` (mounted ConfigMap) | `false` | Production never enabled the feature |
| `CommerceAI:Provider` | absent; compiled default `openrouter` | `openrouter` | No provider mismatch |
| `CommerceAI:Model` | absent; compiled default `openrouter/free` | `openrouter/free` | No model mismatch |
| `CommerceAI:Endpoint` | absent; compiled default OpenRouter URL | OpenRouter URL | No endpoint mismatch |
| `CommerceAI:ApiKey` | absent; empty compiled default | empty | No production credential existed |
| `CommerceAI__*` env vars | none | none | No environment override |
| CommerceAI secret | none | none | No secret reference existed |

The repository itself is fail-closed and does not contain a provider key. The
user’s prior local success therefore depended on an untracked local override
(environment variable or user-secrets) that was never transferred to the
production secret/config mechanism. No local credential was copied to
production, and no credential value was read from or written to Git.

## 5. Provider and model findings

- Provider: `openrouter`
- Endpoint: `https://openrouter.ai/api/v1/chat/completions`
- Model: `openrouter/free` (OpenRouter “Free Models Router”)
- Policy: `FreeModelsOnly=true`
- Timeout: 90 s
- Max tokens: 8192
- Temperature: 0.3

The OpenRouter model catalog confirms `openrouter/free` exists and supports
`response_format`. No provider or model substitution was made.

## 6. Secret-reference verification (no values)

- Secret: `shopixy-app/shopixy-api-commerceai-v1`
- Key: `api-key`
- Binding: `CommerceAI__ApiKey` via `secretKeyRef` on the `api` container
- Presence verified inside the running container without printing the value
- OpenRouter credential validation: `GET https://openrouter.ai/api/v1/key`
  returned HTTP 200, label `<masked>`, account limit `50`, usage `0`,
  free tier `true`
- The credential was streamed from a local file over SSH stdin into Kubernetes;
  it was never an argument, ConfigMap value, log line, report value, or Git
  object. The local key file was deleted after installation.

## 7. Network and TLS findings

The API has no direct internet egress. Its `HTTPS_PROXY` points at the existing
exact-host CONNECT proxy `shopixy-r2-egress:3128`, which previously accepted only
`<32-hex>.r2.cloudflarestorage.com:443`.

- DNS for `openrouter.ai` resolved normally.
- The API’s first real provider attempt produced `HttpRequestException` and the
  proxy rejected the CONNECT, so the API returned 502 and the edge returned a
  Cloudflare 502 page.
- After the proxy allowlist change:
  - `CONNECT openrouter.ai:443` → `HTTP/1.1 200 Connection Established`
  - `CONNECT example.com:443` → `HTTP/1.1 403 Forbidden`
- The subsequent OpenRouter HTTPS request returned HTTP 200, proving DNS, TCP,
  CONNECT, TLS and certificate validation all succeeded end to end.

## 8. Root cause

The original 503 was caused by the production CommerceAI feature being disabled
and having no credential. Once enabled, two previously unexercised production
controls blocked the workflow:

1. **Redis ACL:** `shopixy-api-runtime` lacked the `ai:ratelimit:*` and
   `ai:usage:*` key patterns. The rate-limiter Lua script raised
   `RedisServerException`; the limiter failed closed with 429.
2. **Egress proxy:** the exact-host proxy did not allow `openrouter.ai:443`.
   The provider connection raised `HttpRequestException` and the API returned
   502.

The complete production failure was therefore configuration + two dependency
authorization gaps, not a single OpenRouter key problem. The OpenRouter key was
valid throughout.

## 9. `startTime` exception root cause

`reportAllChanges` is **not present** in the application source, the admin
bundle, or the deployed web image.

It is present in Cloudflare Web Analytics’
`https://static.cloudflareinsights.com/beacon.min.js/...`, which Cloudflare
injects into HTML responses. That beacon embeds a web-vitals build that
dereferences a missing TTFB/navigation entry as `undefined.startTime`. This is a
third-party failure, not an application state or response-schema failure, and it
does not block the AI workflow.

The admin CSP already blocks the beacon. Browser console evidence before and
after the fix contains the CSP block message, not an application
`startTime` exception. A regression guard now asserts that the admin CSP never
allows `cloudflareinsights` and that application diagnostics never introduce
`reportAllChanges` or `startTime`.

Residual external risk: Cloudflare Web Analytics automatic injection is a zone
setting. If it remains enabled for `admin.shopyxi.com`, a browser that bypasses
CSP could still execute the third-party bug. The durable external mitigation is
to disable automatic Web Analytics injection for that hostname in Cloudflare.
This external dashboard change was not made because no authorized Cloudflare
analytics token was available.

## 10. Fixes implemented

1. Enabled `CommerceAI` with explicit provider, model, endpoint, free-model
   policy, timeout, max tokens and temperature.
2. Added `secret/shopixy-api-commerceai-v1` and bound it as
   `CommerceAI__ApiKey`; no value in Git or ConfigMap.
3. Added the exact Redis ACL namespaces `~ai:ratelimit:*` and `~ai:usage:*` to
   `shopixy-api-runtime` and rolled Redis one node at a time.
4. Extended the existing exact-host egress proxy to allow
   `openrouter.ai:443` in addition to the R2 host. No wildcard, no redirect,
   public-IPv4 validation retained.
5. Added regression tests for the production ConfigMap shape, the opaque 503
   body, the beacon boundary and the proxy allowlist.
6. Added `deploy/ai-product-generation/` with the configuration runbook,
   apply/rollback scripts, proxy source and unit tests.

## 11. Backend and frontend tests

| Suite | Command | Result |
| --- | --- | --- |
| Backend AI reliability | `dotnet test ... --filter FullyQualifiedName~ShopixyAiReliabilityTests` | **48 passed, 0 failed** |
| AI modal | `npx vitest run src/components/AiProductGeneratorModal.test.tsx` | **12 passed, 0 failed** |
| Frontend AI boundary | `npx vitest run src/architecture-tests/aiProductGenerationBoundary.test.ts` | **3 passed, 0 failed** |
| Egress proxy | `node --test deploy/ai-product-generation/proxy.test.cjs` | **4 passed, 0 failed** |

Covered: disabled config, missing key, invalid key, provider auth failure, rate
limit, timeout, provider unavailable, successful generation, malformed output,
count mismatch, safe status mapping, no secret leakage, modal loading/disabled
states, retry, stale response, opaque 503, missing `startTime`, and proxy host
allowlisting.

## 12. Production deployment details

No application image was rebuilt or pushed. The API image remains:

```text
localhost/shopixy/api@sha256:75f49cb501bae5f3add2677367300bcdbb70a9799a257041fd7d38b01d084e04
```

Changed runtime objects:

- `shopixy-api-json` ConfigMap — CommerceAI non-secret settings
- `shopixy-api-commerceai-v1` Secret — provider credential
- `shopixy-api` Deployment — single `CommerceAI__ApiKey` secret reference
- `redis-ha-auth` Secret — two ACL key patterns
- `shopixy-redis-a` / `shopixy-redis-b` — rolling ACL reload
- `shopixy-r2-egress-v2` ConfigMap and `shopixy-r2-egress` Deployment — exact
  OpenRouter host allowlist and `AI_TARGET_HOST`

Verified state:

- API: `1/1` ready, `2/2` containers, restart counts `0,0`
- Redis: `a` master, `b` replica, `master_link_status=up`, both `2/2`
- Egress proxy: `1/1` ready, restart count `0`
- Web, CommerceHub, Storefront, database, message broker and storage workloads
  were not restarted.

Rollback:

- `/root/rollback.sh`
- `/var/lib/shopixy/ai-product-generation-20261118T185542Z`
- `/var/lib/shopixy/ai-redis-acl-20261118T185907Z`
- `/var/lib/shopixy/ai-egress-20261118T190407Z`
- previous egress ConfigMap `shopixy-r2-egress-v1` retained

## 13. Actual browser acceptance

The user authenticated personally. The real production page was used, not a
mock or a health endpoint.

| Step | Result |
| --- | --- |
| Open product creation page | PASS |
| Open **Create Product with AI** | PASS |
| Configure generation options | PASS |
| Submit real production request | PASS — HTTP 200 |
| Provider request succeeds | PASS — OpenRouter HTTP 200 |
| Generated draft renders in preview | PASS |
| Apply to product form | PASS |
| Name/description populate | PASS |
| Tags/slug populate | PASS |
| Meta title/keywords/description populate | PASS |
| Modal and form remain responsive | PASS |
| Uncaught JavaScript exception | NONE — console empty |
| Provider/secret data exposed | NONE |
| Manual simple product form | PASS |
| Manual variant product form | PASS |

Screenshots:

- `evidence/02-generated-name-description.png`
- `evidence/03-generated-tags-slug.png`
- `evidence/04-generated-seo.png`

## 14. Evidence of successful production generation

```text
19:05:20.9369Z  API outbound HttpClient request started
19:05:23.9811Z  OpenRouter response HTTP 200
19:05:53.9339Z  API request completed HTTP 200, latency 33007.569 ms
```

Generated draft included `Wireless RGB Gaming Mouse`,
`wireless-rgb-gaming-mouse`, 5 features and 3 FAQ entries, and populated the
form’s tags and SEO fields. Full sanitized evidence:
`evidence/03-successful-production-generation.json`.

## 15. No product saved or published

No `POST /api/products`, `PUT /api/products/{id}` or publish action was issued.
The generated values were applied to the unsaved form only. No production
product was created, modified or published during this mission.

## 16. Remaining risks and limitations

- Cloudflare Web Analytics automatic injection remains an external setting.
  The app CSP blocks it; fully removing the third-party exception requires
  disabling that injection for the admin hostname in Cloudflare.
- The egress allowlist is exact-host and depends on DNS returning public IPv4.
  A future OpenRouter hostname or CDN change would require an allowlist update.
- Redis ACL changes are persisted in `redis-ha-auth`; if the secret is replaced
  from an older source, the `ai:*` patterns must be reapplied.
- The AI output is a marketing draft. The form correctly flags unverified
  claims; merchant review is still required before publication.
- The generated meta description was 169 characters while the form displays a
  160-character guidance counter. The backend accepts up to 500; this is a copy
  quality guideline, not a failure.

## 17. Git, commits and PR

- Branch: `ai/product-generation-production-fix-20261118`
- Implementation commit: `e004ba3` — production CommerceAI configuration and
  tests
- Dependency/egress commit: `e0da635` — exact OpenRouter egress and beacon
  boundary guard
- Evidence/report commit: `d5784f4` — FINAL-REPORT and sanitized evidence
- Remote branch: `origin/ai/product-generation-production-fix-20261118`
- PR creation URL:
  `https://github.com/fadyadel2010/Shopyxi_Deploy_Vps/pull/new/ai/product-generation-production-fix-20261118`

No force-push or history rewrite was performed. Unrelated pre-existing working
tree changes were preserved in the original checkout; this work was done in a
separate worktree based on `origin/main`.

## Acceptance classification

| Criterion | Status |
| --- | --- |
| Actual root cause of 503 proven | **PASS** |
| Production AI provider request succeeds | **PASS** |
| Generated content populates product form | **PASS** |
| `startTime` exception resolved in application boundary | **FIXED AND RE-TESTED** |
| External Cloudflare beacon injection removed | **BLOCKED** — no authorized analytics token; CSP blocks it |
| No secrets exposed | **PASS** |
| Manual product creation remains functional | **PASS** |
| Required tests pass | **PASS** |
| Production deployment verified | **PASS** |
| Real browser acceptance passes | **PASS** |
| Evidence committed and remotely verified | **PASS** (final commit) |
