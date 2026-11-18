# Mina Store product visibility â€” read-only production baseline

Captured before any production mutation on 2026-11-18 (UTC).

## Tenant and hostname

| Item | Value |
|---|---|
| Tenant ID | `059aa5c5-b9db-4dd6-bf44-4004d1eb96a1` |
| Tenant slug | `mina-store` |
| Tenant status | `Active` (`Status=1`, `IsActive=t`) |
| Verified public hostname | `https://mina-store.shopyxi.com` |
| DNS | Cloudflare A/AAAA records resolve; HTTPS returns `HTTP/1.1 200 OK` |
| Incorrect screenshot spelling | `mina-store.shopxyi.com` does not resolve; not used |

## Exact products

| Product ID | Type | Name | SalePrice | Variants | PublishStatus | IsOnlineVisible | IsSearchable | ProjectionMutationVersion | LastSyncedAt |
|---|---|---|---|---|---:|---:|---:|---:|---|
| `64986ae7-7fe9-13dd-4bfa-aa08ce5d8f65` | Simple (`HasVariants=f`) | `Ù…Ù†Ø¸Ù ÙˆØ§Ù„Ø²Ø¬Ø§Ø¬Ø§Øª Ø¨Ø§Ø¨Ù„ Ø¬ÙˆÙŠ` | 11.00 | 1 (`3e5c9833-511f-46ee-8762-cd8400880f0c`, `Price=11.00`, `OnlinePrice=111.00`, active) | 1 | t | t | 9 | NULL |
| `c96999b9-0541-ea8b-69f2-3e3c4e17f6e3` | Variable (`HasVariants=t`) | `test` | 0.00 | 2 (`10c0f12f-bcdc-4027-b833-f34aa14757bf`, `ca5d9b7e-7620-4647-8cae-e31ce02db679`, both `Price=200.00`, active) | 1 | t | t | 5 | NULL |

Both products belong to category `082d9310-920d-4fc8-90d1-94f3e5291648`
(`Ù‚Ø³Ù… ÙØ±Ø¹Ù‰`, not deleted), whose parent is
`afa48c41-b281-4d25-9a43-ead35f4ecfd9` (`Ù‚Ø³Ù… Ø±Ø¦ÙŠØ³Ù‰`, not deleted).

## Storefront API before repair

`GET https://mina-store.shopyxi.com/api/storefront/products/discover?page=1&pageSize=12&sort=relevance&lang=en`

```json
{"items":[],"totalCount":0,"page":1,"pageSize":12,"totalPages":0,"hasNextPage":false,"hasPreviousPage":false,"facets":{"brands":[],"categories":[],"priceRanges":[],"promotions":{"promotedCount":0,"nonPromotedCount":0},"stock":{"inStockCount":0,"outOfStockCount":0}},"searchTimeMs":54}
```

The response is a real Elasticsearch query (`searchTimeMs=54`), not a frontend
empty-state substitution.

## Read models before repair

| Store | Query | Result |
|---|---|---:|
| MongoDB `ecommerce_read_db.products` | all documents | 7 |
| MongoDB `ecommerce_read_db.products` | `TenantId=059aa5c5...` | 0 |
| MongoDB `ecommerce_read_db.products` | the two product IDs | 0 |
| MongoDB `marketplace_product_read_models` | all documents | 0 |
| MongoDB `processed_events` | all documents | 0 |
| OpenSearch `products` | all documents | 7 |
| OpenSearch `products` | `tenantId=059aa5c5...` | 0 |
| OpenSearch `products` | the two product IDs | 0 |

## MassTransit transactional outbox before repair

`OutboxMessage` and `OutboxState` in PostgreSQL:

| Item | Value |
|---|---:|
| Outbox messages | 222 |
| Sequence range | 282â€“503 |
| Distinct outbox states | 178 |
| Delivered outbox states | 0 |
| Undelivered outbox states | 178 |
| Earliest undelivered sequence | 282 |
| Earliest message type at sequence 282 | `PointOfSale.API.Events:CheckoutStartedEvent` |
| Product-related outbox messages | 27 (10 core product events + 17 marketplace product events) |

The outbox contains no message before sequence 282. Sequence 282 is the
head-of-line message and is denied by RabbitMQ; MassTransit therefore cannot
advance the ordered outbox.

## RabbitMQ permission defect

`shopixy-app` on vhost `shopixy` allowed:

- `PointOfSale.Core.Events*` event exchanges
- only `PointOfSale.API.Events:Cart*` API exchanges

It did **not** allow:

- `PointOfSale.API.Events:CheckoutStartedEvent`
- `PointOfSale.API.Events:PriceRecalculationEvent`
- `PointOfSale.API.Events:CheckoutOrderCreatedEvent`
- `PointOfSale.API.Events:InventoryReservedEvent`
- `PointOfSale.API.Events:ManualPaymentSubmittedEvent`
- the remaining `PointOfSale.API.Events:*Event` types

RabbitMQ logs recorded 780 `access_refused` denials in approximately 50
minutes, all for `PointOfSale.API.Events:CheckoutStartedEvent`, from the API
pod (`10.42.0.120`). The denied exchange does not exist yet; both `configure`
and `write` permission are required to declare and publish it.

All relevant product queues and bindings exist and have live consumers. The
queues were empty because the ordered outbox never released the product
events.

## Baseline conclusion

The first failing stage is the transactional-outbox publication stage, before
any MongoDB or OpenSearch projection. Both products are correctly published
and visible in PostgreSQL; their absence from the storefront is not a product
configuration defect.
