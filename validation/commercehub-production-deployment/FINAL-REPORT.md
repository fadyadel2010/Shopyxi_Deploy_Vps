# CommerceHub Production Deployment and Merchant Publishing Acceptance

Report date: 2026-11-17. Execution timestamps below are UTC on 2026-11-16.

## A. Source Verification

| Item | Verified result |
|---|---|
| Repository | `https://github.com/fadyadel2010/Shopyxi_Deploy_Vps.git` |
| Branch | `commercehub/merchant-product-publishing-ux-pricing-20261116` |
| Authorized and deployed application commit | `4fb2beb41cb704d8b4e9d9ee383afc34d8904594` |
| Accepted base | `cf5892ed379261d7a1d08ba3c622d8f2dbda4a80` |
| Committed implementation report read | `validation/commercehub-merchant-product-publishing/FINAL-REPORT.md` |
| Source working tree at build | Clean |
| Remote verification | Feature branch resolved to the authorized commit; `main` remained at the accepted base |
| Source identity | Git tree seal `c960752d300ac9c03e044ae1ca8f553a9e8fd53f283cceb0d141289083095f44` |
| Runtime parity | Both image labels identify the authorized commit; running API/Core/Infrastructure DLL hashes match the release publish output |

The accepted implementation was not recreated or modified. This delivery adds documentation/evidence only; it does not change the deployed application commit. No migration or deployment-configuration change was required.

Evidence: `source-verification.json`, `production-artifact-verification.json`, `production-postdeploy-verification.json`.

## B. Production Baseline

Actual live inspection used SSH alias `shopixy-vps`, existing single-node k3s, namespace `shopixy-app`. Historical recovery-image references were not assumed current.

| Component | Actual predecessor immutable image | Generation | Readiness |
|---|---|---:|---|
| API | `docker.io/shopixy/api@sha256:c384ee860fcbc9aa1522cd7ffa6d76da320009772fa016c615ddb9037b65187e` | 63 | 1/1; API and edge containers Ready |
| Admin Web | `docker.io/shopixy/web@sha256:cadd2cb3a44fa6bff972652b0d94c1677c6238fbcae5240e69afcd2fed84452a` | 21 | 1/1 Ready |
| Storefront | `docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df` | 10 | 1/1 Ready |

- The predecessor was the accepted merchant-onboarding release contained in the target commit's ancestry.
- Existing strategy: `Recreate`, one replica, `imagePullPolicy: Never`.
- Production Admin URL: `https://admin.shopyxi.com/`.
- Production API URL: `https://api.shopyxi.com/`. Public health access is intentionally denied at the edge; internal readiness was checked through the existing service.
- Admin returned HTTP 200 before deployment.
- Capacity before release: 8 CPUs, approximately 39 GiB RAM with 31 GiB available, 162 GiB disk available. Metrics API was unavailable; no `kubectl top` capacity claim is made.
- The baseline aggregate health JSON was not retained; no claim is made that its status string was Healthy.

Evidence: `production-deployment.json` contains sanitized before/after identities and deployment-spec hashes.

## C. Deployment Results

### Build and artifact gates

| Gate | Result |
|---|---|
| API Release publish | PASS — .NET SDK 10.0.404, `linux-musl-x64`, framework-dependent |
| Locked restore | PASS — existing `packages.production.lock.json`, existing package cache |
| Admin Web production build | PASS — 5,096 modules, 70 output files |
| API application-byte parity | PASS — API/Core/Infrastructure DLL/PDB overlay matches publish output |
| API dependency parity | PASS — publish dependencies match retained runtime dependencies |
| API unrelated image-file parity | PASS — no unrelated `/app` file changes |
| API startup smoke | PASS — exact candidate started successfully against disposable services; not used as Production acceptance |
| Non-root runtime | PASS — API UID 1654, Web UID 101 |
| Web output parity / nginx syntax | PASS |
| Trivy HIGH/CRITICAL | PASS — zero findings in both images, cached-database offline scan |
| Archive transfer integrity | PASS — local and remote SHA-256 matched |

API archive SHA-256: `cbeae7707dae856b767f01ea2e402211cc5d09e7f8bb00d6cbd1f3a17d361bf7`.

Web archive SHA-256: `d33864562a46bf08fced7f76afc0c11d0188688b775f6f33912d16a372dd72fd`.

### Actual artifact delivery

Registry pulls for the digest-form parent references were denied. No successful registry push is claimed. The exact retained local parents were verified against the live predecessor image IDs and used through their existing local aliases without substitution.

Candidates were transferred by SSH/SCP to the existing VPS, SHA-256 verified, and imported into its existing k3s containerd. Immutable digest references were used with the retained `Never` pull policy.

The guarded rollout tested deployment UID, resource version, previous image, and full spec identity, then changed only the primary application image. API edge, mounts, environment, configuration references, strategy, replicas, services, ingress, secrets, and Storefront were not changed.

| Component | Deployed immutable image | Generation | Rollout |
|---|---|---:|---|
| API | `docker.io/shopixy/api@sha256:cb900c183069f94311142ad9c5a16fa23266611d3c29f674bae64040189ac307` | 64 | PASS; 1/1 Ready, 0 restarts |
| Admin Web | `docker.io/shopixy/web@sha256:e95bbdf5a645495003a3f88a580148238d738b6b1fd0ee345194d9e271de02a8` | 22 | PASS; 1/1 Ready, 0 restarts |
| Storefront | Predecessor digest unchanged | 10 | Unchanged; same Pod and spec |

Rollout: `18:49:23Z`–`18:49:48Z`. The retained `Recreate` strategy can interrupt availability; no zero-downtime claim is made.

### Post-deployment health

- Admin HTTP 200 and actual authenticated Production access: PASS.
- Internal API `/health/ready`: HTTP 200.
- PostgreSQL, Redis, MongoDB, Elasticsearch, RabbitMQ, R2, DNS, business outboxes, identity authority, payments, and certificate renewal checks: Healthy.
- Aggregate payload: **Degraded**, specifically `certificate_provider` and `caddy_edge`.
- Retained `/app/appsettings.json` has both adapters disabled; `/app/appsettings.Production.json` does not override those flags. Their environment enable flags were absent. The relevant health-check code reports disabled adapters as Degraded.
- No infrastructure or health-contract setting was changed to manufacture a Healthy response.
- Therefore the literal aggregate-Healthy requirement is **BLOCKED**, despite successful serving, readiness, and merchant operations.

Evidence: `production-deployment.json`, `production-artifact-verification.json`, `production-postdeploy-verification.json`, `production-health-adapter-flags.json`.

## D. Global Commission Rule

Before mutation, SQL and the actual headed Super Admin screen both showed zero commission rules.

The user completed Production Super Admin authentication personally. The supported Commission UI created the rule, exercising the existing administrative service, audit path, SQL persistence, and cache invalidation. No direct SQL business write, hardcoded rate, token injection, frontend mock, or fallback was used.

| Field | Actual value |
|---|---|
| Rule ID | `98eca7ca-414a-442f-bdf6-fb320cfb4de4` |
| Scope | Global (`ScopeType=0`) |
| Scope ID | SQL NULL |
| Rate | `0.100000` = 10% |
| Active / deleted | Active / not deleted |
| Priority | 0 |
| Created | `2026-11-16T21:56:39.580058Z` |
| Active category/vendor overrides | 0 / 0 |

Existing resolver precedence remains Vendor → Category → Global. With exactly one active global rule and no scoped overrides, every merchant resolves the requested 10%; priority does not invent a new hierarchy.

Actual server resolver coverage: **55/55 HTTP 200 results**, comprising all four currently listed merchants plus unspecified merchant context, across ten categories plus unspecified category context. Every result returned rate `0.1`, the same rule ID, source `Global`, and `usedLegacyFallback=false`.

A post-create evidence-script check failed after the successful mutation. Read-only UI/SQL checks confirmed the persisted rule before any retry; no duplicate create was attempted.

Evidence: `production-global-commission-10-percent.json`, `production-global-commission-10-percent.png`, `production-sql-authority-readback.json`.

## E. Commission Calculation

Production product: `QA-ERP-20261112-TRACKED`.

- Product ID: `b2ec52fb-d600-f1ed-dc23-2fcdc09e61b8`.
- Variant ID: `81069782-7bda-4e35-bf24-3e13424f06f2`.
- Merchant role: Owner; tenant `cdbe20e0-f84a-4416-b20c-044e6bcf04d0`.

| Scenario | Source price | CommerceHub price | Commission | Deduction | Merchant net |
|---|---:|---:|---:|---:|---:|
| Source-price preview | EGP 10.00 | EGP 10.00 | 10% | EGP 1.00 | EGP 9.00 |
| Custom-price preview and persisted listing | EGP 10.00 | EGP 12.00 | 10% | EGP 1.20 | EGP 10.80 |

- Commission base returned by server: `Subtotal`; currency EGP.
- English source/custom and Arabic custom-price screens displayed the expected API amounts using their respective number/currency formatting. Arabic source-price API resolution passed, but its screenshot captured the UI still resolving; completed source-mode numeric rendering in Arabic is not certified.
- Fresh requests, language-specific page loads, and readbacks were used; cached isolated responses were not reused.
- English publication POST and Arabic update PUT both submitted `expectedCommissionRuleId` matching the global rule and returned HTTP 200.
- The deployed handlers re-resolve and validate commission before the write; post-publication SQL and browser readback confirmed the same active rule and listing.
- Listings do not persist a financial order commission snapshot. No such snapshot is claimed; historical order data was not rewritten.

Evidence: `production-en-acceptance.json`, `production-ar-acceptance.json`, source/custom-price screenshots.

## F. Browser UI Acceptance

All acceptance below used **actual headed Chrome against Production**, `https://admin.shopyxi.com/admin/commercehub`, with the user's genuine Owner session. No isolated fixture login, session-response rewrite, bearer injection, or mocked Production API response was used.

### Explicitly authorized publication prerequisite

Initially the merchant's only product had `isOnlineVisible=false`; the real application correctly blocked publication. The user separately selected **“Enable this QA product”** after being warned that this source-product setting could expose it on Storefront.

The existing Products UI changed online visibility to true and saved successfully in place. A harness wait incorrectly expected navigation to `/products`; a read-only GET confirmed success, and no duplicate save was performed. Source price remained EGP 10.00, effective Storefront price EGP 10.00, and stock zero. No price, inventory, or historical financial change was requested.

This is an explicitly authorized product-visibility change, not a Storefront application deployment.

### English / LTR

| Check | Outcome |
|---|---|
| Product selection | PASS |
| Parent/child cascade | PASS — Electronics exposes TVs, Mobile Phones, Laptops; parent without final child blocks update |
| Dynamic fields | PASS — TVs renders Brand/select, Screen Size/number, Resolution/select |
| Required-field validation | PASS — `"Brand" is required.`; zero write requests |
| Final category | PASS — Home & Garden → Kitchen, valid active child |
| Attribute honesty | PASS — generic QA listing used an existing leaf with no required attributes; no branded TV facts invented |
| Source/custom modes | PASS — EGP 10.00/12.00 and correct 10% amounts |
| Publish state | PASS — blocked before prerequisites; enabled with valid authority/category/price |
| Initial publication | PASS — POST `/api/admin/commercehub/publish`, HTTP 200 |
| Persistence | PASS — API GET, fresh reload, and SQL readback |
| Overflow / workflow errors | PASS — no horizontal overflow, console errors, or failed workflow API requests during captured pass |

Listing ID: `ff8cdce3-ad88-4467-b027-cdde5812e84d`; published at `22:04:38Z`.

Evidence:

- `production-en-acceptance.json`
- `production-en-dynamic-attributes.png`
- `production-en-required-attribute-validation.png`
- `production-en-source-price.png`
- `production-en-custom-price-preview.png`
- `production-en-published-persisted.png`

### Arabic / RTL

| Check | Outcome |
|---|---|
| RTL direction / overflow | PASS — `dir=rtl`; no horizontal overflow |
| Category controls | PASS — المنزل والحديقة → مطبخ |
| Dynamic fields | PASS — الماركة، حجم الشاشة (بوصة)، الدقة |
| Required-field validation | PASS — `"الماركة" مطلوب.`; zero write requests |
| Source/custom API calculations | PASS — authenticated server previews returned EGP 10/12, 10%, 1/1.20, 9/10.80 |
| Custom pricing and commission panel | PASS — Arabic labels and correct EGP 12.00, 10%, 1.20, 10.80 amounts |
| Source-mode completed numeric rendering | NOT TESTED — retained screenshot shows resolving state; no completed visual assertion was captured |
| Existing-listing update | PASS — PUT `/api/admin/commercehub/update`, HTTP 200; same listing retained |
| Persistence | PASS — fresh reload, published state, EGP 12.00 |
| Workflow errors | PASS — no captured console errors or failed workflow API requests |
| Entire-screen absence of English fallback | BLOCKED — listing overview renders `Kitchen` and raw `out_of_stock`; existing shell also contains English Help/Soon and Owner |

The earlier isolated acceptance had no persisted listing, so it could not reveal the overview-row leakage. This report does not misrepresent that prior isolated result as complete Production localization acceptance.

The exact authorized commit remains deployed. No unapproved application localization patch was substituted to conceal the finding.

Evidence:

- `production-ar-acceptance.json`
- `production-ar-dynamic-attributes.png`
- `production-ar-required-attribute-validation.png`
- `production-ar-source-price.png`
- `production-ar-custom-price-preview.png`
- `production-ar-published-persisted.png`
- `production-validation-messages.json`

## G. Pricing Integrity

| Authority | Before / after |
|---|---|
| POS/default unit selling price | EGP 10.00 / EGP 10.00 |
| Source variant price | EGP 10.00 / EGP 10.00 |
| Source online-price override | NULL / NULL |
| Effective Storefront selling price | EGP 10.00 / EGP 10.00 |
| CommerceHub custom listing price | EGP 12.00 |
| Stock / reserved quantity | 0 / 0, unchanged |

The English and Arabic captured passes compared source units, variant/pricing fields, warehouse stocks, tax settings, and purchase price before/after publication with **no changed protected field**. These saved comparison baselines start after the separately authorized online-visibility save; they are not a complete before/after snapshot of that save.

Only authorized operations were performed: one global rule creation, one product online-visibility save, one CommerceHub publication, and one same-price Arabic listing update. No order, checkout, historical transaction, balance, settlement, repayment, or accounting mutation was performed. No full historical-ledger checksum certification is claimed.

The zero-stock listing remains correctly out of stock; no stock was fabricated to make it purchasable.

Evidence: `production-source-product-en-before.json`, `production-source-product-ar-before.json`, both language acceptance files, and `production-sql-authority-readback.json`.

## H. Final Verification Matrix

| Scenario | Result | Evidence / qualification |
|---|---|---|
| Target commit verification | PASS | Exact authorized application commit and running DLL parity |
| API deployment | PASS | New immutable image, generation 64, Ready |
| Admin Web deployment | PASS | New immutable image, generation 22, Ready |
| API health: literal aggregate Healthy | BLOCKED | HTTP 200, but aggregate Degraded for retained disabled certificate/Caddy adapters |
| API readiness / business dependencies | PASS | Ready; business dependency checks Healthy |
| Admin Web access | PASS | HTTP 200 and actual authenticated use |
| Global commission rule | PASS | Active SQL 10% rule |
| Merchant scope | PASS | Global/null target; no scoped overrides; 55 resolver combinations |
| Commission preview | PASS | Real server-authoritative 10%, no fallback |
| Commission amount | PASS | EGP 1.20 on EGP 12.00 |
| Merchant net earnings | PASS | EGP 10.80 |
| Category hierarchy | PASS | Parent/child validation and required-child blocker |
| Dynamic attributes | PASS | Schema rendering and bilingual required-field validation |
| Custom CommerceHub price | PASS | EGP 12.00 persisted |
| POS source price | PASS | EGP 10.00 unchanged |
| Storefront source price | PASS | EGP 10.00 unchanged; online override NULL |
| English UI | PASS | Functional headed workflow and persisted publication |
| Arabic/RTL UI: complete localization | BLOCKED | Functional flow passes; overview/shell English strings remain |
| Product publication | PASS | HTTP 200, new published listing, fresh browser and SQL readback |
| Published listing | PASS | Correct category/price, matching global rule validated at publish/update |
| Storefront application | PASS | Same deployment generation, image, Pod, and spec |
| Source product online visibility | PASS | Separately authorized false → true change |
| Finance / historical records | PASS | No such mutation executed; no full-ledger checksum claim |
| Historical-ledger checksum audit | NOT TESTED | No full historical-finance comparison was performed |
| Checkout / order commission snapshot | NOT TESTED | No order or checkout was executed |
| Source-price-mode publication persistence | NOT TESTED | Source mode was previewed; actual publication/update retained custom EGP 12.00 |
| Arabic source-mode completed numeric rendering | NOT TESTED | API calculation passed; source-price screenshot shows resolving state |

Evidence limitations: the language acceptance JSON `verifiedAtUtc` values mark the prepublication sample, not completion of every later check. Each file also records its subsequent successful write and reload; the later timestamped SQL readback independently confirms persistence. An optional additional final browser readback could not run after Chrome/CDP became unavailable; it produced no evidence file and is not counted as another successful pass. Earlier successful browser reloads and the subsequent SQL/runtime readbacks remain the acceptance evidence.

## I. Final Verdict

| Area | Verdict |
|---|---|
| Source verification | PASS |
| API deployment | PASS |
| Admin Web deployment | PASS |
| Global commission configuration | PASS |
| Commission preview | PASS |
| English browser acceptance | PASS |
| Arabic browser acceptance: functional RTL publishing | PASS |
| Arabic browser acceptance: all required no-English criteria | BLOCKED |
| Actual product publication | PASS |
| Pricing integrity | PASS |
| Literal aggregate-Healthy requirement | BLOCKED |
| Overall CommerceHub Production acceptance | BLOCKED |

The release, global 10% configuration, real publication, and monetary integrity are verified. **Full unconditional Production acceptance is not claimed.** Remaining gaps are the untranslated Arabic overview/shell strings and the retained Degraded optional-adapter health status. Resolving either by application or infrastructure changes would require scope beyond deploying the exact accepted commit.

No passwords, tokens, cookies, browser storage, authorization headers, Kubernetes secrets, or private configuration values are included in the report or evidence.
