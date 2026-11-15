using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Notifications;

/// <summary>Durable QA history is never authority to contact a real customer.
/// This exclusion is independent of whether a QA simulator is currently enabled.</summary>
public static class OrderCustomerCommunicationAuthority
{
    public static IQueryable<Order> CommunicableOrders(ApplicationDbContext db) =>
        db.Orders.IgnoreQueryFilters().Where(o => !o.IsDeleted && o.PaymentStatus != PaymentStatus.SyntheticQaCollected &&
            !db.SyntheticQaOrders.IgnoreQueryFilters().Any(q => q.OrderId == o.Id) &&
            !db.SyntheticQaEvidence.IgnoreQueryFilters().Any(q => q.OrderId == o.Id) &&
            !db.SyntheticQaCommissionPostings.IgnoreQueryFilters().Any(q => q.OrderId == o.Id));
}
