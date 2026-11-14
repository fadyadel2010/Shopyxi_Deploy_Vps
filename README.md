# Shopyxi_Deploy_Vps

Private Shopixy source and Production recovery reference, established 14 November 2026.
This is **not** a database/secret backup or an automatically deployable cluster bundle.

## Start here

- [Verified Production baseline](docs/PRODUCTION-BASELINE.md): live images, runtime identities, acceptance boundaries and source provenance.
- [Recovery guide](docs/RECOVERY.md): existing procedures, prerequisites and explicit unverified recovery gaps.
- [Preserved local work](docs/LOCAL-WORK.md): the Windows working copy intentionally differs from committed release inputs. **Do not reset, restore, clean, or indiscriminately re-stage it.**

## Architecture and layout

| Location | Purpose |
|---|---|
| `PointOfSale.API/` | ASP.NET Core API, HTTP/security boundaries and application services |
| `PointOfSale.Core/` | Domain entities, contracts and shared rules |
| `PointOfSale.Infrastructure/` | EF Core persistence, accounting, messaging and provider integration |
| `PointOfSale.Infrastructure/Migrations/` | EF migrations, designers and model snapshot; required source, **not disposable output** |
| `PointOfSale.Web/` | React/Vite Admin and POS frontend |
| `PointOfSale.Storefront/` | React/TypeScript public Storefront and themes |
| `PointOfSale.CommerceHub/` | Separate React/TypeScript CommerceHub application |
| `PointOfSale.WhatsApp.Baileys/` | Node/TypeScript WhatsApp transport service |
| `PointOfSale.Tests/`, `audit-tests/` | Retained test source and focused runners; not a new QA certification |
| `DB/` | Required SQL migration/authority scripts, not database contents |
| `deploy/production-baseline/` | Sanitized live workload observations, image identities and deployment comparison |
| `deploy/automatic-subdomains/` | Historical reviewed rollout assets/runbook, not the current image selection |
| `deploy/app43b/` | Historical **OFFLINE-NOT-FOR-APPLY** templates and recovery preparation |
| `docs/`, `.config/`, `theme-authority/` | Operational/architecture material, build compatibility script and theme contract |

The API integrates with existing PostgreSQL, MongoDB, Redis/Sentinel, RabbitMQ and OpenSearch services. Their data, credentials, private certificates and cluster administration are deliberately outside this repository. The live cluster also has an existing R2 egress workload and API client-IP sidecar; see the baseline rather than assuming only three containers exist.

## Local prerequisites and build reference

- Git and authorized access to this exact private repository.
- .NET SDK **10.0.404**, as pinned in `global.json`; project targets are `net10.0`. Accepted image reports record .NET/ASP.NET runtime 10.0.15.
- Node.js **20.19 or later** (Baileys engine contract) and npm. Retain the checked-in npm/NuGet manifests and lockfiles. The existing offline caches and installed dependencies were preserved.
- Existing release packaging additionally uses the established Linux/WSL, Docker, Trivy and private toolchain configuration. They are **not** bundled or reinstalled here.
- Supply development-only secrets through the existing private configuration mechanisms. `appsettings*.json` contain empty credential slots; empty slots do not mean Production runs without secrets.

Existing release evidence documents the following local commands (reference only; not rerun during repository recovery):

```text
dotnet build validation/general-ledger-final-fix/GeneralLedgerRegression.csproj --no-incremental -m:1 -p:UseSharedCompilation=false
dotnet restore PointOfSale.API/PointOfSale.API.csproj --runtime linux-musl-x64 -p:NuGetLockFilePath=packages.production.lock.json --locked-mode
dotnet publish PointOfSale.API/PointOfSale.API.csproj -c Release -r linux-musl-x64 --self-contained false --no-restore -m:1 -p:UseSharedCompilation=false
```

Each frontend's `package.json` contains its actual `dev`, `build` and test commands; Baileys has its own TypeScript build. Do not run these against Production or assume the preserved Windows working copy equals the Git release snapshot. Use a separate checkout for recovery/build work.

## Production and acceptance

Consult `docs/PRODUCTION-BASELINE.md` first. The 14 November General Ledger API successor supersedes the earlier API reference in the Storefront report; Admin and Storefront images did not change in that successor.

Retained reports at their original repository paths are **sanitized recovery editions**:

- `validation/general-ledger-final-fix/FINAL-REPORT.md`
- `validation/storefront-20261114/FINAL-REPORT.md`

Their full original evidence remains locally, unchanged. Browser sessions, raw responses, SQL business records, screenshots, private user/payment data and historical bulk artifacts are not uploaded.

General Ledger was accepted in the actual headed Production UI. Stock Count received a narrow tested/shipped error-message correction, **not** a new live mutating workflow PASS. The Storefront report's 36 screens were read/safe-control checks, **not** blanket end-to-end certification. Real carrier dispatch/delivery, real money/wallet approval, card/Paymob, provider activation, mass messaging and every management mutation remain outside the accepted claims.

## Source versus generated material

Source, tests, migrations/designers, static assets, scripts and lockfiles belong in Git. `bin`, `obj`, `dist`, `node_modules`, image archives, caches, raw validation output, actual `.env` overrides, backups, keys and kubeconfigs do not. Ignoring a file does not authorize deleting it. Do not remove the separate Load-Test workspace or any unique local work.

This recovery task made no application functionality or Production changes. Existing deployment scripts are historical, gated procedures with machine-specific paths and predecessor checks, **not instructions to redeploy automatically**.
