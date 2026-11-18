# FINAL REPORT â€” Mina Store Storefront Product Visibility Incident

Date: 2026-11-18 (UTC)
Branch: `storefront/mina-store-product-visibility-20261118`
Policy/application commit: `d64f213bacbde85bf17c153a5a0b5dc8cf9a97f4`

## 1. Executive summary

Two products that were active and published in POS Admin were absent from the
`mina-store` public storefront because the entire MassTransit transactional
outbox was head-of-line blocked at sequence 282. The head message was
`PointOfSale.API.Events:CheckoutStartedEvent`, and the RabbitMQ user
`shopixy-app` had configure/write/read permission only for
`PointOfSale.API.Events:Cart*` API exchanges. MassTransit could not declare or
publish the head exchange, so none of the 222 queued messages â€” including all 27
product events â€” were delivered. MongoDB and OpenSearch therefore never received
the two Mina Store product projections.

The earliest proven fix was a RabbitMQ permission policy correction. The API
event namespace was broadened to
`PointOfSale\\.API\\.Events:[A-Z][A-Za-z0-9]*(?:Event|EventBase)` for
configure/write/read, applied through the management API without a broker
restart. The outbox drained automatically. Both products now exist exactly once
in MongoDB and OpenSearch, and the live storefront API and headed browser show
both products.

No product, variant, price, stock, category, tenant, order, payment, DNS,
ingress, certificate, or application image was changed. No application rollout
was required.

## 2. Original screenshot observations

| Screenshot | Observation |
|---|---|
| POS Admin dashboard | Store `mina-store`, total products 2 |
| `https://admin.shopyxi.com/products` | Two products, both Active and Published: simple `Ù…Ù†Ø¸Ù ÙˆØ§Ù„Ø²Ø¬Ø§Ø¬Ø§Øª Ø¨Ø§Ø¨Ù„ Ø¬ÙˆÙŠ` and variable `test` |
| `https://mina-store.shopxyi.com/products` | Store `mina-store`, All Products, `0 results`, `No products yet` |

The screenshot hostname uses `shopxyi` (y before i). That spelling does not
resolve publicly. The verified canonical hostname is
`https://mina-store.shopyxi.com` (see section 4).

## 3. Exact products and tenant identity

Tenant: `mina-store`, `059aa5c5-b9db-4dd6-bf44-4004d1eb96a1`, Active.

| Product ID | Name | Type | Active | Publish | Online visible | Searchable | Deleted |
|---|---|---|---|---:|---:|---:|---:|---:|
| `64986ae7-7fe9-13dd-4bfa-aa08ce5d8f65` | `Ù…Ù†Ø¸Ù ÙˆØ§Ù„Ø²Ø¬Ø§Ø¬Ø§Øª Ø¨Ø§Ø¨Ù„ Ø¬ÙˆÙŠ` | Simple | t | 1 | t | t | f |
| `c96999b9-0541-ea8b-69f2-3e3c4e17f6e3` | `test` | Variable | t | 1 | t | t | f |

Category: `082d9310-920d-4fc8-90d1-94f3e5291648` (`Ù‚Ø³Ù… ÙØ±Ø¹Ù‰`, not deleted),
parent `afa48c41-b281-4d25-9a43-ead35f4ecfd9` (`Ù‚Ø³Ù… Ø±Ø¦ÙŠØ³Ù‰`, not deleted).

Variants:

| Product | Variant | Price | Online price | Active | Default | Stock |
|---|---|---|---:|---:|---:|---:|---:|
| `64986ae7â€¦` | `3e5c9833-511f-46ee-8762-cd8400880f0c` | 11.00 | 111.00 | t | t | 100 |
| `c96999b9â€¦` | `ca5d9b7e-7620-4647-8cae-e31ce02db679` | 200.00 | NULL | t | t | 0 |
| `c96999b9â€¦` | `10c0f12f-bcdc-4027-b833-f34aa14757bf` | 200.00 | NULL | t | f | 0 |

`ProjectionMutationVersion` at baseline: 9 for the simple product, 5 for the
variable product. `LastSyncedAt` was NULL for both.

## 4. Hostname and tenant routing

| Check | Result |
|---|---|
| Screenshot host `mina-store.shopxyi.com` | Does not resolve |
| Verified host `mina-store.shopyxi.com` | Cloudflare A `104.21.80.200`, `172.67.153.141`; AAAA `2606:4700:3037::6815:50c8`, `2606:4700:3031::ac43:998d` |
| HTTPS | `HTTP/1.1 200 OK` |
| Tenant resolution | Storefront returned the `mina-store` catalog and tenant-scoped products |
| DNS/ingress/TLS changes | None; no defect proven in routing |

The hostname mismatch in the screenshot was a spelling error, not the incident
cause.

## 5. End-to-end data-flow map

```text
PostgreSQL Products / ProductVariants (authoritative)
  -> domain/integration events (PointOfSale.Core.Events:Product*)
  -> MassTransit EF transactional outbox (OutboxMessage / OutboxState)
  -> RabbitMQ vhost shopixy
  -> product-updated-event consumer -> MongoDB ecommerce_read_db.products
  -> product-updated-search-index / stock-price consumers -> OpenSearch products
  -> Storefront GET /api/storefront/products/discover
  -> React product list / detail pages
```

## 6. First failing pipeline stage

The first failing stage was **outbox publication to RabbitMQ**, before any
MongoDB or OpenSearch projection.

Evidence:

- `OutboxMessage`: 222 rows, sequences 282â€“503, 178 distinct outbox IDs.
- `OutboxState`: 178 rows, all `Delivered=NULL`, `LastSequenceNumber=NULL`.
- Head sequence 282:
  `urn:message:PointOfSale.API.Events:CheckoutStartedEvent`.
- RabbitMQ recorded 780 `access_refused` errors in about 50 minutes for
  `configure access to exchange 'PointOfSale.API.Events:CheckoutStartedEvent'`.
- The exchange did not exist.
- `shopixy-app` permission allowed `PointOfSale.API.Events:Cart*` only, plus the
  existing Core event/queue patterns.

Because the outbox is ordered, the denied head message prevented every later
message from being dispatched. Product records were correct in PostgreSQL and
were not the cause.

## 7. Root cause for each affected product

Both products share one root cause:

1. Their publication events were written correctly to the transactional outbox.
2. The outbox could not publish the earlier `CheckoutStartedEvent` because the
   RabbitMQ user lacked configure/write permission for that exchange.
3. The ordered outbox never advanced to the product events.
4. No Mongo/ES projection was written for either product.
5. The storefront correctly returned zero documents from an empty read model.

There was no second product-specific defect. Both products satisfied the
existing visibility rules (`IsActive`, `PublishStatus=1`, `IsOnlineVisible`,
`IsSearchable`, not deleted, valid category; the variable product has an active
variant with a positive effective price).

## 8. PostgreSQL source-state findings

Verified before and after repair; values are unchanged.

| Field | Product 1 | Product 2 |
|---|---|---|
| Tenant | `059aa5c5â€¦` | `059aa5c5â€¦` |
| Active / Publish / Visible / Searchable | t / 1 / t / t | t / 1 / t / t |
| Deleted | f | f |
| SalePrice | 11.00 | 0.00 |
| UpdatedAt | `2026-11-18 14:20:41Z` | `2026-11-18 14:23:17Z` |
| Tenant product count | 2 | 2 |

No product or variant write was performed. The simple product's online price of
111.00 comes from its variant; the variable product's 200.00 prices come from
its two variants.

## 9. Outbox and event findings

Before: 222 messages, 178 states, none delivered, sequence 282â€“503.
The backlog included 78 `AnalyticsEventReceivedEvent`, 27 product events
(10 core product + 17 marketplace product), 3 order-created events, 3
checkout/order/payment events, 8 stock events, and shipment events.

After the permission change:

- `OutboxMessage`: 0 rows.
- `OutboxState`: 0 rows.
- Last `access_refused`: `2026-11-18 17:21:07 UTC`.
- `PointOfSale.API.Events:CheckoutStartedEvent` and the other previously denied
  API exchanges now exist.
- All product queues have live consumers and zero messages.

Replay was automatic and used only the existing outbox messages. No event was
fabricated, deleted, or manually inserted.

## 10. MongoDB read-model findings

Before: 7 total product documents, 0 for the tenant, 0 matching the two IDs.

After: 9 total, 2 for the tenant, 2 matching the two IDs, no duplicates.

| Product | Mongo fields |
|---|---|
| `64986ae7â€¦` | `Ù…Ù†Ø¸Ù ÙˆØ§Ù„Ø²Ø¬Ø§Ø¬Ø§Øª Ø¨Ø§Ø¨Ù„ Ø¬ÙˆÙŠ`, simple, `IsActive=true`, `IsOnlineVisible=true`, `IsSearchable=true`, category `082d9310â€¦` |
| `c96999b9â€¦` | `test`, variable, `IsActive=true`, `IsOnlineVisible=true`, `IsSearchable=true`, category `082d9310â€¦` |

The other 7 pre-existing documents were preserved.

## 11. Storefront API findings

Before:

```json
{"items":[],"totalCount":0,"page":1,"pageSize":12,"totalPages":0,...}
```

After
`GET https://mina-store.shopyxi.com/api/storefront/products/discover?page=1&pageSize=12&sort=relevance&lang=en`:

- HTTP 200.
- `totalCount=2`.
- IDs: `c96999b9-0541-ea8b-69f2-3e3c4e17f6e3`, `64986ae7-7fe9-13dd-4bfa-aa08ce5d8f65`.
- Prices: 200.00 and 111.00 EGP.
- `searchTimeMs=1035`, proving a real Elasticsearch query.
- `Cache-Control: no-store, private, max-age=0`; `surrogate-control: no-store`.
- Tenant filters returned only the Mina Store documents.

## 12. Redis and cache findings

Direct Redis shell inspection was blocked by the platform permission guard, so
no Redis keys were read or modified. No global or targeted flush was performed.
The Storefront response explicitly forbids shared/proxy caching
(`Cache-Control: no-store`, `surrogate-control: no-store`) and executed a fresh
Elasticsearch query after repair. No stale negative cache was observed in the
API or browser. Direct Redis key-level verification remains **BLOCKED**.

## 13. Frontend findings

The frontend was not filtering out valid products; it rendered the empty API
result as the legitimate empty state before repair.

After repair, headed-browser acceptance confirmed:

- Two product cards in Arabic and English.
- Both names, images, categories, prices, and stock labels.
- Simple product detail with add-to-cart enabled.
- Variable product detail with two color options; selecting `Ø§Ø³ÙˆØ¯` changed the
  SKU to `TEST-2` and updated the selected option, while correctly remaining
  out of stock.
- Category tab, search, direct URL navigation, refresh, mobile layout, RTL/LTR.
- No horizontal overflow at 1440 px or 390 px.
- No incorrect empty state and no cross-tenant product.

## 14. Defects and root causes

| Defect | Classification | Status |
|---|---|---|
| `shopixy-app` lacked permission for non-Cart API event exchanges | Messaging configuration / access control | Fixed |
| Ordered outbox head-of-line block | Consequence of the permission defect | Cleared by replay |
| Missing Mongo/ES projections for both Mina products | Consequence of the block | Fixed by replay |
| Screenshot hostname `mina-store.shopxyi.com` | User-visible spelling error | Not a production defect |
| Replay ordering faults for an inactive `ahram` QA product | Consumer ordering / projection-readiness side effect | Preserved; residual follow-up |

## 15. Fixes implemented

1. Broadened `shopixy-app` RabbitMQ permissions on vhost `shopixy` for
   configure, write, and read:
   `PointOfSale\\.API\\.Events:[A-Z][A-Za-z0-9]*(?:Event|EventBase)`.
2. Applied the policy through the RabbitMQ management API and verified the
   stored values exactly.
3. Added `deploy/rabbitmq/shopixy-app-permissions.json` as the canonical policy.
4. Added `deploy/rabbitmq/apply-shopixy-app-permissions.sh`, an idempotent
   management-API deployment script with a `--dry-run` mode.
5. Added `PointOfSale.Tests/Architecture/RabbitMqPublishPermissionPolicyTests.cs`,
   which enumerates every publishable API/Core event exchange and asserts the
   policy admits it while rejecting arbitrary/non-event exchanges.

No application projection code was changed because no projection defect was
proven.

## 16. Production recovery actions and scope

- One broker permission update for user `shopixy-app` on vhost `shopixy`.
- No broker restart, no queue purge, no exchange deletion, no Redis flush.
- No manual event insertion; the existing 222 outbox messages were replayed by
  MassTransit after the permission was corrected.
- No product/variant/category/order/payment mutation.
- No DNS, ingress, certificate, or application-image change.

Rollback: the pre-change permission JSON is preserved at
`validation/storefront/mina-store-product-visibility/evidence/rabbitmq-shopixy-app-permissions-before.json`.
Reapplying it would restore the previous restriction, but it would reintroduce
the publication block and is not a desirable rollback for this incident.

## 17. Data-preservation results

| Check | Result |
|---|---|
| Both original product IDs present exactly once in Mongo | PASS |
| Both original product IDs present exactly once in OpenSearch | PASS |
| Source/read-model publication states agree | PASS |
| Names, variants, categories, prices, currency, stock unchanged in SQL | PASS |
| No duplicate products or variants created | PASS |
| Other tenants' 7 existing Mongo documents preserved | PASS |
| POS Admin unchanged | PASS |
| No order, payment, Finance, or CommerceHub mutation performed | PASS |
| No fabricated events | PASS |

## 18. Tests

Executed with .NET SDK 10.0.404 and Vitest 4.1.12:

| Suite | Command | Result |
|---|---|---|
| Backend policy + projection/search authority | `dotnet test PointOfSale.Tests --filter "RabbitMqPublishPermissionPolicyTests\|ProductSqlAuthorityProjectionWriterTests\|ProductSearchSqlAuthorityBatchTests"` | **55 passed, 0 failed** |
| Frontend catalog/detail authority | `npm test -- --run ProductListPage.catalogOwnership productAuthority storefrontProductDetailApi useHomepageSectionProducts` | **31 passed, 0 failed (4 files)** |

The 4 new policy tests assert:

- every previously denied API event exchange is admitted;
- every event exchange discoverable in the API/Core assemblies is admitted;
- configure, write, and read share the same event surface;
- arbitrary and non-event exchange names are rejected.

The mission's full isolated PostgreSQL/MongoDB integration matrix was **not
executed** in this incident. The defect is broker authorization state, not
application logic; the existing 51 projection/search-authority tests plus live
production end-to-end verification were used instead. This is recorded as a
limitation, not claimed as complete.

## 19. Production deployment identities

| Component | Identity / result |
|---|---|
| API image | `localhost/shopixy/api@sha256:75f49cb501bae5f3add2677367300bcdbb70a9799a257041fd7d38b01d084e04` (unchanged) |
| API deployment generation | 66, observed 66 |
| API pod | `shopixy-api-758d44d9b6-qwhpv`, 2/2 Running, 0 restarts |
| Storefront / Web pods | Running; no rollout |
| RabbitMQ | `shopixy-rabbitmq-server-0/1/2`, 3/3 Running, no restart |
| Policy commit | `d64f213bacbde85bf17c153a5a0b5dc8cf9a97f4` |
| Runtime hash verification | No image change to verify; permission values verified through management API |

## 20. Headed-browser screenshots and network evidence

All under `validation/storefront/mina-store-product-visibility/evidence/browser/`:

| File | Scenario |
|---|---|
| `01-products-desktop-ar.png` | Storefront products, Arabic, desktop |
| `02-category-main-ar.png` | Main category tab |
| `03-search-test-ar.png` | Search applied |
| `04-simple-product-detail-ar.png` | Simple product detail |
| `05-variable-product-detail-ar.png` | Variable product detail |
| `06-products-desktop-en.png` | English, LTR |
| `07-products-mobile-ar.png` | Mobile initial capture |
| `08-admin-products-desktop.png` | POS Admin products, authenticated |
| `09-products-mobile-ar-loaded.png` | Mobile after image load |
| `10-variable-product-second-variant-ar.png` | Selected `Ø§Ø³ÙˆØ¯` variant |
| `acceptance-results.json` | Body text, anchors, images, overflow, console/network capture |
| `admin-products-after.json` | Sanitized admin table/API evidence |

Network observations:

- Storefront `discover` 200 with both IDs.
- Admin `/api/products` 200 with both products.
- Both product images 200 and complete, natural dimensions loaded.
- No failed required API request.
- Console noise: Cloudflare beacon blocked by CSP, `frame-ancestors` meta
  warning, anonymous guest `auth/me` and `auth/refresh` 401s. These are
  pre-existing platform/guest behaviors, not product-visibility failures.

## 21. POS Admin regression results

| Check | Result |
|---|---|
| Product count | `1-2 of 2` |
| Simple product visible | PASS |
| Variable product visible | PASS |
| Both Active | PASS |
| Both Published | PASS |
| Prices/stock match source | PASS |
| No horizontal overflow | PASS |
| No admin API failures | PASS |

## 22. Git branch, commits, and PR URL

- Branch: `storefront/mina-store-product-visibility-20261118`
- Base: `181b8a0f69ff36545acd09618b41b0dd0006900d`
- Policy/application commit: `d64f213bacbde85bf17c153a5a0b5dc8cf9a97f4`
- Evidence/report commit: the commit containing this file (branch tip)
- Repository: `https://github.com/fadyadel2010/Shopyxi_Deploy_Vps.git`
- PR creation URL:
  `https://github.com/fadyadel2010/Shopyxi_Deploy_Vps/pull/new/storefront/mina-store-product-visibility-20261118`

Unrelated pre-existing Finance/Orders/branding working-tree changes were
preserved and not committed.

## 23. Remaining risks and limitations

1. **Replay side effect:** 7 unique historical stock events for the inactive
   `ahram` QA product `904b05b1-767b-59d8-98b0-40461b7667d6` were parked as 21
   copies in three `_error` queues. No Mina Store event is affected. The
   messages were preserved for a scoped follow-up; they were not deleted or
   requeued.
2. **Redis:** direct key-level inspection was blocked by the permission guard.
   No flush was attempted; API headers and fresh query behavior show no stale
   catalog cache.
3. **Integration matrix:** the full isolated 11-case PostgreSQL/MongoDB matrix
   was not run; 55 backend tests plus live production verification were used.
4. **Permission durability:** the permission is stored in RabbitMQ's cluster
   state. It is durable across normal broker restarts, but a restore from an
   older broker backup would require rerunning the committed idempotent script.
5. **Platform console noise:** Cloudflare CSP and anonymous guest 401s remain
   pre-existing console messages unrelated to product visibility.
6. **Dirty tree:** unrelated pre-existing changes remain uncommitted by design;
   the branch itself contains only the incident policy/test/report files.

## Acceptance matrix

| Acceptance item | Status |
|---|---|
| Both original products traced through the full pipeline | PASS |
| Earliest failing stage identified with evidence | PASS |
| Root cause identified | PASS |
| Root cause corrected | FIXED AND RE-TESTED |
| Both products appear in the correct tenant Storefront | FIXED AND RE-TESTED |
| No duplicate or fabricated products | PASS |
| POS Admin remains correct | PASS |
| Prices and inventory unchanged | PASS |
| Other tenants remain isolated | PASS (with preserved replay side effect) |
| Required backend/frontend tests pass | PASS (55 backend, 31 frontend) |
| Full isolated integration matrix | NOT TESTED (documented limitation) |
| Production deployment verified | PASS (policy-only; no image rollout) |
| Real headed-browser acceptance | PASS |
| Redis key-level verification | BLOCKED (direct access); no stale cache observed |
| Evidence committed and remote verified | PASS after push (see delivery message) |
