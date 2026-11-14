# Shopixy automatic tenant subdomains

This release replaces per-tenant edge provisioning with one immutable platform
contract:

- one proxied Cloudflare wildcard `A` record: `* -> 207.231.111.200`;
- one cert-manager DNS-01 certificate for `shopyxi.com` and `*.shopyxi.com`;
- one bounded Traefik one-label wildcard route;
- PostgreSQL `Tenant.Slug`, lifecycle state, and `StoreConfig.StorefrontStatus` as
  the publication authority;
- durable PostgreSQL provisioning operations and outbox-backed events;
- no Cloudflare, DNS, certificate, or HTTP client dependency in per-tenant
  provisioning.

## Provenance

- Isolated audited source:
  `D:\Shopixy-Automatic-Subdomains-Audit-R2-20261111\source`.
- Current production API (final accepted r3):
  `docker.io/shopixy/api@sha256:860d691bb8c7a4dc463d4dfbd99dfe595e963cce3e32e6392d9e3ce8cb3dbc5a`.
- Immediate application rollback (r2):
  `docker.io/shopixy/api@sha256:1185031a83553260e30f4acc63726469446341eb6c9e00452e102cd8817444aa`.
- Emergency predecessor retained locally:
  `docker.io/shopixy/api@sha256:5efeb7042bcaf6ea223f3df06c7983862e073787e78634fb996ffbf9c4cf266e`.
- Current API assembly SHA-256:
  `5c2cc1226a8e51d68e195062a17aae47b896b015fe40474fce8c7bacd2d8feba`.
- Runtime: ASP.NET Core and .NET `10.0.15`; SDK `10.0.404`.
- Target cluster: Kubernetes `v1.36.5+k3s1`, Traefik `3.7.13`, cert-manager
  `1.21.3`.

The original r1 package remains preserved but is not an approved fallback. r3
keeps the reviewed OS, UID `1654:1654`, healthcheck, entrypoint, and one-replica
`Recreate` contract. Its archive, every OCI blob, all 64 published DLLs, and
2,603 selected source inputs were independently verified.

## Routing and isolation

The wildcard grammar accepts exactly one DNS label of 3-63 lowercase ASCII
letters, digits, or interior hyphens. Multi-label, malformed, and reserved hosts
do not reach a tenant storefront. Exact platform routes retain higher priority.
Health, metrics, and registration paths retain internal-only admission, and
registration is also explicitly disabled by API configuration.

The edge performs syntax admission only. The API resolves the canonical slug and
enforces tenant lifecycle and publication state from PostgreSQL. An arbitrary
syntactically valid hostname therefore does not publish an unknown, suspended,
deleted, inactive, or unpublished tenant.

The canonical acceptance tenant is:

- host: `ahram.shopyxi.com`;
- tenant ID: `cdbe20e0-f84a-4416-b20c-044e6bcf04d0`.

## Secret boundary

The Cloudflare token is stored only as `cloudflare-dns01-token` in namespace
`cert-manager`. It must be scoped to zone `shopyxi.com` with only:

- Zone / DNS / Edit;
- Zone / Zone / Read.

It is not copied to application namespaces, manifests, logs, evidence, or image
layers. `install-cloudflare-token-secret.sh` rejects loose permissions,
whitespace, symlinks, and unexpected application service-account access.

Production rollout and post-Ahram-retirement acceptance are PASS. See
`R2-AUDIT-AND-ROLLOUT.md` for final receipts and `RUNBOOK.md` for the staged
rollout and rollback sequence.
