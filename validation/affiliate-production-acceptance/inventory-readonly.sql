BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT json_build_object(
'sourceOfTruth','ProductVariantStocks (WarehouseStocks is compatibility only)',
'stock',coalesce((SELECT json_agg(x) FROM (SELECT s."WarehouseId",s."VariantId",s."Quantity",s."ReservedQuantity",s."DamagedQuantity" FROM "ProductVariantStocks" s JOIN "ProductVariants" v ON v."Id"=s."VariantId" AND v."TenantId"=s."TenantId" WHERE s."TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0' AND v."ProductId"='904b05b1-767b-59d8-98b0-40461b7667d6' ORDER BY s."Id") x),'[]'::json),
'remainingReserved',coalesce((SELECT sum(greatest(0,"ReservedQuantity"-"CommittedQuantity"-"ReleasedQuantity")) FROM "InventoryReservations" WHERE "TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0' AND "ProductId"='904b05b1-767b-59d8-98b0-40461b7667d6'),0),
'historicalMovementCount',(SELECT count(*) FROM "StockMovements" WHERE "TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0' AND "ProductId"='904b05b1-767b-59d8-98b0-40461b7667d6'),
'historicalQuantityNet',coalesce((SELECT sum("QuantityChange") FROM "StockMovements" WHERE "TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0' AND "ProductId"='904b05b1-767b-59d8-98b0-40461b7667d6'),0));
ROLLBACK;
