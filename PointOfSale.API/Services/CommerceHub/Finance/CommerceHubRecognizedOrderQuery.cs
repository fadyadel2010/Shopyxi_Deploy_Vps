using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Enums.CommerceHubFinance;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services.CommerceHub.Finance;

/// <summary>
/// One reporting row per recognized sub-order/currency. A valid zero-rate
/// order has no commission entry (the append-only ledger prohibits zero
/// amounts), so its canonical payment entry supplies recognition instead.
/// This is a read model, never an instruction to create/backfill ledger data.
/// </summary>
internal static class CommerceHubRecognizedOrderQuery
{
    public static IQueryable<RecognizedOrderRow> Build(ApplicationDbContext dbContext, DateTime snapshotBoundaryUtc)
    {
        var entries =
            from transaction in dbContext.CommerceHubFinancialTransactions.AsNoTracking()
            join subOrder in dbContext.CommerceHubSubOrders.AsNoTracking()
                on transaction.ReferenceId equals subOrder.Id
            join order in dbContext.CommerceHubMarketplaceOrders.AsNoTracking()
                on subOrder.MarketplaceOrderId equals order.Id
            where !subOrder.IsDeleted && !order.IsDeleted
                  && transaction.CreatedAtUtc <= snapshotBoundaryUtc
                  && subOrder.Status != OrderStatus.Cancelled
                  && (subOrder.PaymentStatus == PaymentStatus.Collected ||
                      subOrder.PaymentStatus == PaymentStatus.Settled ||
                      subOrder.PaymentStatus == PaymentStatus.Refunded ||
                      subOrder.PaymentStatus == PaymentStatus.PartiallyRefunded)
                  && transaction.ReferenceType == CommerceHubFinancialReferenceType.Order
                  && transaction.Direction == CommerceHubFinancialTransactionDirection.Credit
                  && (
                      (transaction.EntityType == CommerceHubFinancialEntityType.Platform &&
                       transaction.EntityId == null &&
                       transaction.TransactionType == CommerceHubFinancialTransactionType.Commission) ||
                      (subOrder.PlatformCommissionAmount == 0m &&
                       transaction.TransactionType == CommerceHubFinancialTransactionType.OrderPayment &&
                       ((subOrder.PaymentMethod == CommerceHubPaymentMethod.CashOnDelivery &&
                         transaction.EntityType == CommerceHubFinancialEntityType.Vendor &&
                         transaction.EntityId == subOrder.StoreId) ||
                        (subOrder.PaymentMethod != CommerceHubPaymentMethod.CashOnDelivery &&
                         transaction.EntityType == CommerceHubFinancialEntityType.Platform &&
                         transaction.EntityId == null))))
            select new
            {
                transaction.ReferenceId,
                transaction.Currency,
                transaction.CreatedAtUtc,
                Amount = transaction.TransactionType == CommerceHubFinancialTransactionType.Commission
                    ? transaction.Amount : 0m
            };

        return entries.GroupBy(row => new { row.ReferenceId, row.Currency })
            .Select(group => new RecognizedOrderRow
            {
                ReferenceId = group.Key.ReferenceId,
                Currency = group.Key.Currency,
                CreatedAtUtc = group.Min(row => row.CreatedAtUtc),
                Amount = group.Sum(row => row.Amount)
            });
    }

    internal sealed class RecognizedOrderRow
    {
        public Guid ReferenceId { get; init; }
        public string Currency { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public decimal Amount { get; init; }
    }
}
