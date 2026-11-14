# Production rollout and rollback

## R2 independent-audit successor

This runbook now targets the independently rebuilt **r2** candidate, not the
preserved original r1 archive. Current review/status is in `R2-AUDIT-AND-ROLLOUT.md`.
Do not promote the original image or use its stale assembly inventory.

All commands are operator-reviewed. Do not continue past a failed gate. Keep
Cloudflare SSL/TLS mode at **Full (strict)**. Do not alter apex, exact platform,
MX, DKIM, DMARC, verification, or other existing records.

## 1. Freeze and capture

1. Announce the one-replica `Recreate` maintenance window.
2. Confirm the current API and storefront each have one replica and strategy
   `Recreate`.
3. Capture deployments, routes, certificates, ConfigMaps, DNS inventory, and the
   latest successful database recovery point.
4. Confirm the rollback API digest is present on the node.
5. Run the read-only database gate:

   ```powershell
   Get-Content .\preflight.sql -Raw |
     ssh shopixy-vps "kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -v ON_ERROR_STOP=1 -U postgres -d app"
   ```

Expected: predecessor `1`, no case-insensitive slug duplicates, and canonical
tenant readiness `1` with slug `ahram`, status `1`, active `true`, and storefront
status `live`.

## 2. Verify immutable artifacts

Verify the sealed evidence manifest and image archive before transfer:

```powershell
Get-FileHash .\20261111160000_AutomatedTenantProvisioning.sql -Algorithm SHA256
Get-FileHash C:\Users\fadya\AppData\Local\Temp\opencode\shopixy-api-automatic-subdomains-audit-r2-1185031a8355.tar.gz -Algorithm SHA256
```

Expected migration SHA-256:
`b6083cefea5b6e5257dc020a9762d6a20ccc8fab5f3cd3adc8f649b0023489b7`.

Expected image archive SHA-256:
`8d787512f680759954f811a4e0f78d5f051efb01633df158c6f10a6b0512eb52`.

Transfer the archive through the approved release channel, verify its checksum on
the node, then import it without changing a workload:

```bash
gzip -dc shopixy-api-automatic-subdomains-audit-r2-1185031a8355.tar.gz \
  | sudo k3s ctr images import -
sudo k3s ctr images list \
  | grep 'automatic-subdomains-ce7fa5ea422e-r2'
```

Confirm the imported manifest digest is
`sha256:1185031a83553260e30f4acc63726469446341eb6c9e00452e102cd8817444aa`.
Do not patch the deployment unless both the tag and immutable digest resolve
locally; the deployment retains `imagePullPolicy: Never`.

## 3. Install DNS-01 authority and prove staging issuance

Create a dedicated Cloudflare API token restricted to `shopyxi.com` with only
Zone DNS Edit and Zone Read. Deliver it as a regular operator-owned file with
mode `0400` or `0600`; do not paste it into a shell, manifest, or ticket.

On the cluster node:

```bash
./install-cloudflare-token-secret.sh /approved/secret/path/cloudflare-dns01-token
kubectl apply -f 01-cert-manager-cloudflare-issuers.yaml
kubectl apply -f 02a-wildcard-certificate-staging.yaml
kubectl wait -n shopixy-app --for=condition=Ready \
  certificate/shopixy-wildcard-origin-staging-r1 --timeout=10m
```

Inspect the Certificate, Order, and Challenge without printing Secret data. A
successful staging issuance proves zone discovery and DNS edit permission. Then
issue production:

```bash
kubectl apply -f 02-wildcard-certificate.yaml
kubectl wait -n shopixy-app --for=condition=Ready \
  certificate/shopixy-wildcard-origin-r1 --timeout=10m
kubectl get certificate shopixy-wildcard-origin-r1 -n shopixy-app
```

The production Secret must be `shopixy-wildcard-origin-r1-tls` and contain SANs
`shopyxi.com` and `*.shopyxi.com`. After production readiness is proven, delete
only the staging Certificate and its staging TLS Secret. Keep the token Secret
for renewal.

## 4. Apply the additive database migration

Keep public registration closed. Apply the checked SQL once; it is idempotent and
transactional:

```powershell
Get-Content .\20261111160000_AutomatedTenantProvisioning.sql -Raw |
  ssh shopixy-vps "kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- psql -X -v ON_ERROR_STOP=1 -U postgres -d app"
```

Apply `12-provisioning-runtime-grants.sql` through the same reviewed psql path.
Production has no default table grants. This separate transaction grants only
SELECT/INSERT/UPDATE on the new operation table to the existing restricted
`shopixy_api_runtime` role; it does not grant DELETE, TRUNCATE, schema ownership,
default privileges or authority on existing tables. The original migration SQL
and its expected checksum are unchanged.

Apply `13-schema-version-authority.sql`. The production startup admission gate
uses `SystemSchemaVersions.postgres` as a separate read-only authority and fails
closed unless it equals the latest applied EF migration. This guarded transaction
advances exactly the expected predecessor metadata row to this release ID, and is
idempotent on repeat. It does not modify tenant, owner, StoreConfig, financial or
provisioning-operation data.

Apply `09-api-edge-monitoring-networkpolicy.yaml` before post-rollout monitoring
acceptance. The API Service retains port 8080, but its target is the reviewed
client-IP edge sidecar on pod port 18080. Existing monitoring policies admitted
only pod port 8080 and therefore could not scrape after that target change. This
additive ingress/egress policy pair admits only the exact Prometheus workload to
the exact API pod labels on 18080; it does not broaden public or other
cross-namespace API access.

If the pre-DNS direct-origin bootstrap probe exposes r2's unknown-host `400`
contract, import and verify the independently sealed r3 successor, then apply
`14-api-r3-deployment-patch.json`. This changes only the API image digest; the
singleton/Recreate strategy, wildcard edge ConfigMap, sidecar, requests and
accepted memory-limit policy remain unchanged. If readiness fails, apply
`15-rollback-api-r3-to-r2-patch.json`; never run migration Down.

Apply `16-assurance-no-store-patch.json` if public acceptance proves the two
existing assurance routes deny correctly but omit no-store on their short-circuit
403 responses. This adds only the already-reviewed `app82-api-no-store-r4`
middleware ahead of `app82-internal-only-r4`; assurance hosts, services, TLS and
denial authority remain unchanged. `17-rollback-assurance-no-store-patch.json`
removes only that addition.

Apply `18-close-registration-deployment-patch.json` if the final defense-in-depth
probe shows public registration is edge-denied but the API configuration still
enables it internally. The patch adds only the explicit production override
`PlatformSecurity__RegistrationEnabled=false`; image, sidecar, resources and
singleton/Recreate policy are tested and unchanged. Roll back only that override
with `19-rollback-close-registration-deployment-patch.json` if readiness fails.

Run `postflight.sql`. All assertion columns must be `1`, including the runtime
table-authority gate. The migration is
backward-compatible with the accepted rollback image; do not run `Down` during
an application rollback because that would discard durable provisioning history.

## 5. Apply immutable configuration and monitoring

Server-side dry-run every object immediately before apply:

```bash
kubectl apply --dry-run=server -f 03-storefront-nginx-configmap.yaml
kubectl apply --dry-run=server -f 04-api-edge-nginx-configmap.yaml
kubectl apply --dry-run=server -f 08-monitoring-combined.yaml
kubectl apply -f 03-storefront-nginx-configmap.yaml
kubectl apply -f 04-api-edge-nginx-configmap.yaml
kubectl apply -f 08-monitoring-combined.yaml
```

The combined immutable ConfigMap preserves every reviewed file from the existing
`phase4j-alerting` authority and adds `automatic-subdomains.yaml`. This avoids an
invalid nested subPath mount under Grafana's existing alerting-directory mount.
Patch Grafana with `08-monitoring-deployment-patch.json`, wait for rollout, and
confirm the four `shopixy-*` alert UIDs load without provisioning errors.

## 6. Roll out API and storefront

Apply only guarded JSON patches. Every patch tests replicas, strategy, accepted
image/config, and volume position before replacing anything.

```bash
kubectl patch deployment shopixy-api -n shopixy-app --type=json \
  --patch-file=07-api-edge-deployment-patch.json --dry-run=server
kubectl patch deployment shopixy-api -n shopixy-app --type=json \
  --patch-file=07-api-edge-deployment-patch.json
kubectl rollout status deployment/shopixy-api -n shopixy-app --timeout=300s

kubectl patch deployment shopixy-storefront -n shopixy-app --type=json \
  --patch-file=06-storefront-deployment-patch.json --dry-run=server
kubectl patch deployment shopixy-storefront -n shopixy-app --type=json \
  --patch-file=06-storefront-deployment-patch.json
kubectl rollout status deployment/shopixy-storefront -n shopixy-app --timeout=300s
```

Confirm one Ready replica, `Recreate`, successor digest, non-root UID, health,
exact platform routes, assurance restrictions, and no-store API responses.

## 7. Install wildcard routes, then DNS

```bash
kubectl apply --dry-run=server -f 05-traefik-wildcard-routes.yaml
kubectl apply -f 05-traefik-wildcard-routes.yaml
```

Before DNS, use direct-origin probes to verify one-label routing and the wildcard
origin certificate.

In Cloudflare DNS create exactly one wildcard record:

| Type | Name | Content | Proxy | TTL |
|---|---|---|---|---|
| A | `*` | `207.231.111.200` | Proxied | Auto |

Do not create tenant-specific DNS records. Exact records continue to take DNS
precedence and must not be edited.

Run `acceptance-probes.sh`. It must pass canonical publication, unknown-tenant
denial, private registration/health, assurance restrictions, no-store behavior,
direct-origin TLS, malformed host, and multi-label host checks.

## 8. Retire the canonical tenant's legacy edge objects

Only after wildcard acceptance passes:

1. Patch `controlled-qa-registration-private-r1` with
   `11-retire-ahram-private-route-patch.json`; wildcard priority `900` continues
   the same private-path restriction for `ahram`.
2. Delete only `controlled-qa-ahram-http-r1` and
   `controlled-qa-ahram-https-r1`.
3. Delete only Certificate `controlled-qa-ahram-r1`; retain its TLS Secret until
   post-retirement acceptance passes.
4. In Cloudflare, delete only the exact `ahram` A record. The wildcard record now
   answers it.
5. Rerun `acceptance-probes.sh`, then delete the obsolete
   `controlled-qa-ahram-r1-tls` Secret.

This is the proof that `ahram.shopyxi.com` uses the shared wildcard path. Never
delete the tenant, StoreConfig, owner, orders, financial records, or audit data.

## Rollback

Rollback is ordered to restore reachability before removing shared facilities:

1. Recreate the exact `ahram` proxied A record to `207.231.111.200` if it was
   retired.
2. Apply `11-rollback-ahram-legacy-resources.yaml` and wait for its Certificate;
   apply `11-rollback-ahram-private-route-patch.json` if retirement was applied.
3. Remove the wildcard Cloudflare A record to stop admitting new wildcard hosts.
4. Delete the two `shopixy-tenant-wildcard-*-r1` IngressRoutes.
5. Apply `09-rollback-storefront-deployment-patch.json`; wait for rollout.
6. Apply `10-rollback-api-edge-deployment-patch.json`; wait for rollout and
   confirm accepted digest `5efeb704...`.
7. Apply `08-rollback-monitoring-deployment-patch.json`, then delete the combined
   `shopixy-alerting-automatic-subdomains-r2` ConfigMap. The original
   `phase4j-alerting` ConfigMap is never edited or deleted. The unused
   `shopixy-automatic-subdomains-alerting-r1` ConfigMap from the preserved first
   mounting attempt may also be deleted after the rollback is Ready.
8. Leave the additive database migration in place. It is safe for the accepted
   app and preserves durable records.
9. After exact-host acceptance passes, remove the wildcard Certificate, issuers,
   and DNS-01 token Secret only if automatic wildcard service is intentionally
   abandoned.

At every stage, preserve one replica, `Recreate`, exact platform routes, mail and
verification DNS, Cloudflare Full (strict), and closed public registration.
