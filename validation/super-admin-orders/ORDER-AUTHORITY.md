# Orders authority and lifecycle contract

Classification: **PASS — source investigation**, not evidence that production
contains or has exercised any marketplace lifecycle.

## Population and flow

```text
CommerceHubCustomer + tenant-owned Product/Variant + CommerceHubListing
  -> CommerceHubCheckoutService (SQL authority, idempotency, stock reservation)
  -> CommerceHubMarketplaceOrders (one customer checkout / parent order)
       -> CommerceHubSubOrders (one live store allocation per parent)
            -> CommerceHubSubOrderItems (frozen purchase lines)
       -> CommerceHubPayments -> CommerceHubPaymentAttempts / PaymentLogs
       -> CommerceHubRefundRequests -> CommerceHubRefunds / execution / return records
  -> lifecycle/payment services -> SQL + financial/inventory/audit/outbox effects
  -> derivative Mongo/search projections (NOT Orders screen authority)
  -> SuperAdminOrdersService -> SuperAdminOrdersController
  -> ordersService.ts -> OrdersPage / OrdersFilters / OrdersTable / OrderDetailsDrawer
```

The Orders screen uses only CommerceHub SQL entities. `Orders` (including its
`OrderSource` classification), `SalesInvoices`, and POS returns are separate.
No POS invoice is imported into a marketplace KPI. CommerceHub origin is
explicit in the entity population, not inferred from a shared product ID.

Parent ID is `CommerceHubMarketplaceOrder.Id`; parent human number is
`OrderNumber` (SQL-unique). Children bind `MarketplaceOrderId`; the live
`(MarketplaceOrderId, StoreId)` pair is SQL-unique. `StoreId` is the tenant/vendor
identity. `StoreName` is the historical checkout snapshot, not a join to today's
merchant display name. Listing/product/variant/category IDs and prices are
frozen per item; seller and customer use distinct identity domains.

Counts query parents directly. Store selection uses `Any` on live children, not
a multiplying join. A selected store returns the **whole parent and all its
live splits** because this is platform-wide Super Admin inspection, not a
merchant-only view. The UI states this explicitly. Soft-deleted parents are
excluded; soft-deleted children/items/payments/refunds/history are excluded from
their detail populations. Cancelled/refunded orders are retained unless filtered.
No separate archive flag was found on these order entities.

Orders list, stats, details and export use repeatable-read, read-only PostgreSQL
transactions. Their multiple queries see one consistent database snapshot.
No Orders Redis payload, Mongo document, search index or financial daily
projection is used after remediation. Browser response timestamp is not the
order snapshot timestamp: `meta.asOfUtc` records the read's snapshot observation;
`meta.timestamp` is the response envelope timestamp. These are observation times,
not the time the last business event happened.

## Supported lifecycle (no invented enum statuses)

`OrderStatus`: Pending, Confirmed, Processing, Shipped, Delivered, Completed,
Cancelled, Refunded, OnHold. Packed, Returned, Failed, ReadyForShipment,
AwaitingPayment and PaymentPending are **not persisted OrderStatus members**.
Payment and return/refund workflow states are separate contracts.

| Transition | Actor / command authority | Preconditions / effects |
|---|---|---|
| Create -> Pending | Authenticated CommerceHub customer; checkout service | Current SQL listing/catalog, stock, currency, governance and payment method authority; transactional parent/child/item creation, frozen prices/commission/cost, reservations, checkout idempotency and integration event |
| Pending/OnHold -> Confirmed after collection | Verified provider callback through canonical payment service and financial workflow | Exact local payment/order/amount/currency/provider identity, reservation authority, locks; collected state, paid time, earnings, immutable ledger recognition, completion/status/notification events |
| Pending/Confirmed -> Processing | Tenant Owner/Admin/Manager, `PATCH /api/commercehub/suborders/{id}/status` | Own store, vendor governance, SQL row locks, optional expected projection version, collected/settled online or COD readiness |
| Processing -> Shipped | Same merchant command | Same guards, persisted item/child/parent status and history, projection-versioned events |
| Shipped -> Delivered | Same merchant command | Active correct inventory lineage, inventory delivery commit (stable per-item event ID); frozen COGS outbox, delivered time, earning availability; COD collection and financial recognition |
| Pending/Confirmed/Processing/Shipped/OnHold -> Cancelled | Same merchant command | Own store/current version/allowed edge; release reservations with stable per-item event ID, update item states/history/aggregate/events. Cancellation is **not itself proof of a monetary refund** |
| Same -> Same | Same command, authorized/current version | Idempotent early return; no additional history or inventory effect |
| Failure during online payment | Verified callback | Preserve failed payment/attempt evidence, set order/online children to retry hold where supported, reset payment lifecycle to Pending, release reservations; no failed-attempt commission recognition |
| Refund Pending -> Processing -> Completed/Failed | Refund service / execution service, not this Orders page | Scoped idempotency, row locks, eligibility/return/external execution authority, bounded amount/quantity; completed refund reverses frozen economics and updates payment state |
| Refund uncertain -> ReconciliationRequired | Refund execution/recovery authority | Unknown external outcome is not declared completed and must not blindly repeat external money movement |
| Partial refund -> PartiallyRefunded payment | Refund completion authority | Bounded cumulative principal/quantity; original order status is not replaced by an invented partial-refund OrderStatus |
| Full refund -> Refunded order/payment | Refund completion authority | Completed and bounded persisted refund; sub-order and aggregate status update, earnings adjustment and financial ledger effects |

Delivered/Completed/Refunded are locked against the merchant transition command;
Cancelled/Refunded have no resurrection edge. `Completed` is a supported enum /
aggregation value, **not an advertised merchant “complete” operation**.
Seller acceptance/rejection are not separate order states in this command
contract. There is no partial-cancellation enum: the persisted child statuses
show the split; the parent is the code's aggregate.

`CommerceHubOrderStateHelper.AggregateMarketplaceStatus` uses all child
statuses, with terminal all-cancelled/all-refunded handling, completed/delivered
combinations, then shipped, processing, confirmed, on-hold and pending precedence.
A delivered child mixed with a cancelled child currently aggregates to Shipped,
not an invented partially-completed state. Inspection retains child authority;
this aggregation policy is documented, not silently redefined by this audit.

Merchant lifecycle mutation uses SERIALIZABLE transactions, locks the parent
then its children/items in deterministic ID order, checks optional expected
`ProjectionMutationVersion`, writes `CommerceHubSubOrderStatusHistory`
(old/new/reason/correlation/time) and publishes versioned authoritative events
before SaveChanges for the outbox. History does not contain a dedicated actor-ID
column; role/tenant/governance admission and surrounding request/event audit are
separate. Do not misrepresent history alone as a complete actor audit.

Inventory reservation and delivery event IDs are deterministic; ledger and
checkout/payment/refund contracts have unique idempotency identities.
Rejected commands and failures roll back the transaction. SQL SERIALIZABLE can
reject concurrent writers; clients must reload/retry under the established
command contract. Super Admin Orders never invokes these commands.

## Payments

Source: `CommerceHubPaymentService`, `CommerceHubPaymentLifecycleAuthority`,
`CommerceHubFinancialWorkflowService`, payment/refund EF constraints.

Payment records have Pending/Paid/Failed/Ignored/RefundRequired/Refunded/
RefundFailed states; order payment lifecycle has Pending, Authorized, Collected
(legacy Paid alias), PartiallyPaid, Failed, Refunded, PartiallyRefunded,
AwaitingVerification, Rejected, Settled, CashOnDelivery, Expired, AmountMismatch
and development-only SyntheticQaCollected.

The page displays the stored order/child payment state, not the “worst”
historical attempt. Attempts remain separate evidence. A failed callback can
leave the order OnHold + Pending for recovery; it is **not automatically an
order with a final failed payment state**. Therefore a failed-attempt count may
legitimately exceed the failed-order metric.

Online provider callbacks, not the browser return URL, admit collection. The
service validates signature/adapters before outcome application, amount/currency
and provider identifiers, and locks payment/order in a transaction. Processed
guards prevent duplicate recognition; an already-collected order receiving a
different success is routed to compensation rather than credited twice.
Uniqueness includes provider reference, provider/intent, non-null transaction,
one active Paid payment per order, and `(PaymentId, AttemptNumber)`.
Payment logs preserve protected payload evidence. Read DTOs never include
PaymentToken, CheckoutUrl, raw request/response payloads or order access tokens.
Provider references are identifiers, not authorization credentials.

Real-service PostgreSQL testing also found that verified pending Paymob callbacks
attempted to insert their protected provider log without entering the required
payment-write boundary. The pending-evidence branch now enters `BeginPayment`
after signature verification/payment resolution, like the other admitted
payment-evidence paths. The adjacent authorization-only evidence branch had the
same omission and now enters its own equally narrow payment boundary. Neither
branch recognizes collection; dedicated tests cover both evidence-only outcomes
and rejected-signature no-write behavior. Pending still returns pending with no
collection,
commission recognition, stock delivery or balance mutation.

Retry preserves routing reference; unresolved provider/external-money evidence
blocks creation of another session. External session attempt limits and
reservation reacquisition/readiness gates apply. A processed failure can be
reconciled only under the explicit service guard; the historical failure attempt
is retained. Refund-required/reconciliation paths are not successful collection.
COD starts unrecognized and is collected by delivery financial workflow; it
does not fabricate an online provider payment. Partial payment is a legacy
enum, not a newly implemented split-tender feature.

## Prices, commission and accounting

The independent CommerceHub listing override (`CommerceHubPrice`) goes through
`CommerceHubPriceAuthority`; POS/Storefront source variant prices are not
overwritten. Checkout stores line UnitPrice/Total and item/sub-order commission
context. Commission resolution is SQL vendor > category > global; within scope,
priority descending with deterministic dates/IDs and ambiguity rejection.
The current 10% global rule is not assumed for every historical order.

Purchase snapshots must not be repriced with current listing/rule values.
Read paths sum stored fields only. Frozen product commission excludes shipping;
vendor proceeds include the established shipping pass-through. Promotions and
refund reversals preserve the Finance contract in
`../super-admin-finance/ACCOUNTING-MAP.md`. This audit changes no Finance service,
ledger writer, commission rule, price or stock.

## Summary formulas after remediation

All today windows are `[UTC midnight, next UTC midnight)`. All exclude deleted
parents; collection totals additionally exclude deleted children. EGP is the
database-enforced marketplace currency, not browser FX conversion.

| Card | Exact source/formula | Scope / exclusions |
|---|---|---|
| Total orders today | Count parent orders by CreatedAt in UTC day | All persisted statuses; selected store uses live-child Any; never count sub-orders as additional orders |
| Pending orders | Count parents with Status=Pending | All time, selected-store membership; Confirmed/OnHold/failure/cancelled/completed are not silently included |
| Orders with failed payments | Count parents with PaymentStatus in Failed/Rejected/Expired/AmountMismatch | All time, selected-store membership; no attempt joins or retry multiplication |
| Captured sales today | Sum child Total whose PaidAt is in UTC day and payment state is Collected/Settled/Refunded/PartiallyRefunded | Gross collected value including shipping, before refunds; optional selected-store allocation. Not platform revenue/commission, not net cash settlement |

The original “Revenue today” formula was Sum(parent TotalAmount) by creation
date regardless of payment/status. It has been replaced and explicitly relabeled.
The new sales total uses child collection time so split COD delivery on different
dates is not recognized wholly on the last parent's PaidAt date.

Only store selection applies to summary cards. Search/status/payment/from/to
apply to table and export. The UI states both scopes. Store selection returns
full parent rows but collection KPI only the selected store's allocations.

## Queries, refresh and export

Search: exact Guid identity plus literal case-insensitive number/name substring
(`ILIKE`, parameterized; LIKE metacharacters escaped). No unrestricted phone/
address/token search. Super Admin alone can invoke this platform-wide query.
Dates expand to inclusive UTC calendar days; ToDate uses exclusive next
midnight. No browser-local timezone conversion is applied to the query. Localized
display timestamps use the existing locale formatter. Native `type=date`
maintains ISO values; `mm/dd/yyyy` is browser-native chrome, not an application
placeholder contract. Accessible date labels and language are set explicitly.

Eight supported sort fields use deterministic ID tie-breakers. Offset pagination
is stable for one dataset/snapshot; concurrently inserted/mutated orders can
still shift offsets across separate refresh requests. It is not a frozen cursor
export. Empty population has totalPages=0 and no invented page/range; stale or
invalid pages are clamped in the UI after valid metadata. Server global
`ApiRequestNormalizationFilter` normalizes page/pageSize before controller
validation; other invalid input is rejected, not mapped to Unpaid or Pending.

List and stats poll every 30s only in the active tab; React Query owns timer
cleanup and same-key deduplication. Manual refresh preserves query URL and page,
uses non-cancelling refetch to avoid replacing an active request, and now reads
SQL rather than the former 45s list / 20s stats caches. Snapshot age >90s,
disconnection/hidden tab and request errors cannot show green active monitoring.
Unavailable summary metrics use em-dashes rather than fabricated financial zero.
No order/payment/stock mutations occur; GET view/export audit records are expected
operational writes and are distinguished from business-data writes.

CSV covers the **entire filtered parent population**, not just the page. Same
sorting/filter query, maximum 10,000 records with explicit 422 rejection, bounded
materialization, cancellation, repeatable-read consistency, UTF-8 BOM, invariant
two-decimal amounts and ISO UTC timestamps. Formula-like leading characters,
including whitespace-prefixed `= + - @`, are apostrophe-neutralized; quotes and
newlines are CSV-escaped. No tokens, phones or addresses are exported. CSV still
contains permitted customer names: treat the download as controlled customer
data. The UI calls the authenticated `/api/super-admin/orders/export` blob route,
rejects HTML fallback and reports success/failure. Machine-stable English column
names and enum codes are intentional; Arabic data is UTF-8.

## Details / security

Orders endpoints: list, stats, export, detail GET only. Inherited named
RequireSuperAdmin policy uses dedicated SuperAdmin JWT/cookie scheme, role,
auth-type and current SQL platform identity/governance/token-version middleware.
AllowAnonymousTenant permits platform tenant scope, **not anonymous access**.
POS Owner/Admin/Manager uses a different scheme/tenant authority; merchant
commands reject foreign StoreId. Customer reads bind CustomerJwt/customer ID.
Unknown order IDs are scoped and return not-found/validation responses only
after authorization. No mass-assignment business command exists on this module.

Details expose persisted lines, frozen gross commission/vendor earnings,
payment records and ordered attempts, refund requests per line, refund execution
history and persisted status history. No shipment registration/capture/refund/
cancellation buttons were added. A shipment's carrier-level timeline is not
implemented on this drawer; Shipped/Delivered child status is not claimed as a
carrier-proof timeline. Drawer is keyboard-modal with Escape, focus trap/restore,
and no hidden focusable drawer when closed.
