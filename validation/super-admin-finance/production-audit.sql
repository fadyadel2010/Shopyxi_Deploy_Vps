-- Non-mutating production audit. Only aggregate counts/booleans and the
-- explicitly supplied public commission-rule identity may leave the database.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '20s';
SET LOCAL TIME ZONE 'UTC';

SELECT json_build_object(
  'checkedAtUtc', now(),
  'reportFromUtc', '2026-10-18T00:00:00Z',
  'reportToExclusiveUtc', '2026-11-17T00:00:00Z',
  'marketplaceOrdersAllTime', (SELECT count(*) FROM "CommerceHubMarketplaceOrders"),
  'marketplaceSubOrdersAllTime', (SELECT count(*) FROM "CommerceHubSubOrders"),
  'marketplacePaymentsAllTime', (SELECT count(*) FROM "CommerceHubPayments"),
  'marketplaceRefundsAllTime', (SELECT count(*) FROM "CommerceHubRefunds"),
  'marketplaceLedgerEntriesAllTime', (SELECT count(*) FROM "CommerceHubFinancialTransactions"),
  'marketplaceLedgerEntriesInWindow', (SELECT count(*) FROM "CommerceHubFinancialTransactions"
    WHERE "CreatedAtUtc" >= '2026-10-18T00:00:00Z' AND "CreatedAtUtc" < '2026-11-17T00:00:00Z'),
  'globalRuleVerified', EXISTS (SELECT 1 FROM "CommissionRules"
    WHERE "Id" = '98eca7ca-414a-442f-bdf6-fb320cfb4de4' AND "ScopeType" = 0
      AND "ScopeId" IS NULL AND "Rate" = 0.10 AND "IsActive" AND NOT "IsDeleted"),
  'activeGlobalRuleCount', (SELECT count(*) FROM "CommissionRules"
    WHERE "ScopeType" = 0 AND "IsActive" AND NOT "IsDeleted"),
  'activeOverrideRuleCount', (SELECT count(*) FROM "CommissionRules"
    WHERE "ScopeType" <> 0 AND "IsActive" AND NOT "IsDeleted"),
  'unsupportedLedgerCurrencyCount', (SELECT count(*) FROM "CommerceHubFinancialTransactions"
    WHERE "Currency" IS DISTINCT FROM 'EGP'),
  'orphanOrderLedgerCount', (SELECT count(*) FROM "CommerceHubFinancialTransactions" t
    WHERE t."ReferenceType" = 0 AND NOT EXISTS (SELECT 1 FROM "CommerceHubSubOrders" s WHERE s."Id" = t."ReferenceId")),
  'orphanRefundLedgerCount', (SELECT count(*) FROM "CommerceHubFinancialTransactions" t
    WHERE t."ReferenceType" = 1 AND NOT EXISTS (SELECT 1 FROM "CommerceHubRefunds" r WHERE r."Id" = t."ReferenceId")),
  'duplicateIdempotencyGroupCount', (SELECT count(*) FROM (
    SELECT "IdempotencyKey" FROM "CommerceHubFinancialTransactions" GROUP BY 1 HAVING count(*) > 1) d),
  'overRefundedSubOrderCount', (SELECT count(*) FROM "CommerceHubSubOrders" s
    WHERE (SELECT coalesce(sum(r."Amount"), 0) FROM "CommerceHubRefunds" r
      WHERE r."SubOrderId" = s."Id" AND r."Status" = 2 AND NOT r."IsDeleted") > s."Total"),
  'nonMarketplaceOrdersExist', EXISTS (SELECT 1 FROM "Orders" WHERE NOT "IsDeleted"),
  'allExistingNonMarketplaceOrdersCancelledUnrecognized', NOT EXISTS (
    SELECT 1 FROM "Orders" WHERE NOT "IsDeleted" AND
      ("Status" <> 6 OR "PaymentStatus" NOT IN (4, 8) OR "FinancialPostingStatus" <> 0)),
  'independentListingOverrideExists', EXISTS (SELECT 1 FROM "CommerceHubListings"
    WHERE NOT "IsDeleted" AND "IsPublished" AND "CommerceHubPrice" IS NOT NULL)
);
ROLLBACK;
