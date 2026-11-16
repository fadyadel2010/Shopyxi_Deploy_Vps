# CommerceHub Production Acceptance Closure

Report date: **2026-11-17** (local UTC+02); machine receipts use UTC on November 16.

**Application gaps fixed and re-tested on Production.** Arabic overview/shell localization and completed Arabic source-price rendering are verified. A newly discovered empty search index was repaired through the existing, explicitly approved tenant-scoped recovery endpoint. Full type checking now passes.

**Unconditional acceptance remains BLOCKED solely by the literal aggregate-Healthy requirement:** the aggregate response is honestly **Degraded**, not Healthy. Both remaining findings are independently established as **OPTIONAL_DISABLED_BY_DESIGN** for the deployed cert-manager/Traefik architecture. Neither adapter was enabled; no health semantics, infrastructure, or security configuration was changed.

## A. Production Baseline

The complete previous report, its 14 JSON receipts and 11 PNGs were reviewed and consistency-checked. Accepted application commit `4fb2beb41cb704d8b4e9d9ee383afc34d8904594` and documentation-only commit `1f53f19bc1f352a43358a1b6a4a58c4f2e49c708` remain ancestors. The starting branch was `commercehub/merchant-product-publishing-ux-pricing-20261116`; the working tree was clean. A fresh fetch and live inspection preceded implementation/deployment.

| Component | Fresh starting immutable image | Generation | Serving readiness |
|---|---|---:|---|
| API | `docker.io/shopixy/api@sha256:cb900c183069f94311142ad9c5a16fa23266611d3c29f674bae64040189ac307` | 64 | 1/1; both containers Ready |
| Admin Web | `docker.io/shopixy/web@sha256:e95bbdf5a645495003a3f88a580148238d738b6b1fd0ee345194d9e271de02a8` | 22 | 1/1 Ready |
| Storefront | `docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df` | 10 | 1/1 Ready |

- Storefront retained the previous Pod `shopixy-storefront-95dbbc769-tsbbk` and full deployment-spec SHA-256 `a8dc72f11b84d588d5c57b18f5943fc5e10292644b5c1d3b96a95d387ed1f2a7`, before and after this closure.
- Admin HTTPS returned 200 and the genuine Owner session was usable. The existing tenant Storefront returned 200. API root returned 400, not used as a health endpoint; authenticated workflow requests and internal business health checks provide the actual API acceptance evidence.
- Global rule `98eca7ca-414a-442f-bdf6-fb320cfb4de4`: Global, SQL NULL scope ID, rate `0.1`, active, not deleted, priority 0; exactly one rule, no active category/vendor overrides. Before/after SQL is identical.
- Fresh full `/health` inspection revealed `CommerceHubSearchSync=Unhealthy` in addition to the two known Degraded adapters. The previous report's health receipt was the readiness-filtered payload, not a retained full `/health` baseline.

Evidence: `runtime-before.json`, `runtime-predeploy.json`, `runtime-after.json`, `deployment.json`, `running-artifact-parity.json`; previous evidence remains untouched in `../commercehub-production-deployment/`.

## B. Arabic Localization Findings

Runtime localization follows `PointOfSale.Web/src/i18n/config.js`: bundled English/Arabic JSON resources, existing language detector and local-storage locale preference, English fallback, `languageOnly`, and existing document-language synchronization. Vite bundles these resources into Admin Web JavaScript; there is no separate runtime locale download to replace.

| Actual visible finding | Rendering component / cause | Small correction and existing localization source | Focused regression | Production result |
|---|---|---|---|---|
| `Kitchen` | `pages/CommerceHubSettings.jsx` overview rendered API `row.categoryName` directly, unlike its form | Resolve `row.categoryId` through existing category-tree helper; Arabic `nameAr=مطبخ`, English `name=Kitchen`. Unknown Arabic category uses existing `common.unavailable`, not an invented category or English row label | `CommerceHubSettings.localization.test.jsx`: bilingual taxonomy and missing-category tests | **FIXED AND RE-TESTED** |
| `out_of_stock` | Same overview directly rendered backend enum | Known values use the same existing `commerceHub.publishing.inStock`, `outOfStock`, `unavailable` keys as inventory filters. Arabic `نفد المخزون`; unknown/null values use localized unavailable | Same test: known statuses, unknown/null fallback, bilingual overview | **FIXED AND RE-TESTED** |
| `Help` | `admin/help/HelpButton.jsx` lookup `common.help` was missing from both resource files; literal default rendered | Add `common.help`: `Help` / `مساعدة` | `Layout.localization.test.jsx`: actual shell/HelpButton rendering in both locales | **FIXED AND RE-TESTED** |
| `Soon` | Same button; `common.soon` missing | Add `common.soon`: `Soon` / `قريباً` | Same shell tests | **FIXED AND RE-TESTED** |
| `Owner` | `components/Layout.jsx` directly rendered `user.role` | Reuse existing `userManagement.roles.Owner=مالك`; all five POS role translations already exist. Unknown role uses localized unavailable, never raw role | Same shell tests: five roles plus unknown, English Owner preserved | **FIXED AND RE-TESTED** |
| `Help article coming soon` tooltip | Same HelpButton; missing `common.helpUnavailable` | Add Arabic `مقال المساعدة متاح قريباً`; preserve exact English default | Same bilingual shell tests verify tooltip | **FIXED AND RE-TESTED** |
| `Available` potential shared availability fallback | Same HelpButton; missing `common.available` | Add `Available` / `متاح`; no availability policy change | Same shell tests exercise available branch | **PASS** — focused component test; this page correctly remains “Soon” |
| `Help Center` shared lookup | Existing HelpDrawer's `common.helpCenter` missing; closed/off-canvas drawer, not counted as visible main-workflow leakage | Add `Help Center` / `مركز المساعدة` in the same resource pair | Locale structural certification | **PASS** — lookup coverage; opening the Help article/assistant is outside the publishing scenario |

No other untranslated UI messages or missing keys were visible in the inspected Arabic overview, expanded existing listing, category selectors, pricing/commission panel, and visible Admin shell. Product name/SKU `QA-ERP-20261112-TRACKED`, brand names SHOPIXY/CommerceHub, ISO currency code EGP, shortcut Ctrl K, and language-switch target EN are identifiers, not untranslated English UI messages; they are intentionally preserved.

Before: `before-ar-custom-completed.png`, `before-ar-source-completed.png`, `browser-before-ar.json`.

After: `after-ar-custom-completed.png`, `after-ar-source-completed.png`, `browser-after-ar.json`. Screenshots were opened and visually reviewed, not certified from JSON alone. Arabic labels, correct RTL, zero document horizontal overflow, zero captured console errors and failed workflow requests were verified. The claim is limited to this inspected workflow, not all Admin modules or Help articles.

All existing English locale values remain unchanged; only missing shared keys were added using their existing English defaults. No global text replacement, taxonomy mutation, locale-switch change, or new framework.

## C. Arabic Source-Price Mode

| Identity / value | Verified result |
|---|---|
| Product | `QA-ERP-20261112-TRACKED` / `b2ec52fb-d600-f1ed-dc23-2fcdc09e61b8` |
| Variant | `81069782-7bda-4e35-bf24-3e13424f06f2` |
| Existing listing | `ff8cdce3-ad88-4467-b027-cdde5812e84d` |
| Source price | EGP 10.00 |
| Source-mode CommerceHub preview price | EGP 10.00 |
| Commission | 10%; rule `98eca7ca-414a-442f-bdf6-fb320cfb4de4`, Global, no legacy fallback |
| Deduction | EGP 1.00 |
| Merchant net | EGP 9.00 |
| Persisted custom listing | Still EGP 12.00; mutation version 1 |
| Custom-mode independent preview | EGP 12.00; deduction 1.20; net 10.80 |
| Browser / locale | Actual headed Chrome, native browser language `en-US`, selected application locale `ar`, `dir=rtl` |
| Verdict | **PASS** |

Both before and after deployment, the browser selected source mode **only in unsaved form state**. The real preview request had `pricingMode=source` and no custom marketplace-price argument; the component receives null custom price in this mode. This is the read-only equivalent of `CommerceHubPrice=null`; the persisted listing was not converted to source mode.

The harness waited for all five numeric rows to equal the locale-formatted expected values and for the commission spinner to be absent. It captured the actual UI-triggered authenticated response using CDP network events, not a substitute mocked/direct response. `browser-after-ar.json` includes that response, completed DOM rows, timestamp, role, IDs, direction and screenshot reference. Reload discarded unsaved source selection and confirmed persisted custom EGP 12.00.

Screenshot: `after-ar-source-completed.png`; adjacent custom proof: `after-ar-custom-completed.png`. No publish/update/price write was sent during either acceptance pass.

## D. Production Health Investigation

### Certificate provider — OPTIONAL_DISABLED_BY_DESIGN

- Responsibility: legacy custom Alias/Redirect domain ACME issuance/status/revocation through `CaddyManagedAcmeCertificateProvider`, with account selection, Caddy route/certificate observation, and authorized CA revocation-control client.
- Source: `PointOfSale.Infrastructure/Certificates/CertificateProviderFoundation.cs` options/validation (16–88), adapter (538 onward), and disabled health result (1207–1277). `PointOfSale.API/Services/Storefront/CertificateProvisioningService.cs:724–767` limits this provisioning path to nonreserved Alias/Redirect domains and validates enabled provider configuration.
- Effective Production: retained `/app/appsettings.json` has `Enabled=false`; Production JSON has no override; no adapter environment settings or command-line overrides were present. Account/directory configuration is absent. Only setting presence/flags and config hashes were persisted, not private contents.
- Actual Production issuance/renewal is cert-manager 1.21.3: frontend/API certificates and wildcard origin certificate are Ready for their current generations, with scheduled renewal times and valid future expiry. Relevant Let's Encrypt HTTP-01 and Cloudflare DNS-01 ClusterIssuers are Ready.
- The current tenant architecture explicitly uses global wildcard TLS, not per-tenant certificate execution: `deploy/automatic-subdomains/README.md:3–13`, `PointOfSale.API/Consumers/TenantProvisioningConsumer.cs:14–17`.
- Live SQL counts: no nondeleted legacy Domains, SslCertificates, or SslCertificateOperations. There is no queued or active legacy certificate workload requiring this adapter.
- No missing requirement for current serving. Enabling this *different optional feature* would require a reviewed account/directory/revocation/edge configuration, not merely setting a flag.
- Its health check deliberately reports disabled as Degraded. That literal result remains unchanged.

### Caddy edge — OPTIONAL_DISABLED_BY_DESIGN

- Responsibility: `CaddyfileEdgeClient` manages per-custom-domain Caddy route files, runs validation/reload commands, and supplies certificate edge/SNI observation.
- Source: `PointOfSale.Infrastructure/Certificates/CaddyEdgeIntegration.cs` options/conditional requirements (18–95), route manager (142 onward), disabled health branch and validation (791–847).
- Effective Production: `Enabled=false` in retained base JSON; no Production, environment or command-line override. Required route/root/log/upstream/validate/reload settings are absent.
- Live Kubernetes exact Admin/API routes and wildcard tenant routes use **Traefik**. Traefik 3.7.13 is Ready. No Caddy deployment was found in the cluster; no host Caddy process is present. The API edge sidecar is nginx, not Caddy.
- Ready cert-manager frontend and wildcard certificates back the actual Traefik TLS routes. Public Admin/tenant HTTPS requests succeed with normal TLS verification.
- There are no legacy custom-domain/certificate operations in SQL. Current serving/provisioning paths do not require Caddy.
- No missing requirement for current serving; custom-domain Caddy enablement would be a separate infrastructure/security feature rollout.
- Disabled still reports Degraded by the retained health contract. No adapter/check was disabled or altered in this assignment.

Evidence: `health-architecture.json`, `runtime-predeploy.json`, `runtime-after.json`, `runtime-final.json`, and the source paths above. No certificate bodies, private keys, Secret values or full environment/config bodies were retained.

### Additional actual defect fixed: empty CommerceHub search index

Fresh full health returned HTTP 503/Unhealthy because `CommerceHubSearchSync` was Unhealthy. Read-only checks showed the active alias/mapping were compatible, but the search index contained **zero** documents despite the existing published/searchable SQL listing. No exception was attached to this health finding.

The user explicitly approved **“Repair derived search”** after being told the scope and business-data boundary. The genuine Owner session called the existing supported tenant-scoped endpoint `POST /api/admin/commercehub/search/reindex` **once** at `2026-11-16T22:55:04Z`. The endpoint invokes `CommerceHubReindexService.ReindexTenantAsync`, revalidates current SQL authority, repairs derived visibility, and does not write source product/listing/commission tables.

Result: HTTP 200; processed 1, succeeded 1, failed 0, staleDeleted 0, promoted false. The unchanged active index then contained 1 document. `CommerceHubSearchSync` became Healthy; the final independent count remained 1. Listing and protected source product readbacks were identical before/after recovery. No publication or listing update was substituted for repair.

Evidence: `search-readonly.json` (before), `search-recovery.json` (single approved operation and readbacks), `search-final-readonly.json`, `runtime-recovered.json`, `runtime-after.json`.

The proven operational cause was missing derived search data. This closure does not claim a newly identified consumer-code root cause, a future-publication test, or a global search rebuild; no API patch was justified.

### Final health distinction

| Surface | Actual result |
|---|---|
| Internal `/health/ready` | HTTP 200; payload Degraded |
| Full `/health` after recovery | HTTP 200; payload Degraded |
| Required business dependency checks | Healthy |
| CommerceHubSearchSync | Healthy after approved recovery |
| certificate_provider / caddy_edge | Degraded: optional adapters disabled by design |
| Aggregate literal Healthy requirement | **BLOCKED** |

No health status was manufactured. Readiness, business health, optional-feature status, and aggregate status are not conflated.

## E. Focused Regression Tests

All commands ran in `PointOfSale.Web` with Node 22.23.4 using existing installed dependencies.

```text
node node_modules/vitest/vitest.mjs run src/pages/CommerceHubSettings.localization.test.jsx src/components/Layout.localization.test.jsx src/pos-admin/commercehub src/i18n/i18nCertification.test.ts src/i18n/userManagement.locales.test.ts --reporter=default --reporter=json --outputFile=../validation/commercehub-production-acceptance-closure/frontend-tests.json
node node_modules/typescript/bin/tsc --noEmit
node node_modules/vite/bin/vite.js build
```

| Test file | Passed |
|---|---:|
| `src/pages/CommerceHubSettings.localization.test.jsx` | 9 |
| `src/components/Layout.localization.test.jsx` | 9 |
| `src/pos-admin/commercehub/commissionCalculator.test.ts` | 4 |
| `src/pos-admin/commercehub/useCommissionPreview.test.tsx` | 3 |
| `src/i18n/i18nCertification.test.ts` | 4 |
| `src/i18n/userManagement.locales.test.ts` | 4 |
| **Total** | **33/33; 6 files** |

The 18 new focused cases cover actual overview rendering, bilingual taxonomy/status, unknown/raw enum prevention, shared Help/role display, RTL/LTR, and completed Arabic custom→source→custom numeric rendering through the real preview hook with test-only server fixtures and zero writes. Production evidence uses no fixtures.

Initial full type checking revealed a pre-existing TS7016 missing declaration in `CommerceHubSellerApplication.test.tsx`; accepted-source overlay reproduced it without overwriting working files (`typecheck.json`). After the user's request to fix remaining issues, an adjacent `CommerceHubSellerApplication.d.ts` was added using the repository's existing `ComponentType` convention. **Final full `tsc --noEmit`: PASS**, without exclusions, suppression or tsconfig changes (`typecheck-final.json`).

An ignored root `node_modules` junction supplies existing shared dependency resolution for imported Storefront modules in this Windows workspace; no dependency installation/upgrade, Storefront source/config change, or committed symlink was made.

Production frontend build: **PASS**, Vite 6.4.5, 5,096 transformed modules, 70 files, 36.60 seconds. Existing large-bundle warning remains; no unrelated optimization. Image nginx syntax test exited successfully; its existing non-root error-log-path warning did not prevent rollout/serving. No API/Storefront build; no repetition of the 55 resolver campaign.

Evidence: `frontend-tests.json`, `frontend-gates.json`, `typecheck.json`, `typecheck-final.json`, `artifact-verification.json`.

## F. Production Deployment

- Application source: `856648d0b61a7621c600524b5aa55cf4a72fb385`.
- Localization/test commit: `d5e1e6313136253d8917abcd991b91cce62d823d`.
- Final application follow-up: `856648d0b61a7621c600524b5aa55cf4a72fb385` (missing page declaration only).
- Changed deployed component: **Admin Web only**. API, Storefront and all infrastructure/configuration remain unchanged.
- Exact running accepted Web parent was retained locally and digest-verified. Build used that same parent without registry substitution; immutable release was transferred over SSH/SCP, hash-verified and imported into the existing single-node k3s containerd. No registry push is claimed.
- Source tree seal: `cae8192264f66315958f273a69be053a398c42dd63eeff965177236bd0abcbdb`.
- Web dist seal: `81c0f7731fe03bde8f3df60dd5dbbd46e9720b4043edb770c9fcb4de5e0e5748`.
- Archive SHA-256: `af0f307ecc43b66fcaa75394b324b6992fdb9bc994ec1636c75eb342c6776202`.
- New immutable Web image: `docker.io/shopixy/web@sha256:627af0b79e1a8bfa4be76691df0f43c938c9b586727d11c2f223e43580c21568`.
- Runtime config identity: `sha256:cd7c333299270ee307f490f8229067991d90727541c92dd00fdc4b9e2121664a`.
- Build/image file parity: PASS; running Pod's **70 files** independently match the sealed dist. UID 101; Trivy cached/offline HIGH/CRITICAL findings: zero.
- Guarded rollout tested UID/resource-version/old image/full spec, server-dry-ran then changed only the Web image. Existing `Recreate`, replicas and pull policy retained; no zero-downtime claim.
- Rollout: `22:59:59Z`–`23:00:04Z`; generation **23**, observed 23, Ready 1/1, Pod `shopixy-web-6bdb8f8bc8-s8fbz`, zero restarts.
- API generation 64/same Pod/spec/digest; its three assembly hashes still match accepted release output. Storefront generation 10/same Pod/spec/digest.

Evidence: `artifact-inputs.json`, `artifact-verification.json`, `deployment.json`, `running-artifact-parity.json`, `runtime-after.json`.

## G. Browser UI Acceptance

Both post-deployment passes used actual headed Chrome at `https://admin.shopyxi.com/admin/commercehub`, the user's existing genuine **Owner** cookie session, and the same product/variant/listing identified in section C. No injected bearer token, session rewrite, API mock, synthetic response, localhost or fixture was used as Production evidence. Language was changed through the existing header switch.

### Arabic

- App locale `ar`, native browser `en-US`, document RTL.
- Overview: `مطبخ`, `نفد المخزون (٠)`, `منشور`, persisted custom EGP 12.
- Shell: `مساعدة`, `قريباً`, `مالك`; tooltip translated.
- Completed source: EGP 10 / 10% / 1 / 9; completed custom: EGP 12 / 10% / 1.20 / 10.80.
- Correct category form, listing details, commission resolution and preserved source price.
- No visible fallback messages, missing keys, raw status enums, document horizontal overflow, captured console errors, or failed workflow requests.
- Zero non-GET/business writes during this acceptance pass.
- **FIXED AND RE-TESTED**.

Evidence: `browser-after-ar.json`, `after-ar-source-completed.png`, `after-ar-custom-completed.png`.

### English

- App locale `en`, native browser `en-US`, document LTR.
- Kitchen, Published, human-readable Out of stock; original Help/Soon/Owner remain correct.
- Completed source/custom values identical to server calculations above; existing custom EGP 12 listing and details unchanged.
- No layout overflow, captured console errors, failed workflow requests, or business writes.
- **PASS**.

Evidence: `browser-after-en.json`, `after-en-source-completed.png`, `after-en-custom-completed.png`.

An initial post-deployment capture timed out after its custom screenshot; it was not counted as a completed acceptance run. Bringing the actual headed tab to the foreground resolved the capture limitation; the subsequent complete Arabic/English passes above contain full receipts. No write retry occurred.

## H. Financial and Business Integrity

- Global 10% rule and all its recorded identity/scope/priority/active/deleted fields are identical in before/after SQL.
- Existing listing remains published, same category, custom EGP 12.00, price-mutation version 1. No new product or publication, no publish/update/unpublish/bulk-price operation.
- POS/default and source variant price remain EGP 10.00; online override NULL; effective Storefront price EGP 10.00.
- Stock remains zero. Protected source units/variants/warehouse stock fields were compared before/after each browser scenario and approved recovery; no differences. No reservations/inventory mutation was requested or executed.
- Recovery modified only derived search/projection state through the existing supported endpoint; no alias promotion or infrastructure change.
- No commission configuration, historical financial record, order, checkout, payment, shipping, Affiliate, stock/reservation, merchant balance or settlement mutation was performed.
- No full historical-ledger checksum certification is claimed; none was necessary for this closure.

Evidence: SQL in `runtime-before.json`/`runtime-after.json`, `running-artifact-parity.json`, both browser receipts and `search-recovery.json`.

## I. GitHub Delivery

| Item | Identity |
|---|---|
| Private repository | `https://github.com/fadyadel2010/Shopyxi_Deploy_Vps.git` |
| Focused branch | `commercehub/production-acceptance-closure-20261117` |
| Preserved evidence predecessor | `1f53f19bc1f352a43358a1b6a4a58c4f2e49c708` |
| Localization application commit | `d5e1e6313136253d8917abcd991b91cce62d823d` |
| Final application / actual deployed Web commit | `856648d0b61a7621c600524b5aa55cf4a72fb385` |
| Actual deployed API commit | `4fb2beb41cb704d8b4e9d9ee383afc34d8904594` (unchanged) |
| Documentation/evidence commit | `4b5b7342e784e743e297cf0ae844fe4aa1947835` |

Application commits and evidence commit `4b5b7342e784e743e297cf0ae844fe4aa1947835` were pushed to the new branch without rewriting history. `git ls-remote` independently returned that exact evidence commit. `github-delivery.json` records this verification, the exact deployed identities and secret-scan results. The final delivery message records the subsequent documentation-only tip and final remote/tree verification. Evidence-only follow-ups are not application releases.

Secret scanning uses the installed Gitleaks scanner. A WSL Git-mode attempt could not resolve the Windows worktree pointer and scanned zero bytes; it is **not** counted as a successful gate. The actual source/staged diff was then passed to Gitleaks stdin and scanned successfully. The final evidence directory/delivery diff is separately scanned before committing.

The initial evidence scan flagged the accepted API DLL SHA-256 as a generic API key because the JSON dictionary key contained `API.dll`. The receipt was represented instead as explicit `{file, sha256}` records; no hash was removed and no scanner rule was suppressed. The initial redacted finding is retained separately, and the final evidence scan is required to be clean.

No passwords/tokens/cookies/Authorization values, browser storage, Kubernetes Secret bodies, private keys, certificates, customer personal data, database dumps or raw Production logs are committed. Build/runtime configuration and connection credentials used for read-only diagnostics stayed in memory outside evidence; public metadata and sanitized counts only were saved.

## J. Final Verdict

| Area | Verdict |
|---|---|
| Production baseline verification | PASS |
| Arabic overview localization | FIXED AND RE-TESTED |
| Shared Admin shell localization in inspected workflow | FIXED AND RE-TESTED |
| Arabic source-price completed visual rendering | PASS |
| English regression acceptance | PASS |
| Certificate provider classification | PASS — OPTIONAL_DISABLED_BY_DESIGN |
| Caddy edge classification | PASS — OPTIONAL_DISABLED_BY_DESIGN |
| Full frontend type check | FIXED AND RE-TESTED |
| CommerceHub search-index recovery | FIXED AND RE-TESTED |
| Production readiness | PASS — HTTP 200, all serving Pods Ready |
| Required business dependency health | PASS |
| Literal aggregate Healthy | BLOCKED — actual payload remains Degraded |
| Admin Web-only deployment | PASS |
| Financial/business integrity | PASS |
| Overall application-level CommerceHub acceptance | FIXED AND RE-TESTED |
| Overall unconditional CommerceHub Production acceptance | BLOCKED — literal aggregate-Healthy condition only |

There is **no remaining demonstrated required-adapter configuration or serving defect**. The unresolved literal health condition is a retained optional-feature reporting contract, not proof of broken public TLS/edge operation. Making it Healthy would require a separately reviewed health-policy change or a distinct optional-feature rollout, both explicitly excluded from this assignment. Neither was performed to obtain a green result.
