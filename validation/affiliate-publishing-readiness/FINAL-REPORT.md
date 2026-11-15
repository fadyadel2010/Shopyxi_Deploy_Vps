# Affiliate merchant publishing readiness — 2026-11-15

**Implementation and Production release: PASS. GitHub closure: pending the focused commit/push and verified remote heads.** The first rollout failed on a missing CRI archive-index alias and was safely rolled back. The bounded retry succeeded; fresh runtime identity and read-only Production UI verification passed. No failed rollout or incomplete browser attempt is counted as successful acceptance.

## A. Existing Implementation

Source baseline: `d273e6d7e470dc9f033bb83b78c0a6065fe3e4f6`. Repository: `https://github.com/fadyadel2010/Shopyxi_Deploy_Vps.git`; branch: `affiliate/publishing-readiness-20261115`.

The requested branch and a partial readiness implementation already existed in this worktree when this continuation began. The original working directory also contained protected, overlapping and unrelated differences. That directory was not edited, reset, switched or staged. Remote main and branches were fetched and inspected; no repository was created and no history was rewritten. The preceding full-E2E and production-remediation reports were read first.

`protected-worktree-verification.json` confirms the original 35 working entries remain and the preserved patch still matches its historical hash. Sixteen of the 17 historical full-file hashes match. The original directory's `Program.cs` already contained overlapping readiness registration at continuation start, so its full-file hash differs from the older financial-release snapshot; that pre-existing work was retained, not reset to manufacture a 17/17 claim.

`AffiliateProductPolicy.IsEnabled` is participation, not proof of a sellable offer. The normal combined configuration endpoint is `POST /api/admin/products/{productId}/affiliate/enable-with-category`; the legacy enable endpoint remains supported. Existing entry/re-entry readiness checked StoreConfig `live`, configuration-valid payment rows and active shipping-row presence, but did not enforce current merchant checkout readiness consistently on discovery/referral reads. Existing product/category/pricing/stock authorities already supplied important sellability checks.

No separate Affiliate publication action was invented. Entry/re-entry retains its existing admission rule; editing an already-enabled policy remains possible while prerequisites are missing. Effective eligibility is independently recomputed and cannot be conferred by policy editing.

Relevant source families:

- `PointOfSale.API/Services/Affiliate/AffiliatePromotionReadinessService.cs` and `PointOfSale.Core/Interfaces/Affiliate/IAffiliatePromotionReadiness.cs`.
- `PointOfSale.API/Controllers/{AdminAffiliateProducts,AdminProductAffiliate,AffiliatePortal,PublicAffiliateProducts,AffiliatePromotionLinks,StorefrontLandingPage}Controller.cs`.
- `PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs` and `PointOfSale.Infrastructure/Elasticsearch/Services/AffiliateProductDiscoveryService.cs`.
- `PointOfSale.API/Middleware/AffiliateReferralMiddleware.cs`, `AffiliatePromotionAttributionAuthority.cs`, and the Affiliate-specific final authority section of `CheckoutService.cs`.

## B. Business Rules

| Group | Exact authority used |
|---|---|
| Storefront | Same-tenant StoreConfig operational status `live`; Tenant `IsActive`, `Status == Active`, and `DeletedAtUtc == null`. Missing/disabled configuration is not enabled; maintenance or a suspended/inactive/deleted tenant is not operational. A config row alone is insufficient. |
| Payment | Current SQL enabled, nondeleted tenant method, admitted by the existing `StorefrontPaymentRuntimeReadinessService`. This checks checkout-supported type, online/offline capability, decryptable and type-valid configuration, and required local runtime/workflow admission. COD qualifies; configured operational ManualWallet can qualify. Unsupported/financially unadmitted Fawry does not. No payment-provider operation is performed. |
| Shipping | SQL-backed `GetShippingMethodsAvailabilityForConfiguredWarehousesAsync`, not projection counts: active/nondeleted method, active/nondeleted same-tenant fulfillment warehouse, existing service-level and carrier-specific admission. Manual shipping needs no external carrier mapping. Existing rate/availability and product fulfillment rules are reused. No quote, shipment or carrier connection test is executed. |
| Product | Same-tenant, nondeleted, Active, Published, online-visible/searchable product, current SQL Storefront recomposition and category lineage, nonblank required name, valid default unit/conversion and active semantic variant configuration, supported EGP currency and existing public pricing/stock/backorder/non-stock rules. A genuinely priced variant must be fulfillable through an admitted configured shipping method; price on one variant and stock only on another cannot manufacture an offer. |

The actual payment entity has `IsEnabled`, not an additional independent Active flag. Shipping uses `IsActive`, not a separate Enabled flag. Deleted rows are excluded explicitly. Missing capabilities/configuration are not treated as operational merely because a row exists.

No custom domain, new DNS/SSL condition, mandatory image, positive-stock-only rule or universal carrier geographic mapping was added. Valid backorders and service/non-stock products are covered by regression tests.

## C. Backend Implementation

`IAffiliatePromotionReadiness` is the effective-promotion authority registered in API DI. Its result separates Storefront, payment, shipping and product readiness from policy/category participation. `PrerequisitesReady` and `IsPromotionEligible` are different values. Merchant-only evaluation is reused for legacy profile referral evidence without introducing a second rule set.

- Product ownership is checked **before** reading merchant configuration. Missing, deleted and foreign products have the same safe not-found result and no leaked blocker details.
- Owner/Admin APIs derive tenant authority from authenticated `ITenantContext`; global Affiliate selections are checked against the selected product's actual SQL owner.
- All applicable blocker codes are returned together in deterministic group order. Codes include Storefront enabled/active, payment/shipping required, product lifecycle/sellability/configuration, and existing policy/category conditions. POS renders safe localized explanations, never raw exception/configuration text.
- Entry/re-entry and link creation/reactivation recheck inside the existing transaction. `EvaluateForMutationAsync` requires PostgreSQL and retains shared authority-row locks through the caller's commit. Concurrent prerequisite UPDATE/DELETE either precedes admission and is rejected, or follows commit and is immediately excluded by subsequent reads.
- Merchant entry rejection: HTTP 400 `AFFILIATE_PROMOTION_NOT_READY` plus the complete current readiness result. Affiliate link rejection uses the same stable code without disclosing merchant setup details. Unknown/foreign selections return 404.
- Existing enabled-policy edits do not reset participation, commission, cap or product lifecycle. There is no persisted eligibility cache, schema migration or status-rewriting job.
- Current SQL admission is applied over cached search/display candidates. Administrative and offer responses use no-store behavior; public Affiliate responses now explicitly prohibit caching.

## D. POS Admin UI

The existing Affiliate Products table and explanation were preserved. A compact server-derived effective-promotion indicator sits beside the separate Policy Status. Manage shows four independently labelled READY/BLOCKED sections, all current reasons together and the existing corrective routes:

| Area | Existing route |
|---|---|
| Storefront | `/storefront/status` |
| Payment | `/ecommerce/payment-methods` |
| Shipping | `/ecommerce/shipping-methods` |
| Product | `/products/{productId}/edit` |

The Enable boundary fails closed when complete product readiness is unavailable or incomplete. Saving an existing enabled policy remains available. A stale rejection refreshes current server authority without rendering raw server errors. Focus/visibility/reconnection refreshes current settings; it never automatically activates a prerequisite.

English and Arabic strings use the existing i18n system. Headed Arabic acceptance verified RTL, four sections, all simultaneous reasons and actual link navigation preserving the Owner's tenant. Product corrective navigation was also exercised. `browser-errors.json` records zero JavaScript exceptions; an isolated resource console message is not presented as a clean global browser-resource audit.

## E. Affiliate Enforcement

- Member and public discovery/catalog, product details and existing landing-page admission consume current readiness, rather than trusting search/Mongo projections.
- Link generation/reactivation rejects stale/ineligible offers, including a forged `isPromotionEligible: true` request. Existing links are hidden, not deleted or rewritten, while unavailable; restoration returns the same historical link/code idempotently.
- V2 referral capture and protected legacy profile evidence are checked against current merchant/product admission. Final Affiliate checkout authority rechecks under transaction locks. This does not execute checkout in acceptance or alter its ordinary pricing, payment, inventory or financial behavior.
- The V2 final guard is scoped to the promoted product, **not unrelated ordinary products in the cart**. Legacy store-scoped evidence rechecks merchant prerequisites without requiring unrelated items to have Affiliate participation policies.
- The Affiliate catalog refreshes on focus, visible return and reconnect and retires an open offer. Focused UI tests verify stale cards/drawers are removed. Direct API guards remain authoritative even before a browser refresh.
- When only payment or shipping Affiliate readiness was removed, the ordinary Storefront product still returned HTTP 200, referral acceptance was absent and the Affiliate landing returned 404. Archiving the ordinary product correctly made the ordinary product itself unavailable.

No historical click/order/commission/settlement/payout authority was rewritten. See the fingerprint receipts in H.

## F. Test Results

| Gate | Actual result |
|---|---|
| Focused readiness evaluator/API/relational tests | **66 passed**, zero failed/not executed; disposable network-none PostgreSQL plus in-memory operational-rule fixtures |
| Directly affected referral authority dependency tests | **51 passed**, zero failed/not executed; no checkout/order/money journey executed; persisted-order snapshot test explicitly excluded |
| New panel and catalog-refresh frontend tests | **12 passed** |
| Focused policy/readiness compatibility tests | **2 passed**; 24 unrelated cases not selected by the test-name filter |
| TypeScript and affected Web production build | Passed; existing bundle-size warning retained |
| API Release build/full immutable publish | Passed, pinned locked dependencies and .NET 10.0.15 |
| Exact API source/index parity | 2,651 sealed inputs plus separately verified embedded theme resource |
| Exact Web/dependency-source/index parity | 2,193 inputs; unchanged package/lock restored with `npm ci`, no dependency upgrades |
| Image security | Exact API and Web images: zero HIGH/CRITICAL findings; fresh API NuGet audit: no vulnerable packages |

`gate-summary.json` binds counts and report hashes. `production-api-candidate.json`, `production-web-candidate.json` and the API source manifest bind immutable image/source/runtime evidence. Normal offline test-build vulnerability warnings are **not** represented as a successful vulnerability audit; release security used the separate successful online gates.

Coverage includes all four prerequisite groups, disabled/deleted/unsupported/incomplete methods, runtime-disabled manual payment, valid COD/manual payment, manual shipping, warehouse ownership/configuration, lifecycle/visibility/price/unit/variant faults, split variant price/stock, backorders/service products, deterministic multiple blockers, tenant isolation, stale direct API rechecks, transaction/concurrency locking, restoration and participation/product-state preservation. Legacy protected evidence also rechecks readiness.

Baseline gaps were established from the existing source and focused current-state scenarios; no executable failed-before count for the old Production image is fabricated. Earlier fixture/build-attempt failures are not counted as product defect reproductions. The previous 70 backend/67 frontend financial remediation suites and full financial E2E were not repeated.

## G. Browser Acceptance — Scenarios A–K

Initial verified fixture: existing headed Owner, normal Affiliate and customer sessions; internal-only isolated stack; `QA-AHRAM-AFFILIATE-E2E-20261115`; Active/Published; policy enabled; Electronics; 10%; no cap; stock 3; Storefront live; one enabled COD and one active manual fulfillment method with the existing warehouse. No replacement product or merchant was created.

Fixture operations used existing versioned Admin APIs through the headed authenticated browser, followed by actual POS Manage rendering/navigation and Affiliate/customer reads. They were not direct SQL business writes. Initial native status automation hit navigation/unsaved-state and CSRF-harness issues; persisted state was checked before correction, and no ambiguous financial action was repeated. Successful acceptance is explicitly **not** a claim that every fixture setup change was made with a native mouse click.

| Scenario | Action and expected result | Actual result / evidence | Verdict |
|---|---|---|---|
| A — all ready | Read existing product, four sections, catalog/detail and supported existing promotion link. Expect all ready and actual policy identity retained. | All four sections ready; current catalog/detail 200; link returns same ID/code, no replacement offer. `final-candidate-A.json`, `final-candidate-affiliate-ready.json`, `scenario-A-manage.png`. | FIXED AND RE-TESTED |
| B — disabled Storefront | Set existing status disabled, expect explicit blockers and Settings route; restore live. | Enabled/active blockers together; policy and 10% preserved; restored true. `scenarios-BC.json`, `status-disabled.png`. | FIXED AND RE-TESTED |
| C — inactive/unavailable | Use supported maintenance status, expect not-active reason, no automatic activation; restore. | Storefront BLOCKED; other sections READY; explicit Settings route; manual restoration returns ready. Tenant suspension/deletion authority additionally covered by focused tests. `scenarios-BC.json`, `status-maintenance.png`. | FIXED AND RE-TESTED |
| D — payment unavailable | Disable sole usable COD; expect payment blocker, route, catalog/referral rejection; restore COD. | Correct panel/link, catalog empty, copied details/link 404, stale create 400; ordinary product remains 200; restoration true and same link. `scenario-D-*.json`, `scenario-D-payment.png`. | FIXED AND RE-TESTED |
| E — shipping unavailable | Inactivate sole manual method; expect shipping blocker, route, discovery/referral rejection; restore it. | Correct blocker/link, empty catalog/404 detail, create rejected; ordinary product remains 200. Manual shipping restores eligibility without external mappings. `scenario-E-*.json`, `scenario-E-shipping.png`. | FIXED AND RE-TESTED |
| F — product invalid | Unpublish, then restore; archive/deactivate, then explicitly activate/publish. Expect product-specific reasons, preserved policy and correct product route. | All actual lifecycle blockers shown; 10% policy retained; restoration true. Product navigation exercised. `scenario-F.json`, `product-unpublish.png`, `product-deactivate.png`, `scenario-I-product.json`. | FIXED AND RE-TESTED |
| G — simultaneous blockers | Disable Storefront, COD and manual shipping simultaneously; expect all reasons without repeated submissions. | Four stable codes representing all three merchant failures; all corrective links and four readiness sections visible at once. Final immutable candidates reverified. `final-candidate-G.json`, `scenario-G-blockers.png`. | FIXED AND RE-TESTED |
| H — direct/stale/tenant bypass | Request copied details/link, generate using stale true eligibility, and use a mismatched store/product reference. | Empty catalog; detail/link 404; stale create 400 `AFFILIATE_PROMOTION_NOT_READY`; mismatched owner 404 with no configuration disclosure. Merchant entry and concurrent mutation rejection additionally proven by relational tests. `final-candidate-bypass.json`. | FIXED AND RE-TESTED |
| I — remove readiness after activation | Remove payment, shipping, Storefront readiness and archive product; inspect catalog/link/referral behavior. | Offer withdrawn; stale link use rejected; policy stays enabled. Product archival independently removes the offer. Same-page focus removes stale catalog card; tests also retire its open drawer. `scenario-D/E/I-product-*-blocked.json`, `final-candidate-bypass.json`, `catalog-focus-blocked-final.json`. | FIXED AND RE-TESTED |
| J — restore readiness | Restore each prerequisite, including staged restoration of simultaneous failures. Expect no premature admission or lost settings. | False until the last missing prerequisite is restored; then true. Same historical link/code returns; no policy/commission reset. Same-page catalog restoration works. `final-candidate-restoration.json`, `final-candidate-affiliate-restored.json`, `catalog-focus-restored-final.json`. | FIXED AND RE-TESTED |
| K — English/Arabic | Render blocked Manage in both languages; follow actual corrective links and check session tenant. | Four translated sections, all reasons readable, Arabic RTL, actual Storefront/payment/shipping navigation retains the isolated Owner tenant; English restored. Product route exercised in I. `final-candidate-K.json`, `scenario-K-arabic.png`. | FIXED AND RE-TESTED |

Invalid persisted method/configuration/variant combinations that the normal write authorities do not safely permit were exercised in the disposable focused regression fixtures, **not manufactured by corrupting the reused browser fixture**. Deleted product behavior was tested there; the historical QA product was not deleted. Product price/unit/variant and inventory/backorder/service subcases are regression evidence, not falsely labelled individual headed screenshots. No provider credentials, carrier invocation, order submission or stock adjustment was needed for these readiness scenarios.

`final-candidate-browser.json` certifies the bounded rerun on the exact fully built, security-admitted API/Web candidates, not merely the earlier DLL-overlay candidates. Storefront was neither rebuilt nor replaced.

## H. Data Safety

- No Production financial or business settings mutations were performed for acceptance. Only the application release guard is authorized to replace the two application images.
- No Production payment/shipping/Storefront/product/commission policy changes, new identities, products, orders, stock operations, journals, cashbox moves or settlements.
- No automatic Storefront/product activation, publication or prerequisite configuration was added. Isolated activation/restoration in the test scripts is an explicit QA action, not application behavior.
- No schema migration, reseed, reset, direct SQL runtime business write, infrastructure/network/DNS/service change or dependency upgrade.
- Existing isolated historical fingerprints match: 1 order and refund, 4 commission-ledger entries, 2 balance-ledger entries, 2 withdrawal requests, preserved payout/return/settlement records, **11 journals/22 lines**, 4 stock movements and the inventory reservation. The preexisting Mongo click is unchanged. `history-safety-review-before/after.json`, `click-safety-review-before/after.json`.
- All browser fixture prerequisites and product lifecycle settings were restored. Version/audit progression from intentional supported fixture operations is retained, not reset or deleted.
- Production read-only boundary counts were zero for every isolated tenant/Owner/Affiliate/product/order/journal/payout identity before and after release. `production-boundary-before.json`, `production-boundary-after.json`.

The prior financial lifecycle and the separately documented Analytics conversion-projection observation were not reopened.

## I. Deployment and GitHub

Changed deployable components: **API and Admin/Affiliate Web only**. Storefront stays on its existing generation 10 image.

Fresh pre-work read-only Kubernetes/CRI verification confirmed API generation 50 and its `8bd79c…` image, Web generation 15 / `f224346…`, Storefront generation 10 / `e9333f…`, all Ready. See `production-runtime-before.json`; the release guard repeats fresh identity verification immediately before delivery.

Security-admitted candidates:

- API: `docker.io/shopixy/api@sha256:39a79e0baaa7a6a935e2f84b6e77a7d4b22618c6f5d9b5e59a5d8f1ebbc9856b`.
- Web: `docker.io/shopixy/web@sha256:ee6f3d73e076221dd600ea44c178afc8b811e2a32758eae9890553cd38486abd`.

The one-shot guard imports verified archives, admits canonical and previously diagnosed CRI-normalized aliases, and runs a SHA-256-only image-instantiation probe **before** changing deployments. It changes only container image fields with UID/resource-version/spec preconditions and rolls back only its own image changes on failure. No registry push or zero-downtime guarantee is claimed.

The first release attempt admitted and hash-probed both candidate images, but API rollout timed out because CRI retained a missing normalized name for the **outer OCI archive index**, which contains the exact candidate index. The guard restored the previous API image and verified readiness at generation 52; Web remained generation 15 and Storefront generation 10. The recorded failure was at container instantiation, before candidate readiness. The separate bounded retry preserves the exact outer index using `ctr images import --index-name` and verifies that **every** CRI-reported digest name exists, targets its actual digest and resolves to the exact candidate config/layers before applying an image patch. It does not mislabel another digest, purge images, restart infrastructure or modify runtime configuration.

The retry completed at **2026-11-15T20:58:52Z**. API generation **53**, Web generation **16** and unchanged Storefront generation **10** are Ready. Exact runtime assembly/Web-entry hashes match the candidates, and API readiness returned HTTP 200. Independent fresh Kubernetes/CRI verification at 20:59:06Z confirmed config/layer identity, zero restarts and the unchanged Storefront image/pod. The CRI outer-index image IDs are valid wrappers containing the pinned candidate indexes, not a claim that two different digest strings are identical. See `production-deployment-failed.json`, `production-runtime-after-rollback.json`, `image-admission-diagnosis.json`, `release-retry-input.json`, `production-deployment.json` and `production-runtime-after-release.json`.

Read-only headed Production confirmation reused the established Owner session at `https://admin.shopyxi.com`: authenticated product-list HTTP **200**, two existing rows with complete server-derived readiness, compact eligibility and the actual four-section Manage panel. The inspected existing product showed current product/policy blockers; no Production prerequisite or product was changed to manufacture a ready offer. An initial browser read sampled a loading table; the final helper waits for the successful list response and nonbusy table. A sandbox-only `URL` helper error was corrected before the successful final read. `production-readonly.json` records **zero business mutation requests**, no blocked mutation attempts and no exported product identity, commission, cookies or credentials.

Git commit/push and verified remote source parity remain **pending** at this implementation commit. `source-parity-index.json` freshly verifies all 2,651 API and 2,193 Web inputs plus the embedded theme against the staged index. The staged evidence scan has zero findings; three initial assembly-hash-key false positives were removed by unambiguous filename/hash record formatting, not by weakening scanner rules or suppressing credentials. All private fixture credentials/configuration, browser state, raw diagnostics and image archives remain outside Git.

## J. Final Verdict

| Area | Result |
|---|---|
| Storefront readiness | FIXED AND RE-TESTED |
| Payment readiness | FIXED AND RE-TESTED |
| Shipping readiness | FIXED AND RE-TESTED |
| Product validity | FIXED AND RE-TESTED |
| Multiple-blocker visibility | FIXED AND RE-TESTED |
| Server-side bypass protection | FIXED AND RE-TESTED |
| Dynamic eligibility | FIXED AND RE-TESTED |
| Affiliate discovery/referral enforcement | FIXED AND RE-TESTED |
| Policy participation compatibility | PASS |
| Arabic/English UI | FIXED AND RE-TESTED |
| Production API/Admin Web deployment | PASS |
| Production read-only confirmation | PASS |
| GitHub delivery | PENDING |
| Overall Affiliate publishing readiness | PASS — GitHub closure pending |

The deployed readiness feature passes its bounded acceptance. Repository closure remains pending until the focused implementation and sanitized evidence are committed, pushed and independently verified on the existing remote; no registry push, zero-downtime release or repeated financial lifecycle is claimed.
