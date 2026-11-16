# CommerceHub Merchant Product Publishing — Final Validation Report

Date: 2026-11-16

Branch: `commercehub/merchant-product-publishing-ux-pricing-20261116`

Base commit: `cf5892ed379261d7a1d08ba3c622d8f2dbda4a80`

## Final verdict

| Area | Verdict | Notes |
|---|---|---|
| Implementation | PASS | Taxonomy-safe publishing, listing-only source/custom pricing, authoritative commission preview, server-side publish revalidation, dynamic attributes, and English/Arabic UX are implemented. |
| API build | PASS | .NET SDK 10.0.404; 0 errors. Existing repository warnings remain. |
| Admin Web build | PASS | Vite production build completed successfully. |
| Focused backend regression | PASS | Earlier focused suite: 20/20. Final exact changed-test rerun: 18/18. |
| Focused frontend regression | PASS | 7/7 across commission calculator and authoritative preview tests. |
| Focused lint | PASS | Changed CommerceHub Web files produced no lint errors. The full repository lint remains blocked by eight pre-existing unrelated Affiliate/onboarding errors. |
| Headed isolated English UX | PASS | Category cascade, custom EGP price, localized authority blocker, disabled Publish, and responsive layout verified in real headed Chrome. |
| Headed isolated Arabic/RTL UX | PASS | RTL direction, Arabic commission blocker/breakdown, disabled Publish, no listed English leakage, and no horizontal overflow verified in real headed Chrome. |
| Resolved commission scenario | BLOCKED | The isolated Ahram fixture has no active SQL commission rule. API correctly returns `409 COMMISSION_AUTHORITY_MISSING`. No rule or rate was fabricated. |
| Isolated publication | BLOCKED | Publication is intentionally disabled while commission authority is unresolved. No CommerceHub write request occurred. |
| Production acceptance | NOT TESTED | Requires an authorized Production Ahram Owner session. Production credentials were not copied into or accepted by the isolated environment. |
| Production deployment | NOT PERFORMED | Deployment requires fully accepted resolved-rule and read-only Production verification. Storefront was not changed. |

## Scope confirmed

- CommerceHub-only custom pricing uses the existing nullable listing-level `CommerceHubPrice` authority.
- `null` means source-price mode; a positive value with at most two decimal places means a custom CommerceHub price.
- POS and Storefront source prices remain unchanged.
- No inventory, finance, Affiliate, checkout, infrastructure, seller-application approval, or general pricing-engine behavior was added or changed.
- No database migration was required.
- Parent and final category IDs are submitted and validated server-side. A parent with active children requires a valid active child belonging to that parent.
- Preview, publish, and update resolve commission from authenticated server authority. Missing, ambiguous, or changed commission rules fail closed.

## Automated verification

### Backend

- `dotnet build PointOfSale.API/PointOfSale.API.csproj --configuration Release --no-restore`
  - PASS with .NET SDK 10.0.404
  - 0 errors; existing warnings only
- Focused changed-test rerun:
  - PASS: 18/18
  - Covers merchant publishing, pricing authority, projection authority, category selection, pricing precision, and legacy rollout independence.
- A wider class-level architecture filter was not used as release evidence because it includes unrelated tests whose archived certification documents are absent from this checkout. Those failures are outside this change.

### Frontend

- `npm run build`
  - PASS
- `vitest` focused suite:
  - PASS: 2 files, 7 tests
  - Covers commission calculations, fail-closed preview behavior, stale-response suppression, and localization behavior.
- Focused changed-file lint:
  - PASS
- English and Arabic locale files:
  - PASS JSON parse
- `git diff --check`:
  - PASS

## Headed isolated-browser acceptance

Environment: disposable synthetic Ahram tenant `15150000-2026-4015-a000-000000000001` through `http://admin.localhost:28180`.

Fixture:

- Product: `QA-AHRAM-AFFILIATE-E2E-20261115`
- Source price: EGP 1,000.00
- Category selection: Electronics → TVs
- Custom CommerceHub price entered: EGP 1,200.00
- Invalid parent-as-final-category preview request: HTTP 400
- Authoritative preview without a configured rule: HTTP 409
- CommerceHub write requests observed: 0

English evidence:

- `isolated-headed-en.png`
- `isolated-headed-en-evidence.json`
- Direction: LTR
- Custom price input enabled
- Missing-rule blocker shown in English
- Publish disabled
- No horizontal overflow

Arabic evidence:

- `isolated-headed-ar.png`
- `isolated-headed-ar-evidence.json`
- Direction: RTL
- Missing-rule blocker and commission breakdown shown in Arabic
- No detected English commission fallback strings
- Publish disabled
- No horizontal overflow

The browser session response ID was normalized only inside the isolated browser because the pre-existing test-auth fixture ID is not valid frontend UUID version syntax. Backend requests retained the real short-lived test bearer authority.

## Known environment constraints

- `/health/ready` returns 503 only because the isolated network cannot resolve the external DNS probe. API/Web containers report healthy and feature endpoints respond successfully.
- The isolated database intentionally does not contain Production user credentials. A Production login attempt there correctly returned invalid credentials; the password was not requested, captured, or stored.
- Production was not touched.

## Release decision

The implementation is suitable for feature-branch delivery and review. Do not deploy or merge to Production until:

1. an authorized platform administrator configures a real applicable commission rule in the target environment;
2. resolved commission preview is verified without inventing a fallback rate;
3. authorized read-only Production acceptance is completed; and
4. immutable API/Admin Web candidates pass the normal security and deployment gates.

Storefront must remain unchanged.
