using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Fulfillment;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Explicit primary-development opt-in. No TestDatabaseScope, migration, reset,
/// alternate host or bus. New operational orders are labelled and retained; canonical
/// historical financial records are read/revalidated only.</summary>
[Trait("Category", "PrimaryDevelopmentPostgres")]
public sealed class AffiliatePrimaryDevelopmentPostgresTests
{
    private static readonly Guid Store = Guid.Parse("51510000-2026-4022-a000-000000000001");
    private static readonly Guid CanonicalOrder = Guid.Parse("0cd3b029-f43f-4b0a-840d-8e1e9aaa9969");

    private static async Task<ApplicationDbContext> Open()
    {
        var root = TestConfiguration.GetSolutionRoot();
        var config = new ConfigurationBuilder().SetBasePath(Path.Combine(root, "PointOfSale.API"))
            .AddJsonFile("appsettings.Development.local.json", optional: false).Build();
        var connection = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection"));
        Assert.Equal("PosEcommerceDb_Baseline", connection.Database);
        Assert.Equal("192.168.1.5", connection.Host);
        var tenant = new TenantContext(); tenant.SetTenantId(Store);
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection.ConnectionString).Options, tenant);
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT current_database(), host(inet_server_addr())";
        await using var reader = await cmd.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal("PosEcommerceDb_Baseline", reader.GetString(0)); Assert.Equal("192.168.1.5", reader.GetString(1));
        return db;
    }

    private static AffiliateService Service(ApplicationDbContext db, IEventPublisher? publisher = null)
    {
        var tenant = new TenantContext(); tenant.SetTenantId(Store);
        return new(db, tenant, publisher ?? Mock.Of<IEventPublisher>(MockBehavior.Strict), new AffiliateTelemetry(),
            NullLogger<AffiliateService>.Instance, Mock.Of<IAffiliatePayoutService>(MockBehavior.Strict),
            Mock.Of<IAffiliateSettlementOrchestrator>(MockBehavior.Strict), Options.Create(new AffiliateSettlementOptions()));
    }

    private static async Task<Order> Seed(ApplicationDbContext db)
    {
        Assert.True(await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == Store && t.Name == "P5.1_TEST_NO_REAL_MONEY"));
        var order = new Order { Id = Guid.NewGuid(), TenantId = Store, OrderNumber = $"P53A_NO_MONEY_{Guid.NewGuid():N}",
            CustomerNotes = "P5.3A operational test only; no collection, commission, payout or physical delivery",
            ShippingAddress = "Development fixture — do not ship", Currency = "EGP", PaymentMethodCode = "COD",
            Status = OrderStatus.Shipped, FulfillmentStatus = FulfillmentStatus.OutForDelivery, FulfillmentMode = FulfillmentMode.Manual };
        db.Orders.Add(order); await db.SaveChangesAsync(); return order;
    }

    [PrimaryAffiliateFact]
    public async Task ActualApprovalLoader_ProjectsXmin_AndHoldsRowLockUntilRollback()
    {
        await using var db = await Open(); var order = await Seed(db); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var loader = typeof(AffiliateService).GetMethod("LoadAffiliateApprovalOrderAuthorityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loaded = await (Task<Order>)loader.Invoke(Service(db), [order.Id, CancellationToken.None])!;
        Assert.Equal(order.Id, loaded.Id);
        var tracked = await db.Orders.SingleAsync(o => o.Id == order.Id);
        Assert.True(db.Entry(tracked).Property<uint>("xmin").CurrentValue > 0);

        await using var other = await Open();
        await using (var competing = await other.Database.BeginTransactionAsync())
        {
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'");
            var error = await Assert.ThrowsAsync<PostgresException>(() => other.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Orders\" WHERE \"Id\" = {order.Id} FOR UPDATE"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            await competing.RollbackAsync();
        }
        await transaction.RollbackAsync();
        await other.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Orders\" WHERE \"Id\" = {order.Id} FOR UPDATE");
    }

    [PrimaryAffiliateFact]
    public async Task Xmin_RejectsStaleWriter_OnTaskOwnedOrder()
    {
        await using var first = await Open(); var order = await Seed(first);
        await using var second = await Open(); var stale = await second.Orders.SingleAsync(o => o.Id == order.Id);
        var original = second.Entry(stale).Property<uint>("xmin").CurrentValue;
        order.CustomerNotes += " | first writer"; await first.SaveChangesAsync();
        Assert.NotEqual(original, first.Entry(order).Property<uint>("xmin").CurrentValue);
        stale.CustomerNotes += " | stale writer";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [PrimaryAffiliateFact]
    public async Task DeliveryEvidence_CommitsDurably_AndRejectsReplacementAfterFreshConnection()
    {
        await using var db = await Open(); var order = await Seed(db);
        var publisher = new Mock<IEventPublisher>(); publisher.SetReturnsDefault(Task.CompletedTask);
        var service = new OrderFulfillmentService(db, publisher.Object, NullLogger<OrderFulfillmentService>.Instance);
        Assert.True((await service.MarkDeliveredAsync(order.Id, FulfillmentRequestSource.Manual,
            "  P53A_SIMULATED_NO_MONEY  ", "  https://example.invalid/p53a  ")).Success);
        await using var reload = await Open(); var persisted = await reload.Orders.SingleAsync(o => o.Id == order.Id);
        Assert.Equal("P53A_SIMULATED_NO_MONEY", persisted.DeliveryReference);
        Assert.Equal("https://example.invalid/p53a", persisted.ProofOfDeliveryUrl);
        Assert.Equal(PaymentStatus.Pending, persisted.PaymentStatus);
        var replay = new OrderFulfillmentService(reload, publisher.Object, NullLogger<OrderFulfillmentService>.Instance);
        var result = await replay.MarkDeliveredAsync(order.Id, FulfillmentRequestSource.Manual, "replacement", "https://example.invalid/other");
        Assert.False(result.Success); Assert.Contains("EVIDENCE_CONFLICT", result.ErrorMessage);
        Assert.False(await reload.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(e => e.OrderId == order.Id));
    }

    [PrimaryAffiliateFact]
    public async Task DeliveryPublicationFailure_RollsBackStatusAndEvidence()
    {
        await using var db = await Open(); var order = await Seed(db);
        var publisher = new Mock<IEventPublisher>(MockBehavior.Strict);
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointOfSale.Core.Events.OrderStatusChangedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointOfSale.Core.Events.FulfillmentDeliveredEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("P53A_PUBLICATION_FAILURE"));
        var service = new OrderFulfillmentService(db, publisher.Object, NullLogger<OrderFulfillmentService>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.MarkDeliveredAsync(order.Id, FulfillmentRequestSource.Manual,
            "P53A_ROLLBACK", "https://example.invalid/p53a-rollback"));
        Assert.Equal("P53A_PUBLICATION_FAILURE", error.Message);
        await using var reload = await Open(); var persisted = await reload.Orders.SingleAsync(o => o.Id == order.Id);
        Assert.Equal(FulfillmentStatus.OutForDelivery, persisted.FulfillmentStatus);
        Assert.Null(persisted.DeliveryReference); Assert.Null(persisted.ProofOfDeliveryUrl);
    }

    [PrimaryAffiliateFact]
    public async Task ExistingCanonicalCompletion_RevalidatesPersistedAuthority_AndApprovalReplayIsReadOnly()
    {
        await using var db = await Open();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == CanonicalOrder);
        Assert.Equal(Store, order.TenantId); Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.True(await ManualOrderCompletionAuthority.HasAuthorityAsync(db, order, default));
        var before = await db.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.OrderId == order.Id).Select(e => e.Id).ToListAsync();
        var service = Service(db);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveCommissionForDeliveredOrderAsync(
            order.Id, Store, DateTime.UtcNow, "P53A_READ_ONLY_REVALIDATION", 1, deliveryReference: "substitution", proofOfDeliveryUrl: order.ProofOfDeliveryUrl));
        Assert.Contains("EVIDENCE_REQUIRED", conflict.Message);
        await service.ApproveCommissionForDeliveredOrderAsync(order.Id, Store, DateTime.UtcNow, "P53A_READ_ONLY_REVALIDATION", 1,
            deliveryReference: order.DeliveryReference, proofOfDeliveryUrl: order.ProofOfDeliveryUrl);
        var after = await db.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.OrderId == order.Id).Select(e => e.Id).ToListAsync();
        Assert.Equal(before.Order(), after.Order());
    }
}

public sealed class PrimaryAffiliateFactAttribute : FactAttribute
{
    public PrimaryAffiliateFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AFFILIATE_PRIMARY_DEVELOPMENT_TESTS") != "1")
            Skip = "Requires explicit authorization for task-owned records in PosEcommerceDb_Baseline";
    }
}
