using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Marketplace;
using PointOfSale.Core.Interfaces.Payments;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Repositories;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Observability;
using PointOfSale.Infrastructure.Payments;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Primary database only. Exact browser task order only. All new financial effects rollback.</summary>
public class AffiliateP53dPrimaryPostgresTests
{
    [PrimaryAffiliateFact]
    public void PrimaryOnlyOptIn_RejectsDisposableDatabaseHarnessBeforeInitialization()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestDatabaseScope.Open());
        Assert.Contains("PRIMARY_ONLY_TEST_BOUNDARY", error.Message);
    }

    private static readonly Guid Store = Guid.Parse("c9c5c1ae-949e-498e-bc63-07242daeb49d");
    private static readonly Guid OrderId = Guid.Parse("5aab57e4-fa68-494c-b478-9ad64af75b82");
    private static readonly Guid Actor = Guid.Parse("1bb114d3-a9a3-4a9d-ace3-3690361637a8");
    private static async Task<ApplicationDbContext> Open()
    {
        var config = new ConfigurationBuilder().SetBasePath(Path.Combine(TestConfiguration.GetSolutionRoot(), "PointOfSale.API"))
            .AddJsonFile("appsettings.Development.local.json").Build();
        var cs = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection"));
        Assert.Equal("192.168.1.5", cs.Host); Assert.Equal("PosEcommerceDb_Baseline", cs.Database);
        var tenant = new TenantContext(); tenant.SetTenantId(Store);
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(cs.ConnectionString).Options, tenant);
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT current_database(), host(inet_server_addr())";
        await using var reader = await cmd.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal("PosEcommerceDb_Baseline", reader.GetString(0)); Assert.Equal("192.168.1.5", reader.GetString(1));
        return db;
    }

    private static PaymentLifecycleService Lifecycle(ApplicationDbContext db)
    {
        var correlation = Mock.Of<ICorrelationIdAccessor>(c => c.GetCorrelationId() == "P53D-ROLLBACK-NO-EXTERNAL-MONEY");
        var events = new Mock<IEventPublisher>(); events.SetReturnsDefault(Task.CompletedTask);
        var marketplace = new Mock<IMarketplaceEventOutboxService>(); marketplace.SetReturnsDefault(Task.CompletedTask);
        var accounting = new CommerceAccountingService(new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance),
            new AccountResolver(db, NullLogger<AccountResolver>.Instance), Mock.Of<ICashboxLedgerAccountService>(),
            NullLogger<CommerceAccountingService>.Instance, db);
        return new PaymentLifecycleService(db, new AccountingOutboxService(db, correlation, NullLogger<AccountingOutboxService>.Instance),
            accounting, events.Object, marketplace.Object, correlation, new ProductVariantStockRepository(db, NullLogger<ProductVariantStockRepository>.Instance),
            Mock.Of<IInventoryReservationMetricsService>(), Mock.Of<ICouponValidationService>(),
            NullLogger<PaymentLifecycleService>.Instance, Options.Create(new PaymentLifecycleSettings()));
    }

    [PrimaryAffiliateFact]
    public async Task CompletedCollectedBrowserOrder_SettlementAndReplay_UseBalancedCashJournal_RollbackOnly()
    {
        await using var db = await Open();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == OrderId);
        Assert.Equal("ORD-C9C5-2026-01106", order.OrderNumber);
        Assert.Equal(FulfillmentStatus.Completed, order.FulfillmentStatus);
        var before = await db.LedgerAccounts.AsNoTracking().Where(a => a.AccountCode == "1010").Select(a => a.CurrentBalance).SingleAsync();
        var batches = await db.PaymentSettlementBatches.CountAsync();
        var existing = await db.PaymentSettlementOrders.AsNoTracking().SingleOrDefaultAsync(m => m.OrderId == OrderId);
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var id = existing == null ? Guid.Parse("d53d0000-2026-4023-a000-000000000001") :
                Guid.Parse(await db.PaymentSettlementBatches.Where(b => b.Id == existing.SettlementBatchId).Select(b => b.SettlementId).SingleAsync());
            var request = new ManualCodSettlementRequest(id, [OrderId]);
            var result = await Lifecycle(db).RecordManualCodSettlementAsync(Store, Actor, request);
            Assert.Equal("Posted", result.AccountingOutcome);
            var replay = await Lifecycle(db).RecordManualCodSettlementAsync(Store, Actor, request);
            Assert.True(replay.WasIdempotent); Assert.Equal(result.JournalId, replay.JournalId);
            var journal = await db.JournalEntries.Include(j => j.LedgerEntries).SingleAsync(j => j.Id == result.JournalId);
            Assert.Equal(1010m, journal.TotalDebits); Assert.Equal(journal.TotalDebits, journal.TotalCredits);
            Assert.Equal(Actor, journal.CreatedByUserId);
            Assert.Equal(before + (existing == null ? 1010m : 0m), await db.LedgerAccounts.AsNoTracking().Where(a => a.AccountCode == "1010").Select(a => a.CurrentBalance).SingleAsync());
            Assert.Equal(PaymentStatus.Settled, await db.Orders.AsNoTracking().Where(o => o.Id == OrderId).Select(o => o.PaymentStatus).SingleAsync());
            await tx.RollbackAsync();
        }
        await using var fresh = await Open();
        Assert.Equal(order.PaymentStatus, await fresh.Orders.Where(o => o.Id == OrderId).Select(o => o.PaymentStatus).SingleAsync());
        Assert.Equal(batches, await fresh.PaymentSettlementBatches.CountAsync());
        Assert.Equal(before, await fresh.LedgerAccounts.Where(a => a.AccountCode == "1010").Select(a => a.CurrentBalance).SingleAsync());
    }

    [PrimaryAffiliateFact]
    public async Task InventoryLocksSerializeContenders_RejectOversell_AndRollbackRestoresExactStock()
    {
        var variant = Guid.Parse("fb22b455-1562-4f70-a2c5-8b1f54b6e26c");
        var warehouse = Guid.Parse("2bca8d91-2c09-450e-a713-e783b9cf3d7a");
        await using var first = await Open();
        var baseline = await first.ProductVariantStocks.AsNoTracking().SingleAsync(s => s.VariantId == variant && s.WarehouseId == warehouse);
        Assert.Equal(6m, baseline.Quantity); Assert.Equal(0m, baseline.ReservedQuantity);
        var movementCount = await first.StockMovements.CountAsync(m => m.ProductVariantId == variant);
        var repo = new ProductVariantStockRepository(first, NullLogger<ProductVariantStockRepository>.Instance);
        await using (var tx = await first.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.False(await repo.ReserveAsync(Store, variant, warehouse, 7m, OrderId));
            Assert.True(await repo.ReserveAsync(Store, variant, warehouse, 6m, OrderId));
            var held = await first.ProductVariantStocks.AsNoTracking().SingleAsync(s => s.Id == baseline.Id);
            Assert.Equal(6m, held.Quantity); Assert.Equal(6m, held.ReservedQuantity);
            await using var second = await Open();
            await using var otherTx = await second.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await second.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='300ms'");
            var other = new ProductVariantStockRepository(second, NullLogger<ProductVariantStockRepository>.Instance);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => other.ReserveAsync(Store, variant, warehouse, 1m, OrderId));
            while (error is not PostgresException && error.InnerException != null) error = error.InnerException;
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(error).SqlState);
            await otherTx.RollbackAsync();
            await tx.RollbackAsync();
        }
        await using var retry = await Open();
        await using (var tx = await retry.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var other = new ProductVariantStockRepository(retry, NullLogger<ProductVariantStockRepository>.Instance);
            Assert.True(await other.ReserveAsync(Store, variant, warehouse, 6m, OrderId));
            Assert.True(await other.ReleaseOrderReservationsAsync(Store, OrderId));
            Assert.False(await other.ReleaseOrderReservationsAsync(Store, OrderId));
            await tx.RollbackAsync();
        }
        await using var fresh = await Open();
        var final = await fresh.ProductVariantStocks.SingleAsync(s => s.Id == baseline.Id);
        Assert.Equal(baseline.Quantity, final.Quantity); Assert.Equal(baseline.ReservedQuantity, final.ReservedQuantity);
        Assert.Equal(movementCount, await fresh.StockMovements.CountAsync(m => m.ProductVariantId == variant));
    }

    private static AffiliateSettlementOrchestrator Payout(ApplicationDbContext db) => new(db,
        Mock.Of<IAffiliatePayoutService>(MockBehavior.Strict), Mock.Of<IEventPublisher>(MockBehavior.Strict),
        Mock.Of<IFinancialPostingService>(MockBehavior.Strict), Mock.Of<IAccountResolver>(MockBehavior.Strict),
        Mock.Of<ICashboxLedgerAccountService>(MockBehavior.Strict), NullLogger<AffiliateSettlementOrchestrator>.Instance,
        Options.Create(new AffiliateSettlementOptions()));

    [PrimaryAffiliateFact]
    public async Task PaidPrimaryTuple_ConcurrentReplaysAndActualServerAbortRetry_DoNotDebitAgain()
    {
        var affiliate = Guid.Parse("01a169fd-ffbb-77b7-b97c-547985d3cffd");
        var withdrawal = Guid.Parse("2c6953e7-d79c-479b-bd2d-6ba2499eda0f");
        var reference = $"P53D-SIMULATED-NO-TRANSFER:{withdrawal:D}";
        await using var baseline = await Open();
        var before = await baseline.LedgerAccounts.AsNoTracking().Where(a => a.AccountCode == "1010").Select(a => a.CurrentBalance).SingleAsync();
        Assert.Equal(PayoutRequestStatus.Paid, await baseline.AffiliatePayoutRequests.Where(r => r.Id == withdrawal).Select(r => r.Status).SingleAsync());
        async Task Replay()
        {
            await using var db = await Open();
            var result = await Payout(db).ExecuteAffiliatePayoutAsync(affiliate, Store, 100m, SettlementSource.WithdrawalEngine,
                reference, payoutRequestId: withdrawal, paidByUserId: Actor);
            Assert.True(result.WasIdempotentSkip);
        }
        await Task.WhenAll(Replay(), Replay());
        var attempts = 0;
        await AffiliateFinancialRetry.ExecuteAsync(async attempt =>
        {
            attempts = attempt;
            if (attempt == 1)
            {
                await using var aborted = await Open();
                await using var tx = await aborted.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                try { await aborted.Database.ExecuteSqlRawAsync("DO $$ BEGIN RAISE EXCEPTION 'P53D controlled transaction abort' USING ERRCODE='40001'; END $$"); }
                finally { await tx.RollbackAsync(); }
            }
            await Replay(); return true;
        });
        Assert.Equal(2, attempts);
        await using var conflictDb = await Open();
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => Payout(conflictDb).ExecuteAffiliatePayoutAsync(
            affiliate, Store, 100m, SettlementSource.WithdrawalEngine, "P53D-CONFLICTING-REFERENCE",
            payoutRequestId: withdrawal, paidByUserId: Actor));
        Assert.Contains("IDEMPOTENCY_CONFLICT", conflict.Message);
        await using var fresh = await Open();
        Assert.Equal(before, await fresh.LedgerAccounts.Where(a => a.AccountCode == "1010").Select(a => a.CurrentBalance).SingleAsync());
        Assert.Single(await fresh.AffiliateBalanceLedger.Where(l => l.ReferenceId == withdrawal).ToListAsync());
        Assert.Single(await fresh.AffiliatePayoutTransferAllocations.Where(a => a.ReferenceId == withdrawal).ToListAsync());
        Assert.Single(await fresh.JournalEntries.Where(j => j.SourceType == "AffiliateCommissionPaid" && j.SourceId == withdrawal).ToListAsync());
    }
}
