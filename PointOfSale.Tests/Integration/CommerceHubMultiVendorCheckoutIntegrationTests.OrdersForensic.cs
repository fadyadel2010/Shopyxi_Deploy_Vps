using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Configuration;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Instrumentation;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.CommerceHub.FinancialLedger;
using PointOfSale.API.SuperAdmin.DTOs;
using PointOfSale.API.SuperAdmin.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Repositories;
using PointOfSale.Tests.Infrastructure;
using PointOfSale.Tests.TestDoubles;

namespace PointOfSale.Tests.Integration;

public sealed partial class CommerceHubMultiVendorCheckoutIntegrationTests
{
    [Fact]
    public async Task OrdersForensic_CodSplitCheckoutFulfillmentCancellationAndReadModel_ReconcileRealServices()
    {
        using var db = FinancePostgresTestContext.Create();
        // EnsureCreated includes EF constraints but not migration-owned sequences.
        // Reproduce the production sequence contract in this owned empty database.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SEQUENCE public."CommerceHubMarketplaceOrderNumberSeq"
                START WITH 1 INCREMENT BY 1 MINVALUE 1 NO MAXVALUE CACHE 1 NO CYCLE
            """);
        var fixture = await SeedCheckoutAsync(db, 10m);
        db.CommissionRules.Add(new CommissionRule
        {
            Id = Guid.NewGuid(), ScopeType = CommissionRuleScopeType.Global, Rate = .1m, IsActive = true
        });
        await db.SaveChangesAsync();
        var publisher = CreateEventPublisher();
        var commission = new CommissionService(db, Options.Create(new CommerceHubPaymentsOptions()),
            new CommissionServiceTelemetry(TimeSpan.FromSeconds(30)), NullLogger<CommissionService>.Instance, publisher.Object);
        var checkout = CreateService(db,
            new ProductVariantStockRepository(db, NullLogger<ProductVariantStockRepository>.Instance),
            publisher.Object, fixture, commissionAuthority: commission);
        var response = await checkout.CheckoutAsync(BuildRequest(fixture), fixture.CustomerId,
            "orders-forensic-checkout", "orders-forensic-checkout-key");
        var replay = await checkout.CheckoutAsync(BuildRequest(fixture), fixture.CustomerId,
            "orders-forensic-replay", "orders-forensic-checkout-key");
        Assert.Equal(response.MarketplaceOrderId, replay.MarketplaceOrderId);
        Assert.True(replay.IsIdempotentReplay);
        var order = await db.CommerceHubMarketplaceOrders.Include(o => o.SubOrders).ThenInclude(s => s.Items).SingleAsync();
        Assert.Equal(480m, order.TotalAmount);
        var first = order.SubOrders.Single(s => s.StoreId == fixture.First.StoreId);
        var second = order.SubOrders.Single(s => s.StoreId == fixture.Second.StoreId);
        var frozen = first.CommissionSnapshotJson;
        var financial = new CommerceHubFinancialWorkflowService(db,
            Options.Create(new CommerceHubPaymentsOptions()), Options.Create(new CommerceHubPayoutSettings()),
            new CommerceHubFinancialLedgerService(db, NullLogger<CommerceHubFinancialLedgerService>.Instance),
            new CommerceHubFinancialAuditService(db), NullLogger<CommerceHubFinancialWorkflowService>.Instance);
        var lifecycle = new CommerceHubOrderManagementService(db,
            new InventoryConsistencyService(db, NullLogger<InventoryConsistencyService>.Instance),
            financial, new NoOpUserGovernanceService(), publisher.Object,
            NullLogger<CommerceHubOrderManagementService>.Instance);

        var foreign = await Assert.ThrowsAsync<CommerceHubCheckoutException>(() =>
            lifecycle.UpdateSubOrderStatusAsync(fixture.Second.StoreId, first.Id, OrderStatus.Processing,
                null, null, "foreign-vendor"));
        Assert.Equal("SUBORDER_FORBIDDEN", foreign.Code);
        var jump = await Assert.ThrowsAsync<CommerceHubCheckoutException>(() =>
            lifecycle.UpdateSubOrderStatusAsync(first.StoreId, first.Id, OrderStatus.Delivered,
                null, null, "invalid-jump"));
        Assert.Equal("SUBORDER_STATUS_INVALID", jump.Code);
        var oldVersion = first.ProjectionMutationVersion;
        await lifecycle.UpdateSubOrderStatusAsync(first.StoreId, first.Id, OrderStatus.Processing, oldVersion, null, "process");
        var historyCount = await db.CommerceHubSubOrderStatusHistory.CountAsync();
        await lifecycle.UpdateSubOrderStatusAsync(first.StoreId, first.Id, OrderStatus.Processing, null, null, "duplicate");
        Assert.Equal(historyCount, await db.CommerceHubSubOrderStatusHistory.CountAsync());
        var stale = await Assert.ThrowsAsync<CommerceHubCheckoutException>(() =>
            lifecycle.UpdateSubOrderStatusAsync(first.StoreId, first.Id, OrderStatus.Shipped, oldVersion, null, "stale"));
        Assert.Equal("SUBORDER_VERSION_CONFLICT", stale.Code);
        await lifecycle.UpdateSubOrderStatusAsync(first.StoreId, first.Id, OrderStatus.Shipped, null, null, "ship");
        await lifecycle.UpdateSubOrderStatusAsync(first.StoreId, first.Id, OrderStatus.Delivered, null, null, "deliver");
        Assert.Equal(PaymentStatus.Collected, first.PaymentStatus);
        Assert.Equal(210m, first.Total);
        Assert.Equal(frozen, first.CommissionSnapshotJson);
        Assert.Equal(190m, (await db.CommerceHubVendorEarnings.SingleAsync(e => e.SubOrderId == first.Id)).Amount);
        var stock = await db.ProductVariantStocks.IgnoreQueryFilters().SingleAsync(s => s.VariantId == fixture.First.VariantId);
        Assert.Equal(8m, stock.Quantity); Assert.Equal(0m, stock.ReservedQuantity);

        await lifecycle.UpdateSubOrderStatusAsync(second.StoreId, second.Id, OrderStatus.Cancelled, null, "fixture cancellation", "cancel");
        var secondStock = await db.ProductVariantStocks.IgnoreQueryFilters().SingleAsync(s => s.VariantId == fixture.Second.VariantId);
        Assert.Equal(10m, secondStock.Quantity); Assert.Equal(0m, secondStock.ReservedQuantity);
        Assert.False(await db.CommerceHubVendorEarnings.AnyAsync(e => e.SubOrderId == second.Id));
        var resurrection = await Assert.ThrowsAsync<CommerceHubCheckoutException>(() =>
            lifecycle.UpdateSubOrderStatusAsync(second.StoreId, second.Id, OrderStatus.Processing, null, null, "resurrection"));
        Assert.Equal("SUBORDER_STATUS_INVALID", resurrection.Code);

        var evidence = new Mock<IRefundEvidenceService>();
        evidence.Setup(e => e.GetEvidenceLookupAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<RefundEvidenceMediaDto>>());
        var reader = new SuperAdminOrdersService(db, evidence.Object, NullLogger<SuperAdminOrdersService>.Instance);
        var details = await reader.GetOrderDetailsAsync(order.Id);
        Assert.NotNull(details); Assert.Equal(2, details.SubOrders.Count);
        Assert.Equal(4, details.StatusHistory.Count);
        Assert.Equal(1, (await reader.GetOrdersAsync(new() { TenantId = first.StoreId })).TotalCount);
        Assert.Equal(210m, (await reader.GetOrderStatsAsync()).CapturedSalesToday);
        Assert.Equal(1, (await reader.ExportOrdersCsvAsync(new() { TenantId = second.StoreId })).RowCount);
    }
}
