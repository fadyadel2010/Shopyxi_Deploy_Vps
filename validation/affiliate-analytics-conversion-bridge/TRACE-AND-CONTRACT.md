# Pre-implementation trace and conversion contract

Baseline: `ddfbe18ee55a965f1f8ff8c2b0b9e1e8cf196158` (freshly fetched remote main).

## Proven source path

1. `/p/{referralCode}/{slug}`, Storefront product requests and `/api/storefront/landing/{referralCode}/{slug}` reach `AffiliateReferralMiddleware.InvokeAsync`. Current SQL promotion/product/profile admission selects the referral. Active canonical cart/checkout first touch wins over a new candidate; otherwise an admitted new product entry may replace cookie fallback.
2. `AFF_ATTR` is DataProtection-protected affiliate/link/tenant evidence. `AFF_SESSION` is only deduplication, **not authenticated customer/session authority**. Middleware emits `AffiliateLinkClickedEvent`; `AffiliateLinkClickedConsumer` generates a new Mongo ID. That ID is not returned to trusted cart context or persisted in SQL.
3. `AffiliateShoppingJourneyService`, `AffiliateShoppingJourney`, `CartService` and checkout `ItemsSnapshotJson` preserve tenant/profile/code/link, but **no click ID**.
4. `CheckoutService` final serializable transaction revalidates SQL referral authority, persists `Orders` and `AffiliateAttributions` inline (lines 7104–7137), then publishes `AffiliateOrderPlacedEvent` before the same transaction commits (line 7237). Existing publisher/outbox infrastructure provides the durable transport boundary.
5. `AffiliateOrderAttributionConsumer` calls `AffiliateService.AttributeOrderToAffiliateAsync`. Its existing-attribution branch (lines 817–849) returns without emitting `AffiliateAttributedEvent`. Checkout already created that attribution, so the `AffiliateClickConversionConsumer` subscribed to `AffiliateAttributedEvent` is never triggered by this normal path.
6. Even if triggered, that consumer guesses the latest unconverted tenant/profile click, catches Mongo failures as noncritical, increments a separate summary without a stable order receipt and uses processing time. The manual-payment consumer repeats that unsafe guess. Neither is an acceptable repair path.
7. `AffiliateAnalyticsController` summary/trend currently use `affiliate_analytics_daily`: clicks count click-event deliveries, orders count commission-earned events. Click history instead uses `affiliate_clicks`, deduplicated by tenant/session/product. UI is `modules/affiliate/pages/portal/AnalyticsTab.jsx`. Thus the old conversion rate is not a committed-order conversion rate.

## Exact retained finding

`validation/affiliate-full-e2e/state-final-reconciliation-v2.json`:

- Click `3de85e14-3e61-4b8d-a964-a49aeb3f1ba7`, tenant `15150000-2026-4015-a000-000000000001`, profile `01a1df98-5257-7282-9e19-f9f8ae5b6b74`, product `37d9a1b6-6585-f655-0e55-824a517ce90c`, code `WWC3NC7R9B`, clicked `2026-11-15T14:34:21.073Z`: `orderId: null`, `convertedAtUtc: null`.
- SQL attribution `01a1dfa8-1e81-75e2-9645-fb71a6bd6f5c` and order `8f2866d0-cac0-4c0b-bd91-fe6f3c4c5e12` / `ORD-1515-2026-01000` agree on tenant/profile/code/promotion link `01a1dfa0-8334-755b-aeb8-c037243e70c0`.
- Checkout `5e1163f8-476f-4647-840c-cdf6a5948cc5` contains that same journey, **but no click ID**. Matching those identifiers alone does not prove an individual click. Historical link repair must not guess.
- The historical 109 dashboard clicks are retained click-event projection counts; the retained snapshot and readiness click-safety review prove **one deduplicated click document**, not 109 documents, visitors, customers or orders. Historical evidence will not be rewritten.

## Contract to implement

- **Attributed orders**: unique committed SQL orders with immutable Affiliate attribution. Order creation timestamp is the authoritative persisted milestone timestamp, only projected after commit; not worker wall-clock time or a claim of exact database commit-clock measurement.
- **Order conversion** is explicitly reported separately from the old payment-gated ranking signal. No payment, fulfillment, earnings, eligibility or settlement is inferred. Existing manual-payment ranking/commission admission is not broadened.
- Click-to-order: one accepted signed referral touch per shopping journey and at most one order per touch; never overwrite a linked click. A subsequent order needs a new server-issued touch. An old uncorrelated attribution may be counted as an attributed order, but its click remains unlinked/unknown.
- Preserve active cart/checkout first touch and current SQL referral admission. New correlation comes only from server-issued protected evidence and the canonical cart, never a request click/affiliate/tenant/product/order identifier.
- Click records count actual `affiliate_clicks` documents. Tracked historical event totals are a distinct legacy metric, not silently relabelled as visitors. No reliable unique visitor count is available.
- Converted clicks count exact linked documents. Attributed-order conversion rate is unique projected attributed orders / tracked click records × 100, with explicit definition and zero denominator yielding unavailable, not fabricated sales. UTC calendar-day filters use event milestone time, not delayed processing time.
- Cancellation/refund retain historical committed attribution/conversion; payment/refund/commission fields continue to use their existing independent authorities.
- Use the existing durable order-placed event/outbox, SQL revalidation, conditional Mongo updates, and existing broker retry/error-queue behavior. Missing clicks and conflicts fail/retry, never silently acknowledge success. Report counts derive from persisted projections, never event queue depth or non-idempotent increments.

No financial lifecycle or publishing-readiness suite is rerun. Production business transactions remain prohibited.

## Final implementation clarification

The new projector emits a separately order-ID-deduplicated product-intelligence conversion count with no earnings increment. That count represents committed attribution, not payment verification or commission eligibility. Financial/manual-payment consumers keep their independent admission. The historical source diagnosis and intended financial separation above are retained.
