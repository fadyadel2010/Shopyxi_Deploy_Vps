# Public claim review — 2026-11-25

Authority: the user's scoped Marketing remediation and production-deployment request,
conditional on factual source evidence and technical readiness. This is an agent source
review, **not** approval by the historical register's proposed PO/RO/FO/AR reviewers.
`claims.json` is the unchanged historical proposal register. `publication.json` is
the current public allowlist and exact conservative EN/AR overrides. Rendering an
excluded claim throws; public markup says SOURCE_SUPPORTED, not reviewer APPROVED.

## Source anchors and boundaries

Paths below are repository-relative, read-only evidence, never Marketing runtime imports.
The existing claim register supplies the E01–E38 mapping and qualifications.

| Public claims | Existing source evidence | Retained boundary |
|---|---|---|
| C01–C03, C43, C50–C51 | `PointOfSale.Web/src/App.jsx`, `PointOfSale.Marketing/src/routes.mjs` | Four product areas; editorial positioning, no growth guarantee |
| C07–C12, C15, C44 | `PointOfSale.API/Controllers/ProductsController.cs`, `ProductVariantsController.cs`, `WarehousesController.cs`, `StockTransfersController.cs`, `PurchasesController.cs`, `SuppliersController.cs`, `CustomersController.cs`, `StatementOfAccountController.cs`, `SalesController.cs`, `SalesReturnsController.cs`, `PurchaseReturnsController.cs` in the same Controllers directory | Recorded operations, permitted workflows; C15 public copy is warehouses only, no branch or consultation offer |
| C13–C14, C16 | `PointOfSale.API/Controllers/FinancialReportsController.cs`, `ReportsController.cs`, `AdminUsersController.cs`; `PointOfSale.Core/Security/PosPermissions.cs` | Permission/data-dependent reports, no audited books or arbitrary roles |
| C17–C21, C45 | `PointOfSale.Storefront/src/app/router.tsx`; `PointOfSale.API/Controllers/CheckoutController.cs`, `StorefrontAccountController.cs`, `AdminStoreConfigController.cs`; `theme-authority/shopixy-theme-authority.json` | Supported finite controls; order submission is not money collection |
| C04, C22–C25, C29, C38, C46 | `PointOfSale.API/Controllers/AffiliatePromotionLinksController.cs`, `AffiliatePortalController.cs`, `AffiliateManualPayoutController.cs`; `PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs`, `AffiliateCommissionEligibility.cs`, `AffiliateSettlementOrchestrator.Manual.cs`; `PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateOrderConversionConsumer.cs` | Own account, eligible products, committed orders, separate approval/payment; no automatic bank execution |
| C05, C32–C35, C47 | `PointOfSale.API/Services/CommerceHub/CommerceHubSellerApplicationService.cs`, `CommerceHubPublicationEligibilityPolicy.cs`, `CommerceHubCheckoutService.cs`; `PointOfSale.Core/Pricing/CommerceHubPriceAuthority.cs`; `PointOfSale.CommerceHub/src/router/index.tsx` | Approved merchant/listing eligibility, separate marketplace order domain; no white-label offer |
| C06, C19, C31 | Above account/order services; `PointOfSale.Infrastructure/Consumers/CommerceHubProjectionConsumer.cs` | Separate identities, orders and projections; no synchronous-everywhere promise |
| C26–C27a, C30, C37 | `PointOfSale.API/Services/Payments/StorefrontPaymentRuntimeReadinessService.cs`; `PointOfSale.Infrastructure/Shipping/CarrierCapabilityRegistry.cs`; `PointOfSale.API/Middleware/HostResolutionMiddleware.cs`; `PointOfSale.Core/Pricing/CommerceHubPriceAuthority.cs`; bilingual product route sources above | Configuration/provider/localization limits, current EGP writes; C27 public consultation sentence omitted |
| C28, C40, C48, C52–C55 | Marketing routes, implemented local links, existing `https://admin.shopyxi.com/register` navigation | Editorial invitation/navigation only. C53/C55 public copy does not claim a free offer, activation, duration or universal entitlement |

C36/C39/C41/C42/C49 are excluded: no evidenced consultation, demo receiving,
success, or contact process. The public render never composes their review-only
section. Historical PENDING records remain evidence of what was **not** separately
approved. No customer testimonials, outcome metrics or external endorsements added.

Screenshot hashes/crop bounds and synthetic disclosures remain controlled by
`product-screenshots.json` and `verifyScreenshots`; no screenshot pixels are edited.
Original capture/provenance files are retained in the original local evidence
workspace. During this remediation all 20 public images were decoded and compared
pixel-for-pixel with crops of originals selected by their exact recorded SHA-256;
all matched. Their absence from GitHub is not represented as third-party approval.
