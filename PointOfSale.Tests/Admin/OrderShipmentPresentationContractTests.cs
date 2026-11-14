using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Admin;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Admin;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.PhoneRisk;
using PointOfSale.API.Services.Pricing;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Marketplace;
using PointOfSale.Core.Interfaces.Payments;
using PointOfSale.Infrastructure.Attribution;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Repositories;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Admin;

// Actual read-only entry points with fictional InMemory rows and strict
// no-network effect boundaries. Not PostgreSQL/concurrency/financial proof.
public sealed class OrderShipmentPresentationContractTests
{
    [Theory]
    [InlineData("controller", null, true)]
    [InlineData("controller", "", true)]
    [InlineData("controller", " ", true)]
    [InlineData("controller", "Fictional method", true)]
    [InlineData("controller", null, false)]
    [InlineData("admin", null, true)]
    [InlineData("admin", "", true)]
    [InlineData("admin", " ", true)]
    [InlineData("admin", "Fictional method", true)]
    [InlineData("admin", null, false)]
    public async Task ReadOnlyOrderKeepsNonNullMethodDisplayAndDoesNotChangeShipment(string entry, string? name, bool hasShipment)
    {
        var tenantId = Guid.NewGuid(); var orderId = Guid.NewGuid(); var shipmentId = Guid.NewGuid();
        var tenant = new TestTenantContext(tenantId);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, tenant);
        var order = new Order
        {
            Id = orderId, TenantId = tenantId, Source = OrderSource.Ecommerce,
            OrderNumber = "FICTIONAL-READ-ONLY", SessionId = "fictional-session", Total = 7m,
            Currency = "EGP", Status = OrderStatus.Pending, PaymentStatus = PaymentStatus.Pending
        };
        db.Orders.Add(order);
        if (hasShipment) db.Shipments.Add(new Shipment
        {
            Id = shipmentId, TenantId = tenantId, OrderId = orderId, ShippingMethodName = name,
            ShipmentNumber = "FICTIONAL-SHIPMENT", CarrierCode = "manual", Status = ShipmentStatus.Pending,
            TrackingNumber = "fictional-tracking"
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var events = new Mock<IEventPublisher>(MockBehavior.Strict);
        var refund = new Mock<IRefundEvidenceService>(MockBehavior.Strict);
        var coupon = new Mock<ICouponValidationService>(MockBehavior.Strict);
        var stock = new Mock<IProductVariantStockRepository>(MockBehavior.Strict);
        string? actualName = null;
        if (entry == "admin")
        {
            var service = new AdminOrderService(db, events.Object, refund.Object, coupon.Object,
                Mock.Of<IPaymentLifecycleService>(MockBehavior.Strict), stock.Object,
                Mock.Of<IR2StorageService>(MockBehavior.Strict), NullLogger<AdminOrderService>.Instance);
            var response = await service.GetOrderDetailsAsync(tenantId, orderId, "fictional-trace");
            Assert.NotNull(response);
            Assert.Equal(7m, response.Totals.Total);
            if (hasShipment)
            {
                var shipment = Assert.IsType<AdminShipmentDto>(response.Shipment);
                actualName = shipment.ShippingMethodName;
                Assert.Equal(shipmentId, shipment.ShipmentId); Assert.Equal("manual", shipment.CarrierName);
                Assert.Equal("fictional-tracking", shipment.TrackingNumber);
            }
            else Assert.Null(response.Shipment);
        }
        else
        {
            var risk = new Mock<IPhoneRiskProfileService>(MockBehavior.Strict);
            risk.Setup(r => r.GetStatusAsync(tenantId, null, default)).ReturnsAsync((AdminPhoneRiskStatusDto?)null);
            risk.Setup(r => r.GetGlobalSpamReportsAsync(tenantId, null, default)).ReturnsAsync(0);
            risk.Setup(r => r.HasReportedSpamAsync(tenantId, orderId, default)).ReturnsAsync(false);
            var velocity = new Mock<IOrderVelocityService>(MockBehavior.Strict);
            velocity.Setup(v => v.GetCurrentAssessmentAsync(null, null, default)).ReturnsAsync(new OrderVelocityAssessment());
            var controller = new OrdersController(db, tenant, events.Object,
                Mock.Of<IMarketplaceEventOutboxService>(MockBehavior.Strict), Mock.Of<IOrderAttributionStore>(MockBehavior.Strict),
                risk.Object, velocity.Object, refund.Object, coupon.Object, Mock.Of<IShippingCalculationService>(MockBehavior.Strict),
                stock.Object, NullLogger<OrdersController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            var response = Assert.IsType<OrderDetailDto>(Assert.IsType<OkObjectResult>((await controller.GetOrder(orderId, default)).Result).Value);
            Assert.Equal(7m, response.Total);
            if (hasShipment)
            {
                Assert.NotNull(response.Shipment); actualName = response.Shipment.ShippingMethodName;
                Assert.Equal(shipmentId, response.Shipment.ShipmentId); Assert.Equal("manual", response.Shipment.CarrierName);
                Assert.Equal("fictional-tracking", response.Shipment.TrackingNumber);
            }
            else Assert.Null(response.Shipment);
        }
        if (hasShipment) Assert.Equal(name ?? string.Empty, actualName);
        Assert.False(db.ChangeTracker.HasChanges());
        var persisted = await db.Shipments.AsNoTracking().SingleOrDefaultAsync();
        if (hasShipment) { Assert.NotNull(persisted); Assert.Equal(name, persisted.ShippingMethodName); }
        else Assert.Null(persisted);
        events.VerifyNoOtherCalls(); refund.VerifyNoOtherCalls(); coupon.VerifyNoOtherCalls(); stock.VerifyNoOtherCalls();
    }
}
