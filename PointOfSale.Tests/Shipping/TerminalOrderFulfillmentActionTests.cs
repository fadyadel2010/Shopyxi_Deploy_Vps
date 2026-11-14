using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Fulfillment;
using PointOfSale.Infrastructure.MultiTenancy;

namespace PointOfSale.Tests.Shipping;

public class TerminalOrderFulfillmentActionTests
{
    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Refunded)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Confirmed)]
    public async Task ActionsRespectTerminalOrderStatus(OrderStatus status)
    {
        var tenantId = Guid.NewGuid();
        var tenant = new TenantContext(); tenant.SetTenantId(tenantId);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var order = new Order { Id = Guid.NewGuid(), TenantId = tenantId, OrderNumber = "QA-TERMINAL",
            Currency = "EGP", ShippingAddress = "Synthetic only", Status = status,
            FulfillmentStatus = FulfillmentStatus.Packed, FulfillmentMode = FulfillmentMode.Manual, PaymentMethodCode = "COD" };
        db.Orders.Add(order); await db.SaveChangesAsync();
        var service = new OrderFulfillmentService(db, Mock.Of<IEventPublisher>(), NullLogger<OrderFulfillmentService>.Instance);
        var actions = (await service.GetAvailableActionsAsync(order.Id)).AvailableActions;
        if (status == OrderStatus.Confirmed) Assert.NotEmpty(actions);
        else Assert.Empty(actions);
    }
}
