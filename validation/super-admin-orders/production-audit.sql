-- Aggregate evidence only; no customer identifiers, tokens or row exports.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '20s';
SET LOCAL TIME ZONE 'UTC';
SELECT json_build_object(
  'checkedAtUtc', now(),
  'marketplaceOrdersAllTime', (SELECT count(*) FROM "CommerceHubMarketplaceOrders"),
  'marketplaceOrdersActive', (SELECT count(*) FROM "CommerceHubMarketplaceOrders" WHERE NOT "IsDeleted"),
  'marketplaceOrdersDeleted', (SELECT count(*) FROM "CommerceHubMarketplaceOrders" WHERE "IsDeleted"),
  'marketplaceSubOrdersAllTime', (SELECT count(*) FROM "CommerceHubSubOrders"),
  'marketplaceItemsAllTime', (SELECT count(*) FROM "CommerceHubSubOrderItems"),
  'marketplacePaymentsAllTime', (SELECT count(*) FROM "CommerceHubPayments"),
  'marketplacePaymentAttemptsAllTime', (SELECT count(*) FROM "CommerceHubPaymentAttempts"),
  'marketplaceRefundRequestsAllTime', (SELECT count(*) FROM "CommerceHubRefundRequests"),
  'marketplaceRefundsAllTime', (SELECT count(*) FROM "CommerceHubRefunds"),
  'marketplaceLedgerEntriesAllTime', (SELECT count(*) FROM "CommerceHubFinancialTransactions"),
  'separateOrdersAllTime', (SELECT count(*) FROM "Orders"),
  'separatePosInvoicesAllTime', (SELECT count(*) FROM "SalesInvoices"),
  'todayUtcStart', date_trunc('day', now()),
  'totalOrdersToday', (SELECT count(*) FROM "CommerceHubMarketplaceOrders"
    WHERE NOT "IsDeleted" AND "CreatedAt" >= date_trunc('day', now())
      AND "CreatedAt" < date_trunc('day', now()) + interval '1 day'),
  'pendingOrders', (SELECT count(*) FROM "CommerceHubMarketplaceOrders" WHERE NOT "IsDeleted" AND "Status" = 0),
  'failedPaymentOrders', (SELECT count(*) FROM "CommerceHubMarketplaceOrders"
    WHERE NOT "IsDeleted" AND "PaymentStatus" IN (4,8,11,12)),
  'capturedSalesToday', (SELECT coalesce(sum(s."Total"),0) FROM "CommerceHubSubOrders" s
    JOIN "CommerceHubMarketplaceOrders" o ON o."Id"=s."MarketplaceOrderId"
    WHERE NOT s."IsDeleted" AND NOT o."IsDeleted" AND s."PaymentStatus" IN (2,5,6,9)
      AND s."PaidAt" >= date_trunc('day',now()) AND s."PaidAt" < date_trunc('day',now()) + interval '1 day'),
  'orphanSubOrders', (SELECT count(*) FROM "CommerceHubSubOrders" s
    WHERE NOT EXISTS (SELECT 1 FROM "CommerceHubMarketplaceOrders" o WHERE o."Id"=s."MarketplaceOrderId")),
  'orphanPayments', (SELECT count(*) FROM "CommerceHubPayments" p
    WHERE NOT EXISTS (SELECT 1 FROM "CommerceHubMarketplaceOrders" o WHERE o."Id"=p."OrderId")),
  'duplicateOrderNumbers', (SELECT count(*) FROM (
    SELECT "OrderNumber" FROM "CommerceHubMarketplaceOrders" GROUP BY 1 HAVING count(*) > 1) d),
  'globalRuleVerified', EXISTS (SELECT 1 FROM "CommissionRules"
    WHERE "Id"='98eca7ca-414a-442f-bdf6-fb320cfb4de4' AND "ScopeType"=0
      AND "ScopeId" IS NULL AND "Rate"=0.10 AND "IsActive" AND NOT "IsDeleted"),
  'activeOverrideRuleCount', (SELECT count(*) FROM "CommissionRules"
    WHERE "ScopeType"<>0 AND "IsActive" AND NOT "IsDeleted")
);
ROLLBACK;
