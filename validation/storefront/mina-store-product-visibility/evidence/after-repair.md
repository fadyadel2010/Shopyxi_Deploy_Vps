# Mina Store product visibility â€” post-repair evidence

Captured 2026-11-18 after the RabbitMQ policy change. No application image was rebuilt or rolled out.

## Repair

| Item | Result |
|---|---|
| Change | `shopixy-app` permissions on vhost `shopixy` broadened from `PointOfSale.API.Events:Cart*` to `PointOfSale\\.API\\.Events:[A-Z][A-Za-z0-9]*(?:Event\|EventBase)` for configure/write/read |
| Method | RabbitMQ management API through a temporary TLS port-forward |
| Broker restart | None |
| Result | Permissions updated and verified byte-for-byte; `PointOfSale.API.Events:CheckoutStartedEvent` exchange now exists |
| Last `access_refused` | `2026-11-18 17:21:07 UTC`; no later denials observed |
| Application image | `localhost/shopixy/api@sha256:75f49cb501bae5f3add2677367300bcdbb70a9799a257041fd7d38b01d084e04` (unchanged) |
| API pod | `shopixy-api-758d44d9b6-qwhpv`, 2/2 Running, 0 restarts |
| RabbitMQ pods | 3/3 Running, no restart |

## Outbox

| Item | Before | After |
|---|---:|---:|
| `OutboxMessage` rows | 222 | 0 |
| Distinct `OutboxState` rows | 178 | 0 |
| Delivered states | 0 | n/a (delivered rows cleaned up) |
| Undelivered states | 178 | 0 |
| Sequence range | 282â€“503 | empty |

## Read models and API

| Check | Result |
|---|---|
| MongoDB `ecommerce_read_db.products`, total | 9 (was 7; +2 Mina Store documents) |
| MongoDB, tenant `059aa5c5-b9db-4dd6-bf44-4004d1eb96a1` | 2 |
| MongoDB, matching the two product IDs | 2; no duplicates |
| MongoDB product 1 | `64986ae7-7fe9-13dd-4bfa-aa08ce5d8f65`, `Ù…Ù†Ø¸Ù ÙˆØ§Ù„Ø²Ø¬Ø§Ø¬Ø§Øª Ø¨Ø§Ø¨Ù„ Ø¬ÙˆÙŠ`, active/visible/searchable, simple |
| MongoDB product 2 | `c96999b9-0541-ea8b-69f2-3e3c4e17f6e3`, `test`, active/visible/searchable, variable |
| OpenSearch `products`, matching hits | 2 (exact tenant-prefixed IDs) |
| `GET https://mina-store.shopyxi.com/api/storefront/products/discover` | `totalCount=2`, both IDs returned, `searchTimeMs=1035` |
| Response caching | `Cache-Control: no-store, private, max-age=0`, `surrogate-control: no-store` |

## POS Admin

Headed browser, authenticated merchant session:

- URL: `https://admin.shopyxi.com/products`
- Count: `Ø¹Ø±Ø¶ 1-2 Ù…Ù† Ø£ØµÙ„ 2 Ù…Ù†ØªØ¬`
- `test` â€” variable, `Ù‚Ø³Ù… ÙØ±Ø¹Ù‰`, `Ù†Ø´Ø·`, `Ù…Ù†Ø´ÙˆØ±`
- `Ù…Ù†Ø¸Ù ÙˆØ§Ù„Ø²Ø¬Ø§Ø¬Ø§Øª Ø¨Ø§Ø¨Ù„ Ø¬ÙˆÙŠ` â€” simple, `Ù‚Ø³Ù… ÙØ±Ø¹Ù‰`, `11.00 EGP`, stock `100`, `Ù†Ø´Ø·`, `Ù…Ù†Ø´ÙˆØ±`
- No horizontal overflow at 1440 px.

## Storefront browser acceptance

| Scenario | Result |
|---|---|
| Desktop Arabic `/products` | 2 results; both cards visible; both images loaded; no overflow |
| Main category tab | 2 results; both products retained |
| Search `test` | Search applied; 2 results |
| Simple product detail `/products/product-64986ae7` | `EGP 111.00`, `In Stock (100)`, add-to-cart enabled |
| Variable product detail `/products/test` | `EGP 200.00`, options `Ø§Ø¨ÙŠØ¶` / `Ø§Ø³ÙˆØ¯`, selecting `Ø§Ø³ÙˆØ¯` updates SKU to `TEST-2` and keeps out-of-stock state |
| English desktop | 2 results, LTR |
| Mobile Arabic 390Ã—844 | 2 results, both images loaded, no horizontal overflow |

No application page errors and no failed required API requests. The only console messages were pre-existing platform noise: Cloudflare's beacon blocked by CSP, the `frame-ancestors` meta warning, and anonymous guest `auth/me` + `auth/refresh` 401s.

## Replay side effect outside Mina Store

The unblocked ordered outbox also delivered historical events for other tenants. Seven unique `ProductVariantStockChangedEvent` messages (stock versions 12â€“18, authored 2026-11-13) for the **inactive** QA product `904b05b1-767b-59d8-98b0-40461b7667d6` in tenant `ahram` hit projection-not-ready guards in three consumers:

| Queue | Messages |
|---|---:|
| `affiliate-product-policy-projection_error` | 7 |
| `product-stock-price-search-index_error` | 7 |
| `product-stock-projection_error` | 7 |

The messages were preserved, not deleted or requeued. No Mina Store message is present. The `ahram` product is `IsActive=false`, `PublishStatus=2`, and its Mongo projection is at `LastEventVersion=44` but `LastStockEventVersion=11`. This is recorded as a residual follow-up, not a Mina Store defect.

## Tests

```text
Backend:    55 passed, 0 failed
            4 RabbitMqPublishPermissionPolicyTests
           51 ProductSqlAuthorityProjectionWriter / ProductSearchSqlAuthorityBatch tests

Frontend:   31 passed, 0 failed across 4 Vitest files
            ProductListPage.catalogOwnership
            productAuthority
            storefrontProductDetailApi
            useHomepageSectionProducts
```

Raw output: `tests-backend.txt`, `tests-frontend.txt`.
