BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT json_build_object(
 'merchant',(SELECT json_build_object('name',"Name",'slug',"Slug",'tenantId',"Id",'currency',"Currency") FROM "Tenants" WHERE "Id"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0'),
 'affiliate',(SELECT json_build_object('userId',u."Id",'accountId',a."Id",'status',a."Status",'emailVerified',u."EmailVerified",'isDeleted',u."IsDeleted",'posUserCount',(SELECT count(*) FROM "Users" p WHERE p."Id"=u."Id")) FROM "AffiliateUsers" u JOIN "AffiliateAccounts" a ON a."Id"=u."AffiliateAccountId" WHERE a."Id"='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'),
 'qaOrders',(SELECT count(*) FROM "Orders" WHERE "AffiliateAccountId"='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'),
 'attributions',(SELECT count(*) FROM "AffiliateAttributions" WHERE "AffiliateAccountId"='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'),
 'links',(SELECT count(*) FROM "AffiliatePromotionLinks" WHERE "AffiliateAccountId"='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'),
 'commissionEntries',(SELECT count(*) FROM "AffiliateCommissionLedger" WHERE "AffiliateAccountId"='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'),
 'balanceEntries',(SELECT count(*) FROM "AffiliateBalanceLedger" WHERE "AffiliateAccountId"='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'),
 'affiliateBalanceMovementNet',(SELECT coalesce(sum("Amount"),0) FROM "AffiliateBalanceLedger" WHERE "AffiliateAccountId"='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'),
 'payoutRequests',(SELECT count(*) FROM "AffiliatePayoutRequests" WHERE "AffiliateAccountId"='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'),
 'manualPayouts',(SELECT count(*) FROM "AffiliateManualPayouts" WHERE "AffiliateAccountId"='01a1dc7c-3cb5-7b17-a0f2-1d082c878a60'),
 'verificationAuditCount',(SELECT count(*) FROM "AuditLogs" WHERE "UserId"='01a1dc7c-3d0c-7ef6-9025-fcdc054ef8ca' AND "ActionType"='EMAIL_VERIFIED'),
 'qaProduct',(SELECT json_build_object('id',"Id",'name',"Name",'isActive',"IsActive",'publishStatus',"PublishStatus") FROM "Products" WHERE "Id"='904b05b1-767b-59d8-98b0-40461b7667d6' AND "TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0'),
 'qaProductStock',(SELECT coalesce(json_agg(x),'[]'::json) FROM (SELECT "WarehouseId","ProductVariantId","Quantity","ReservedQuantity" FROM "WarehouseStocks" WHERE "ProductId"='904b05b1-767b-59d8-98b0-40461b7667d6' AND "TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0' ORDER BY "Id") x),
 'qaProductMovementCount',(SELECT count(*) FROM "StockMovements" WHERE "ProductId"='904b05b1-767b-59d8-98b0-40461b7667d6' AND "TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0'),
 'merchantLedgerAccountsPrivate',(SELECT coalesce(json_agg(x),'[]'::json) FROM (SELECT "Id","AccountCode","CurrentBalance" FROM "LedgerAccounts" WHERE "TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0' ORDER BY "Id") x),
 'merchantAuthoritativeLedgerPrivate',(SELECT coalesce(json_agg(x),'[]'::json) FROM (SELECT l."AccountId",sum(l."DebitAmount") AS debits,sum(l."CreditAmount") AS credits FROM "LedgerLines" l JOIN "LedgerJournalEntries" j ON j."Id"=l."JournalEntryId" AND j."TenantId"=l."TenantId" WHERE l."TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0' AND j."Status" IN (1,2) GROUP BY l."AccountId" ORDER BY l."AccountId") x),
 'cashboxesPrivate',(SELECT coalesce(json_agg(x),'[]'::json) FROM (SELECT c."Id",c."Name",c."IsActive",t."AccountId" FROM "Cashboxes" c LEFT JOIN "TreasuryAccounts" t ON t."TenantId"=c."TenantId" AND t."ReferenceCode"=replace(c."Id"::text,'-','') WHERE c."TenantId"='cdbe20e0-f84a-4416-b20c-044e6bcf04d0' ORDER BY c."Id",t."Id") x));
ROLLBACK;
