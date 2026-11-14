# Production baseline — 14 November 2026

## Live verification, not an assumed report value

Read-only SSH verification of `shopixy-vps`, namespace `shopixy-app`, completed **2026-11-14T12:32:33Z**. Kubernetes GET observations and CRI `inspecti` mappings are retained in `deploy/production-baseline/live-observation.json`. No Secret resources, environment credentials, databases, PVC contents, kubeconfigs, private certificates or private backup contents were exported. No workload or infrastructure was changed.

| Workload / container | Verified deployed image |
|---|---|
| API / api | `docker.io/shopixy/api@sha256:d3f10ae2cc93eb1fdddb7bd89c55726ca3f6d0ea1e4dbbf7825f47aaa19dbcb9` |
| API / client-IP edge | `docker.io/shopixy/web@sha256:f35e1b9cd384244049d0008af9476ee3d6fb974ca7fbb8e7e1f9fe70c11b0a42` |
| Admin Web | `docker.io/shopixy/web@sha256:756879210827a50c84d8d42da66e2d6e2235907e1af33930a2a450cab0f46682` |
| Storefront | `docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df` |
| WhatsApp Baileys | `docker.io/shopixy/baileys@sha256:fb31e748e0262895030f609b6cb2edd50ea2a1703d5d5e6010d01c6e1988d3c4` |
| CommerceHub | `docker.io/shopixy/commercehub@sha256:dd2bf0247adca5be3c48b29adaadd3310c991fc5d53d392ef26ad174ff3d80a8` |
| R2 egress | `docker.io/shopixy/baileys@sha256:a396a42d133883cfe1a1a6c3cf1ab73f10920c3d3931c7c8cb106da601f48291` |

All six Deployments had desired/ready/available replicas 1/1/1 and matching generation/observed-generation: API 44, Admin 13, Storefront 10, Baileys 7, CommerceHub 6, R2 egress 4. Pod/container identities and readiness are in the observation.

**Identity caution:** Kubernetes image references, imported OCI manifest aliases and CRI image/config IDs are not interchangeable strings. For example API CRI ID is `sha256:c091044dfd93dd1e92639486b0deb13fa3ccbc6f5d92fbc1db80c087b808c1ca`; the same CRI entry lists the accepted API reference and imported alias `sha256:c3a8c62a709ac580c736793f7260f1612fc6d1854f28dc0661ab9c9c2c0d0c3d`. Runtime identity was reconciled through that mapping, not by wrongly demanding all three strings match.

## Source provenance

- Main Windows source: `D:\Shopixy-Automatic-Subdomains-Audit-R2-20261111\source`.
- General Ledger `source-index.json` has **2,646 entries**: 2,642 certified current-baseline inputs plus focused test/runner inputs. Initially 2,640 matched D:; four existing API modifications and two absent files were retained as-is locally.
- The Admin release index has **2,001 entries**; initially 1,992 matched D:, with four modified and five absent inputs.
- User explicitly authorized read-only access to the manifest-listed files in the two certified C: release forks. All 15 required alternate inputs were SHA-256 verified. The Git index uses those accepted bytes **without changing any D: application file**.
- Baileys scratch changes plus the **later pairing** source index establish its latest four affected source inputs. The earlier scratch-stage SessionManager hash is superseded, not a new divergence.
- `deploy/production-baseline/accepted-source-sha256.json` records the combined accepted-input hashes; Git blob parity is verified before commit. Other available source/tests are retained as recovery material, not silently claimed to have independent image-to-source certification.
- The four explicitly protected API files are `Controllers/PlatformController.cs`, `DTOs/ExpenseDTOs.cs`, `Program.cs` and `Services/Platform/RateLimitService.cs`, under `PointOfSale.API`. Their original working hashes remain unchanged; see `docs/LOCAL-WORK.md`.

The VPS runs compiled images and is not a substitute source archive. Source absence there was never used as a deletion criterion. This task did not rebuild images or rerun application QA, and does not claim reproducible binaries from a fresh machine.

## Accepted state and limits

The General Ledger final fix is the latest accepted API release: deterministic journal/date/posting order with cumulative displayed balances; focused 20/20 local regressions and headed Production UI acceptance were documented previously. The current source hashes are:

- `PointOfSale.Infrastructure/Accounting/FinancialReportService.cs`: `7e488c8b249240d54c60b812cbc18e29134005114ce22a5507847f13bdaff251`
- `PointOfSale.API/Infrastructure/Mvc/ApiErrorResponseNormalizationFilter.cs`: `319a673b8ec87814359e9a8a53ee173aa48718f0e740ba145946d0efe0da9944`

Stock Count's specific HTTP409 recount guidance is regression/image-verified, **not newly live-mutating/end-to-end accepted**. Storefront's 36 management screens were read/safe-control checks; selected synthetic checkout/manual-shipment/draft and supervised WhatsApp transport evidence does not certify real provider/money/dispatch/publication workflows. No customer or financial row data is needed in this recovery reference.

The sanitized final reports preserve these boundaries. Older rollout/template documents are historical and can contain superseded images or explicitly unverified infrastructure plans. **This baseline and the live observation take precedence for release identity.**

## Recovery limitations

Source is not a complete disaster-recovery backup. Image availability outside the current node/retained archives, registry push/pull availability, secret custody, Data Protection discriminator/ring/decryptors, off-host backup coverage and database/PVC/node-loss restore procedures require their own authorized verification. No such restore was attempted here. The live manifest export is intentionally redacted and **must not be applied verbatim**.
