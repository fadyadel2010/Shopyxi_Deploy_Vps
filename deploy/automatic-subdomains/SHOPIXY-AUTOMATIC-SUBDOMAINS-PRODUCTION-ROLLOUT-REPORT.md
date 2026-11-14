# Independent production release audit — deployment stopped

> **Historical original-r1 stop report.** Preserved rather than silently rewriting
> the original failed gates. Current remediation and rollout status is recorded
> in `R2-AUDIT-AND-ROLLOUT.md`; do not use the dated status below for r2 decisions.

Decision: **NO-GO — pre-deployment artifact/provenance gate**.

Audit date: 2026-11-11 UTC. This is an incomplete release audit with an explicit
stop, not production acceptance or a claim that all engineering gates passed.

## Authoritative package

The package is at
`D:\Shopixy-APP82-20261109\successor-assurance-r11-cart-http-contract\source\deploy\automatic-subdomains`.
It is not present under the session's `D:\PointOfSale\deploy` directory. The
isolated package README identifies this source tree as authoritative. No source
files in the unrelated dirty `D:\PointOfSale` working tree were overwritten.

## Verified identities

- All 40 files in the original `SHA256SUMS.txt` match their sealed file hashes.
- Archive exists at the owner-specified Windows path. SHA-256:
  `7bd6e56479354ccfcab64aa2930ed692652a27ffc07ec0927f13b520fa53a40a`.
- OCI manifest SHA-256:
  `e29e1b4112401420dc4ae57eac5d6a9f038ee21027f40b178ed3e880a637c92c`.
  Its config and every referenced layer were hashed independently.
- Required archive tag exists:
  `shopixy/api:automatic-subdomains-783c9b0af224-r1`.
- Final image API assembly SHA-256:
  `783c9b0af22409b11d3d7fff3664d2b413b9c9eca0d98918172322971f7c8421`.
- Image config specifies `1654:1654`, Production, the expected entrypoint,
  retained healthcheck, and .NET/ASP.NET version environment metadata `10.0.15`.
  This audit has not independently rerun the container runtime smoke test.
- Reviewed SQL file matches expected SHA-256
  `b6083cefea5b6e5257dc020a9762d6a20ccc8fab5f3cd3adc8f649b0023489b7`.

## Stop discrepancy: sealed assembly inventory does not describe this image

`successor-assembly-sha256.txt` lists 82 DLLs. Of these, 64 match the final image,
including the API, Core and Infrastructure assemblies. Eighteen listed DLLs are
absent from the final image; no actual file hash can be verified for them:

1. `Humanizer.dll`
2. `Microsoft.Build.Framework.dll`
3. `Microsoft.CodeAnalysis.CSharp.dll`
4. `Microsoft.CodeAnalysis.CSharp.Workspaces.dll`
5. `Microsoft.CodeAnalysis.dll`
6. `Microsoft.CodeAnalysis.ExternalAccess.RazorCompiler.dll`
7. `Microsoft.CodeAnalysis.Workspaces.dll`
8. `Microsoft.CodeAnalysis.Workspaces.MSBuild.dll`
9. `Microsoft.EntityFrameworkCore.Design.dll`
10. `Microsoft.Extensions.DependencyModel.dll`
11. `Microsoft.VisualStudio.SolutionPersistence.dll`
12. `Mono.TextTemplating.dll`
13. `System.CodeDom.dll`
14. `System.Composition.AttributedModel.dll`
15. `System.Composition.Convention.dll`
16. `System.Composition.Hosting.dll`
17. `System.Composition.Runtime.dll`
18. `System.Composition.TypedParts.dll`

The independent inventory processes image layers in manifest order and handles
the application opaque-directory whiteout. A separate enumeration of the final
application layer confirms there are no application hardlinks or symlinks that
could explain the missing entries.

This may be a build-time versus runtime inventory/provenance documentation defect,
not a missing runtime dependency. It is **not** grounds to invent replacement
hashes, silently narrow the seal, substitute another image, or claim complete
source-to-image provenance. Preserve the original image, inventory and seal.
Reconcile the inventory against the exact build publication and reproducible
source evidence before promotion. Any resulting binary change requires a new
immutable candidate and seal.

Receipts: `independent-audit-20261111/artifact-identity.json`,
`assembly-identity.json`, `final-app-layer-members.json`, and
`original-SHA256SUMS.txt`.

## Live baseline (read-only)

- Kubernetes client/server: `v1.36.5+k3s1`.
- API still specifies accepted digest
  `5efeb7042bcaf6ea223f3df06c7983862e073787e78634fb996ffbf9c4cf266e`;
  desired/ready replicas `1/1`, strategy `Recreate`.
- Storefront still specifies
  `8c59cbb19d41f94906c6dd2d314a43216bb98f7f71cfafd39351b6b38b075c36`;
  desired/ready replicas `1/1`, strategy `Recreate`.
- Web digest remains `f35e1b9cd384244049d0008af9476ee3d6fb974ca7fbb8e7e1f9fe70c11b0a42`.
- CommerceHub digest remains `dd2bf0247adca5be3c48b29adaadd3310c991fc5d53d392ef26ad174ff3d80a8`.
- Exact read-only `preflight.sql` passed predecessor, no case-insensitive slug
  duplicates, and canonical Ahram publication assertions, all `1`.
- Canonical Ahram UUID remains `cdbe20e0-f84a-4416-b20c-044e6bcf04d0`,
  slug `ahram`, status `1`, active, not deleted, Storefront `live`.
- CNPG backup `daily-0200-utc-20261111020000` reports `completed`, stopped
  `2026-11-11T02:01:37Z`. Cluster reports Ready, continuous archiving working,
  and last backup successful. Object-store availability, WAL continuity and an
  adequate immediate pre-migration recovery point are **not independently proven**.
- `kubectl top nodes` and `kubectl top pods -A` fail with
  `Metrics API not available`. Resource headroom is **not verified**; do not
  infer sufficient headroom from Ready pods or modify resource policy.

Additional non-secret snapshots cover deployments/resources/config references,
routes, certificate/issuer objects, Secret metadata only, services, endpoint
slices, NetworkPolicies, ServiceAccounts, Traefik HelmChartConfig, nodes, CNPG
backup metadata and local image inventory. These are snapshots, not a completed
semantic/security acceptance of every captured object.

## Operator-owned DNS-01 handoff

The named account was verified by direct authenticated SSH:
`shopixy-admin`, UID/GID `1000:1000`, passwd HOME `/home/shopixy-admin`.

Prepared exact path:
`/home/shopixy-admin/.config/shopixy/secrets/cloudflare-dns01-token`.

- `.config`, `shopixy`, `secrets`: `0700`, operator-owned.
- File: `0600`, operator-owned, regular, single hardlink; created empty using
  exclusive creation. Existing files are never truncated or replaced.
- Directory traversal rejects symlinks via directory descriptors and
  `O_NOFOLLOW`; file metadata is checked without opening credential contents.
- No token was read, installed, copied into Kubernetes, or included in evidence.
- Owner asked whether a Windows file can instead be used. Answer: yes, after
  the exact path is supplied and Windows ACLs/reparse-point protections are
  verified, followed by a non-disclosing SSH transfer into this operator file.
  Do not send token values through chat. No Windows credential file has yet
  been authorized by exact path or accessed.

## Review classifications at stop

| Component | Classification | Scope / reason |
|---|---|---|
| Original package file seal | PASS | 40/40 hashes match |
| Archive/manifest/config/layer identity | PASS | Exact expected archive and OCI digests |
| API assembly/tag/non-root config | PASS | Exact expected API hash and tag; config identity only |
| Complete assembly inventory | DEFECT | 18 sealed entries absent from final image |
| Source-to-image provenance | BLOCKED | Reconcile publication inventory and reproducible build evidence |
| SQL file identity / read-only preflight | PASS | Exact checksum, predecessor and canonical tenant assertions |
| Migration execution / postflight | NOT VERIFIED | Not executed in production |
| Existing deployment contract snapshot | PASS | Accepted images; API/Storefront 1/1 Recreate |
| Recovery point adequacy | NOT VERIFIED | Successful backup metadata is not restore/WAL proof |
| Resource headroom | BLOCKED | Metrics API unavailable; alternative metrics not yet checked |
| Protected VPS credential-file preparation | PASS | Metadata-only receipt; empty file, no Secret installed |
| DNS-01 token scope / Secret / RBAC | NOT VERIFIED | No credential supplied or installed |
| Engineering test receipts / runtime smoke | NOT VERIFIED | Full receipt/provenance audit not completed at stop |
| Fresh successor Trivy scan | NOT VERIFIED | Must be refreshed immediately before eventual deployment |
| NGINX / Traefik / Grafana / rollback proof | NOT VERIFIED | Reviewed portions only; independent gates incomplete |
| Live Cloudflare / wildcard TLS / Ahram retirement | NOT VERIFIED | No rollout or retirement performed |
| Full production acceptance | BLOCKED | Identity/provenance stop and remaining review gates |

## Mutation and rollback status

No image was transferred/imported into production. No deployment, ConfigMap,
Kubernetes Secret, issuer/certificate, route, DNS record or database schema/data
was changed. No tenant was created. No general registration was opened. No
resource policy, Cloudflare mode, nameserver or DNSSEC changes were made.

The only authorized remote filesystem preparation was the empty protected
operator credential path. Local audit receipts were added without replacing the
original sealed package files. No application rollback is necessary.

The accepted `10.0.14` image remains running because promotion stopped. The
existing package discloses four HIGH findings: `CVE-2026-114146`,
`CVE-2026-114147` twice, and `CVE-2026-114149`. This audit does not recast that
image as a currently clean release. Promotion to a verified clean successor is
still outstanding.

## Safest next action

Reconcile and prove the exact image's assembly/publication inventory, then resume
the unfinished independent review. Do not execute RUNBOOK production stages
until all gates pass. The Cloudflare token may be prepared in parallel, but its
presence does not clear the artifact/provenance gate. After the review and token
gates pass, the owner's rollout authorization remains in force; no repeated
general confirmation is needed.
