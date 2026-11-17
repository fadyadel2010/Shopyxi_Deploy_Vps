# Accounting authority and reporting contract

This is a source investigation, **not a certification of the accounting policy**.
Identifiers and numerical examples below are synthetic unless explicitly
identified as the supplied global-rule ID. No production customer/order rows
or financial amounts are included.

## Architecture

```text
POS:
SalesController -> SalesInvoices / invoice items / payment authority
 -> CommerceAccountingService.PostPosSaleAsync
 -> AccountingOutboxMessages + LedgerJournalEntries / LedgerEntries
SalesReturnsController -> return/reversal accounting

Storefront:
Orders (Source=Ecommerce/POS/etc.) + payment lifecycle
 -> order accounting/settlement services -> general ledger

CommerceHub:
CommerceHubListings.CommerceHubPrice (independent nullable override)
 -> checkout price authority, not a write to ProductVariant source price
 -> CommissionService (SQL vendor > category > global authority)
 -> immutable sub-order/item commission snapshots
 -> CommerceHubMarketplaceOrders -> CommerceHubSubOrders / SubOrderItems
 -> online collected payment OR COD delivery/collection workflow
 -> CommerceHubVendorEarnings + CommerceHubFinancialTransactions
 -> completed CommerceHubRefunds / reversal entries
 -> vendor debt/payable/settlement/payout services
 -> CommerceHubDailyFinancialAggregateService (derived daily projection)
 -> SuperAdminFinancialDashboardService
 -> SuperAdminFinanceController /api/super-admin/finance/*
 -> financeService.ts -> FinanceDashboardPage / KPI / charts / tables / integrity
```

There is no automatic rule that a POS sale earns CommerceHub commission.
Shared product/listing references do not make the sales populations identical.
Marketplace Mongo/search/order projections are not financial authority for
these Finance queries.

## Recognition authority

`CommerceHubFinancialWorkflowService.ApplyOnlinePaymentSuccessAsync` applies
snapshots/earnings and calls `RecordOrderAsync` for online sub-orders.
`ApplyDeliveredFinancialsAsync` collects COD and posts its financial entries.
Delivery changes earning availability; it is not the original recognition date
for an already-collected online order. Vendor payout/settlement is not required
for initial platform commission reporting.

`CommerceHubFinancialLedgerService.RecordOrderAsync` writes positive-amount
entries only. Online has platform OrderPayment/Credit and vendor proceeds;
COD has vendor cash OrderPayment/Credit and commission debt. A zero-rate order
legitimately has no Commission entry.

Commission snapshots are frozen at checkout; historical amounts must not be
recalculated using today's active rule. Commission applies to **item product
subtotal**, excluding shipping, with item amounts rounded then summed.
Merchant earning is `sub-order Total - gross commission`; the existing promo
contract funds the platform commission, not a merchant-funded POS discount.

## Dashboard metrics (corrected implementation; validation recorded separately)

All financial overview/trend filters use UTC. Date-only filters expand to
inclusive UTC day boundaries; explicit UTC timestamps preserve the time.
The upper bound is capped at the request's UTC snapshot time. Currency
authority is EGP, not a currency conversion service. .NET decimal money rounds
to 2 places away from zero; ratios round to 4 places away from zero.
PostgreSQL amounts use numeric(18,2); commission rates have greater precision.

| Metric | Authoritative entity/record | Recognition/date | Implemented formula and treatment | Authority / concern |
|---|---|---|---|---|
| Gross Commission | CommerceHubFinancialTransactions, Platform/null, Order/Commission/Credit; SubOrders and parent order joins | Ledger CreatedAtUtc in range | Sum recognized original commission, before promotions and refunds | Live ledger or validated daily projection; initial implementation counted rows, omitted zero-rate orders |
| Promo Cost | Platform Order/Promotion/Debit less completed Refund/Promotion/Credit | Each ledger entry's CreatedAtUtc | Original platform funding minus promo expense reversals | Live signed amount; daily projection initially clamped negative daily net to zero |
| Net Commission | Same ledger populations | Same event window | Gross Commission − Promo Cost | Before refund commission debit; promo reversal may make net promo cost negative |
| Net Revenue | Same commission + promotion + refund ledger populations | Each event's own ledger date | Gross Commission − net Promo Cost − gross Commission reversal debit | Refund entity retains separate net/promo fields; operational commission debit is their gross sum |
| Refund Value | CommerceHubRefunds, Completed only, joined sub-order/parent, not deleted | ProcessedAtUtc, otherwise UpdatedAt, otherwise CreatedAt | Sum Amount; Pending, Processing, Failed, ReconciliationRequired excluded | Source-entity backed, not proof of completed external cash disbursement by itself; fallback date can move with updates |
| AOV | Recognized sub-order totals / recognized count | First canonical financial recognition date | Sum SubOrders.Total including shipping / Orders in Scope; no deduction of refund principal | Distinct sub-orders; one deterministic non-deleted promo usage joins each order |
| Orders in Scope | Distinct recognized sub-order/currency with commission credit, or canonical payment credit when the frozen commission is zero | First ledger recognition date, capped at snapshot | Count distinct recognized sub-orders | Includes collected, settled, partially refunded and refunded payment states; pending/failed/cancelled excluded |
| Refund Rate | Distinct recognized-in-window sub-orders with a completed refund also in the window / recognized sub-orders in the window | Same recognized cohort | Ratio, returns 0 if denominator 0 | Bounded cohort ratio, not eventual lifetime return probability; window-distinct source query, never summed daily distinct counts |
| Revenue Drag | Platform Refund/Commission/Debit | Ledger CreatedAtUtc; refund must be Completed | Sum commission reversal debit, never refund principal | Depends on resolving net-vs-gross reversal contract above |

Source service methods: `GetOverviewAsync`, `GetTrendsAsync`,
`GetPromoAnalyticsAsync`, `GetVendorPerformanceAsync`, `GetRefundAnalysisAsync`.
Query builders and filter/date helpers are in
`PointOfSale.API/SuperAdmin/Services/SuperAdminFinancialDashboardService.cs`;
daily selection/projection is in its `.Aggregates.cs` partial.

## Refund contract discrepancy and correction

The existing calculator explicitly returns both:

- `GrossCommissionReversalAmount`.
- `CommissionReversalAmount` (final/net commission after promo).
- `PromoDiscountReversalAmount`.

`ValidateRefundFinancialIntegrity` enforces
`refund principal - vendor reversal = net commission reversal + promo reversal`.
The original ledger writer posted **net** `refund.CommissionReversalAmount` as a
Commission/Debit and separately posts Promo/Credit. The dashboard subsequently
subtracts the net debit and reduces promo cost by the credit.

Synthetic full-refund example: gross commission 10, platform funding 3,
original net commission 7; refund stores net reversal 7 and promo reversal 3.
The original dashboard formula yielded `10 - (3 - 3) - 7 = 3`, not zero.
This is not cured by formatting, excluding real records, changing historical
amounts, or running an aggregate rebuild. The write/read contract must agree
on whether the commission debit is gross or net and how funding is restored.
The general ledger bridge, debt calculation, reconciliation, and reporting
must all be validated together before a financial posting contract change.

The original calculator also treated a stored final commission of zero as a fallback
to gross commission. For a fully funded commission this is ambiguous/unsafe:
the legitimate final zero cannot be distinguished from a legacy missing value
using a `> 0` check alone. Do not manufacture a replacement historical amount.

The corrected writer debits gross commission 10 and separately credits promo 3,
so `10 - (3 - 3) - 10 = 0`. Reconciliation expectations and the decision engine
use gross reversal consistently. The accounting outbox is staged in the same
writer transaction. No historical entry is edited, no migration is required,
and production has no CommerceHub entries requiring a corrective posting.

Cumulative quantity/commissionable-principal allocations subtract rounded
before/after entitlements. Promo cents are allocated from the rounded gross
entitlement; net is gross minus promo. This conserves cents across repeated
partial refunds and prevents reversing the whole original commission on the
last partial refund. A fully platform-funded net zero remains zero.

The daily projection retains negative net promo cost on refund-only dates.
Only exact complete UTC days can use daily cache; partial-day/current-day
windows use live queries. Derived daily counts are not authoritative for
window-distinct refunded-order counts.

## Integrity limitations

The selected-date screen calls a **scoped** integrity scan. Global
payable/receivable/payout/adjustment, idempotency/correlation, duplicate-pair,
currency, and entity-scope checks are only populated in the unfiltered global
read-service path. Zero-valued omitted counters are not evidence of a check.
Order source integrity filtering initially used sub-order **CreatedAt**,
whereas revenue uses ledger recognition time. A previously created order
recognized in this period can therefore be missed by the scoped source check.
The corrected scope uses first ledger recognition when present and retains
creation time as a fallback for missing-ledger diagnostics.

Aggregate issue flags inspect persisted `!IsValid` stamps; they are not the
live drift reconciliation used when deciding whether a fresh cache is safe.
A cached projection passing its selection guard does not prove all financial
invariants. Global debit/credit equality is explicitly not applicable to the
operational bucket ledger.

## Authorization

`SuperAdminFinanceController` inherits `SuperAdminBaseController`, which
requires the named Super Admin policy. Program.cs binds this policy to the
separate SuperAdmin authentication scheme, authenticated role and auth-type
claim. Platform-authority middleware revalidates current SQL identity,
governance and token version. `[AllowAnonymousTenant]` deliberately permits
platform-wide scope; it is **not** `[AllowAnonymous]`.

POS-specific finance and CommerceHub merchant endpoints must use their own
tenant authority. Direct service tests do not prove end-to-end middleware
authorization; production anonymous probes and isolated policy tests are
reported separately.

## Production evidence interpretation

`production-before.json` contains only sanitized aggregate counts, booleans,
deployment metadata and immutable images. All-time absence includes deleted
marketplace records, so date boundaries, recognition status, tenant filtering
and cache lag cannot explain missing marketplace revenue in this snapshot.
Nevertheless this empty dataset cannot exercise a real marketplace refund,
commission, promotion or settlement lifecycle. Passing vacuous invariants
must never be presented as transaction-level production certification.
