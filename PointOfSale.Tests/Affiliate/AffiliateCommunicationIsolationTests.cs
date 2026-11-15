using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Notifications;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateCommunicationIsolationTests
{
    [Theory]
    [InlineData("enrollment")]
    [InlineData("evidence")]
    [InlineData("posting")]
    [InlineData("synthetic-status")]
    [InlineData("deleted")]
    public async Task DurableSyntheticOrMissingAuthority_NeverAdmitsCustomerCommunication(string marker)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var order = new Order { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), OrderNumber = "LOCAL-NO-CONTACT", Currency = "EGP" };
        if (marker == "synthetic-status") order.PaymentStatus = PaymentStatus.SyntheticQaCollected;
        if (marker == "deleted") order.IsDeleted = true;
        db.Add(order);
        if (marker == "enrollment") db.Add(new SyntheticQaOrder { OrderId = order.Id });
        if (marker == "evidence") db.Add(new SyntheticQaEvidence { Id = Guid.NewGuid(), OrderId = order.Id });
        if (marker == "posting") db.Add(new SyntheticQaCommissionPosting { Id = Guid.NewGuid(), OrderId = order.Id });
        await db.SaveChangesAsync();
        Assert.False(await OrderCustomerCommunicationAuthority.CommunicableOrders(db).AnyAsync(o => o.Id == order.Id));
        Assert.False(await OrderCustomerCommunicationAuthority.CommunicableOrders(db).AnyAsync(o => o.Id == Guid.Empty));
    }

    [Fact]
    public async Task OrdinaryOrder_RemainsAdmittedByCanonicalOrderAuthority()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var order = new Order { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), OrderNumber = "LOCAL-ORDINARY", Currency = "EGP" };
        db.Add(order); await db.SaveChangesAsync();
        Assert.True(await OrderCustomerCommunicationAuthority.CommunicableOrders(db).AnyAsync(o => o.Id == order.Id && o.TenantId == order.TenantId));
        Assert.False(await OrderCustomerCommunicationAuthority.CommunicableOrders(db).AnyAsync(o => o.Id == order.Id && o.TenantId == Guid.Empty));
    }
}
