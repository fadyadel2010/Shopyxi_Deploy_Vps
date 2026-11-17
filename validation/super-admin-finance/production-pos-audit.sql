-- Aggregate-only, repeatable-read reconciliation. No customer values or IDs.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '20s';
SET LOCAL TIME ZONE 'UTC';
SELECT json_build_object(
  'checkedAtUtc', now(),
  'salesInvoicesAllTime', (SELECT count(*) FROM "SalesInvoices" WHERE NOT "IsDeleted"),
  'invoiceArithmeticMismatchCount', (SELECT count(*) FROM "SalesInvoices"
    WHERE NOT "IsDeleted" AND round("Subtotal" - "DiscountAmount" + "TaxAmount", 2) <> "TotalAmount"),
  'negativeInvoiceValueCount', (SELECT count(*) FROM "SalesInvoices"
    WHERE NOT "IsDeleted" AND ("TotalAmount" < 0 OR "PaidAmount" < 0)),
  'invoiceStatusCounts', (SELECT coalesce(json_object_agg(status, n), '{}'::json)
    FROM (SELECT "Status"::text status, count(*) n FROM "SalesInvoices" WHERE NOT "IsDeleted" GROUP BY 1) s),
  'salesJournalCount', (SELECT count(*) FROM "LedgerJournalEntries" WHERE "SourceType" = 'SalesInvoice'),
  'salesCogsJournalCount', (SELECT count(*) FROM "LedgerJournalEntries" WHERE "SourceType" = 'SalesInvoice_COGS'),
  'salesJournalWithoutSourceCount', (SELECT count(*) FROM "LedgerJournalEntries" j
    WHERE j."SourceType" IN ('SalesInvoice', 'SalesInvoice_COGS')
      AND NOT EXISTS (SELECT 1 FROM "SalesInvoices" s WHERE s."Id" = j."SourceId" AND s."TenantId" = j."TenantId")),
  'unbalancedJournalCount', (SELECT count(*) FROM (
    SELECT j."Id" FROM "LedgerJournalEntries" j LEFT JOIN "LedgerLines" l ON l."JournalEntryId" = j."Id"
    GROUP BY j."Id" HAVING coalesce(sum(l."DebitAmount"), 0) <> coalesce(sum(l."CreditAmount"), 0)
      OR count(l."Id") < 2) a),
  'crossTenantJournalLineCount', (SELECT count(*) FROM "LedgerLines" l
    JOIN "LedgerJournalEntries" j ON j."Id" = l."JournalEntryId" WHERE l."TenantId" <> j."TenantId"),
  'pendingPosAccountingOutboxCount', (SELECT count(*) FROM "AccountingOutboxMessages"
    WHERE "EventType" = 'PosSale' AND "ProcessedAtUtc" IS NULL AND NOT "IsDeleted"),
  'failedPosAccountingOutboxCount', (SELECT count(*) FROM "AccountingOutboxMessages"
    WHERE "EventType" = 'PosSale' AND "IsFailed" AND NOT "IsDeleted"),
  'posInvoicesWithRevenueJournalCount', (SELECT count(*) FROM "SalesInvoices" s
    WHERE NOT s."IsDeleted" AND EXISTS (SELECT 1 FROM "LedgerJournalEntries" j
      WHERE j."SourceType" = 'SalesInvoice' AND j."SourceId" = s."Id" AND j."TenantId" = s."TenantId")),
  'salesReturnsAllTime', (SELECT count(*) FROM "Returns" WHERE NOT "IsDeleted" AND "OriginalInvoiceId" IS NOT NULL),
  'returnRevenueJournalCount', (SELECT count(*) FROM "LedgerJournalEntries" WHERE "SourceType" = 'Return'),
  'returnCogsJournalCount', (SELECT count(*) FROM "LedgerJournalEntries" WHERE "SourceType" = 'Return_COGS'),
  'fullyReturnedInvoicePrincipalMismatchCount', (SELECT count(*) FROM "SalesInvoices" s
    WHERE NOT s."IsDeleted" AND s."Status" = 4 AND s."TotalAmount" <>
      (SELECT coalesce(sum(r."TotalAmount"),0) FROM "Returns" r
        WHERE NOT r."IsDeleted" AND r."OriginalInvoiceId" = s."Id" AND r."TenantId" = s."TenantId")),
  'salesReturnWithoutJournalCount', (SELECT count(*) FROM "Returns" r
    WHERE NOT r."IsDeleted" AND r."OriginalInvoiceId" IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM "LedgerJournalEntries" j
        WHERE j."SourceType" = 'Return' AND j."SourceId" = r."Id" AND j."TenantId" = r."TenantId")),
  'salesReturnAllocationMismatchCount', (SELECT count(*) FROM "Returns" r
    WHERE NOT r."IsDeleted" AND r."OriginalInvoiceId" IS NOT NULL AND r."RefundMethod" IS NOT NULL
      AND r."TotalAmount" <> r."CashRefundAmount" + r."CardRefundAmount" + r."CustomerCreditAmount")
);
ROLLBACK;
