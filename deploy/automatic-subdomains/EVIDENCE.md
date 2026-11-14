# Validation evidence — 2026-11-11

> **Historical r1 evidence copied for comparison; not r2 acceptance.**
> The claims below describe the original candidate and were not all independently
> verified. For current r2 evidence, identities, review classifications and actual
> production changes, use `R2-AUDIT-AND-ROLLOUT.md` and `audit-results/`.

No production DNS, Secret, Certificate, route, ConfigMap, image, deployment, or
database mutation was performed during this validation. Production access was
used only for read-only inventory, Prometheus queries, database preflight, RBAC
checks, and Kubernetes server-side dry-runs.

## Application and database

- Focused registration/slug/controller suite: **60 passed, 0 failed**.
- PostgreSQL provisioning suite: **5 passed, 0 failed**.
- Complete migration chain applied successfully on disposable PostgreSQL 16.
- Generated production SQL applied twice at the known predecessor: both runs
  committed successfully; migration history, table, and slug index all present.
- EF pending-model check: **no pending model changes**.
- Production read-only preflight: predecessor present, zero case-insensitive slug
  duplicates, canonical tenant `cdbe20e0-f84a-4416-b20c-044e6bcf04d0` is slug
  `ahram`, status `1`, active, not deleted, storefront `live`.

## Successor image

- Immutable reference:
  `shopixy/api@sha256:e29e1b4112401420dc4ae57eac5d6a9f038ee21027f40b178ed3e880a637c92c`.
- API assembly SHA-256:
  `783c9b0af22409b11d3d7fff3664d2b413b9c9eca0d98918172322971f7c8421`.
- Runtime: `Microsoft.AspNetCore.App 10.0.15` and
  `Microsoft.NETCore.App 10.0.15`.
- Runtime identity: UID/GID `1654:1654`; accepted healthcheck retained.
- NuGet vulnerability audit: no vulnerable direct or transitive packages.
- Current Trivy database: successor HIGH/CRITICAL findings **0**. The accepted
  `10.0.14` rollback image now reports four HIGH findings
  (`CVE-2026-114146`, `CVE-2026-114147` twice, and `CVE-2026-114149`), all fixed
  in `10.0.15`; it is retained only for emergency rollback and must not remain
  the steady-state production image.
- Local dependency-backed smoke: PostgreSQL, RabbitMQ, Redis, and MongoDB
  connected; container reported `healthy`; `/health/live` returned `200` with
  `Cache-Control: no-store`.
- Compressed import archive SHA-256:
  `7bd6e56479354ccfcab64aa2930ed692652a27ffc07ec0927f13b520fa53a40a`.

## Edge and monitoring

- Both rendered NGINX configurations pass `nginx -t` in the accepted frontend
  image family.
- Traefik `3.7.13` loaded the exact Go-compatible HostRegexp; a valid one-label
  host selected the test upstream (`502`), while multi-label and two-character
  labels missed the router (`404`).
- Live local NGINX probes:
  - `ahram.shopyxi.com`: `200`;
  - malformed host: `421`;
  - multi-label host: `421`;
  - reserved `api`: `421` at wildcard storefront fallback;
  - assurance label: `421` at wildcard storefront fallback;
  - API edge valid one-label host reached upstream (`502` with no test upstream),
    while malformed and multi-label hosts returned `421`.
- Reserved-label validator proves every canonical `StoreSlug.ReservedLabels`
  entry exists at the storefront edge.
- Grafana 13.3 loaded all four provisioned alert rules (`rules=4`); the production
  datasource UID is `prometheus`.
- All four PromQL expressions returned `status=success` from the live Prometheus
  3.15.1 API. Pre-certificate queries correctly have no series and therefore use
  fail-closed Grafana no-data behavior.

## Live platform compatibility

- Kubernetes client/server: `v1.36.5+k3s1`.
- Traefik: `3.7.13`.
- cert-manager: `1.21.3`.
- The cluster has no Prometheus Operator `PrometheusRule` CRD; monitoring was
  remediated to the installed Grafana file-provisioning contract.
- Server-side dry-run passed for both ClusterIssuers, staging and production
  Certificates, both NGINX ConfigMaps, both Traefik routes, Grafana alert
  ConfigMap, API/storefront/Grafana forward patches, canonical-route retirement,
  and all rollback paths.
- Concatenated forward+rollback JSON patches produced `patched (no change)` for
  API, storefront, Grafana, and the shared private route, proving reversible
  guarded operations against the live object shapes.
- cert-manager controller can read solver Secrets; tested application and default
  service accounts cannot read `cloudflare-dns01-token` in `cert-manager`.

## Operator-controlled rollout gates

The remaining actions require production change authorization rather than more
code remediation: inject the zone-scoped token, prove ACME staging then production
issuance, import the verified image archive, apply the migration and guarded
manifests, create the single proxied wildcard DNS record, run acceptance probes,
and retire the legacy exact `ahram` edge objects in the documented order.
