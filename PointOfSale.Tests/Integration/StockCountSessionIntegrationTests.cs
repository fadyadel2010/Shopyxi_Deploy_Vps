using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Inventory;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Repositories;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Integration;

public sealed class StockCountSessionIntegrationTests
{
    [Fact]
    public void AdditiveCountMigration_MatchesCurrentModel_AndNeverSeedsOrRewritesHistory()
    {
        using var scope = TestDatabaseScope.Open(); using var db = scope.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript("20261113110000_AccountingPeriodAuthority", "20261113120000_StockCountSessionAuthority");
        foreach (var forbidden in new[] { "DROP TABLE", "DELETE FROM", "TRUNCATE", "UPDATE ", "INSERT INTO \"LedgerAccounts\"" })
            Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.StockCountSessions.IgnoreQueryFilters());
        Assert.Empty(db.StockCountLines.IgnoreQueryFilters());
        Assert.Empty(db.StockCountEvents.IgnoreQueryFilters());
        var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (directory != null) File.WriteAllText(Path.Combine(directory, "reviewed-stock-count-upgrade.sql"), script);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(8)]
    [InlineData(10)]
    public async Task CountPostReplayAndLinkedReversal_RetainHistory_AndRestoreStockAndLedger(decimal counted)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        var draft = await Create(scope, s);
        Assert.Equal(10, Assert.Single(draft.Lines).SystemQuantity);
        Assert.Equal(2, draft.Lines.Single().DamagedSnapshot);
        var saved = await Count(scope, s, draft, counted);
        StockCountSession posted;
        var request = new PostStockCount(saved.Version, s.Expense, "QA-ERP-R10-20261113-COUNT-POST");
        await using (var db = scope.CreateDbContext(s.Tenant)) posted = await Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, request);
        await using (var db = scope.CreateDbContext(s.Tenant)) Assert.Equal(posted.JournalId, (await Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, request)).JournalId);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            Assert.Equal(counted, (await db.ProductVariantStocks.SingleAsync()).Quantity);
            Assert.Equal(2, (await db.ProductVariantStocks.SingleAsync()).DamagedQuantity);
            Assert.Equal(counted == 10 ? 0 : 1, await db.JournalEntries.CountAsync());
            Assert.Equal(counted == 10 ? 0 : 1, await db.StockMovements.CountAsync());
            Assert.Equal((counted - 10) * 5, (await db.StockCountLines.SingleAsync()).PostedValue);
            if (counted != 10)
            {
                var journal = await db.JournalEntries.Include(x => x.LedgerEntries).SingleAsync();
                Assert.Equal(s.Owner, journal.CreatedByUserId);
                Assert.Equal(journal.LedgerEntries.Sum(x => x.DebitAmount), journal.LedgerEntries.Sum(x => x.CreditAmount));
                Assert.Equal(s.Owner, (await db.StockMovements.SingleAsync()).CreatedBy);
            }
        }
        StockCountSession reversed;
        await using (var db = scope.CreateDbContext(s.Tenant)) reversed = await Service(db, s).CancelOrReverseAsync(s.Tenant, draft.Id, s.Owner, new(posted.Version, "QA-ERP-R10-20261113-COUNT-COMPENSATION"), true);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(StockCountStatus.Reversed, reversed.Status);
        Assert.Equal(10, (await verify.ProductVariantStocks.SingleAsync()).Quantity);
        Assert.Equal(2, (await verify.ProductVariantStocks.SingleAsync()).DamagedQuantity);
        Assert.All(await verify.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
        Assert.Equal(counted == 10 ? 0 : 2, await verify.JournalEntries.CountAsync());
        Assert.Equal(counted == 10 ? 0 : 2, await verify.StockMovements.CountAsync());
        Assert.Equal(0, await verify.StockMovements.SumAsync(x => x.QuantityChange));
        Assert.Equal(4, await verify.StockCountEvents.CountAsync());
        Assert.All(await verify.StockCountEvents.ToListAsync(), x => Assert.Equal(s.Owner, x.CreatedBy));
        if (counted != 10) Assert.Equal(posted.JournalId, (await verify.JournalEntries.SingleAsync(x => x.Id == reversed.ReversalJournalId)).ReversesJournalId);
        var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (directory != null) await File.WriteAllTextAsync(Path.Combine(directory, $"stock-count-reconciliation-{counted}.json"), JsonSerializer.Serialize(new
        { localOnly = true, s.Tenant, s.Owner, draft.Id, posted.JournalId, reversed.ReversalJournalId, opening = 10, counted, finalQuantity = 10, netLedger = 0, historyRetained = true }));
    }

    [Fact]
    public async Task DuplicateCreate_UsesOneSnapshot_AndChangedPayloadConflicts()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        var request = Request(s); StockCountSession first;
        await using (var db = scope.CreateDbContext(s.Tenant)) first = await Service(db, s).CreateAsync(s.Tenant, s.Owner, request);
        await using (var db = scope.CreateDbContext(s.Tenant)) Assert.Equal(first.Number, (await Service(db, s).CreateAsync(s.Tenant, s.Owner, request)).Number);
        await using (var db = scope.CreateDbContext(s.Tenant)) Assert.Equal("STOCK_COUNT_KEY_REUSED", (await Assert.ThrowsAsync<InventoryWorkflowException>(() => Service(db, s).CreateAsync(s.Tenant, s.Owner, request with { Reason = "Changed" }))).Code);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Single(await verify.StockCountSessions.ToListAsync()); Assert.Single(await verify.StockCountEvents.ToListAsync());
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("reason")]
    [InlineData("account")]
    [InlineData("closed-period")]
    [InlineData("version")]
    [InlineData("value")]
    public async Task RejectedPost_RollsBackEverySourceStockMovementJournalAndOutbox(string failure)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        var draft = await Create(scope, s);
        var saved = failure == "incomplete" ? draft : await Count(scope, s, draft, failure == "value" ? 99999999999999.9999m : 12, failure == "reason" ? null : "QA variance");
        if (failure == "closed-period")
        {
            await using var close = scope.CreateDbContext(s.Tenant);
            var period = await close.AccountingPeriods.SingleAsync();
            await new AccountingPeriodService(close).TransitionAsync(s.Tenant, period.Id, s.Owner, "close", period.Version, "QA denial");
        }
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            var exception = await Record.ExceptionAsync(() => Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner,
                new(failure == "version" ? 0 : saved.Version, failure == "account" ? Guid.NewGuid() : s.Expense, "QA rejected post")));
            Assert.NotNull(exception);
            if (failure == "closed-period") Assert.IsType<AccountingPeriodException>(exception);
            else Assert.IsType<InventoryWorkflowException>(exception);
        }
        await AssertUnposted(scope, s);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0.00001)]
    public async Task InvalidPhysicalCount_IsRejectedWithoutProgressOrStockChanges(decimal quantity)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s);
        await Assert.ThrowsAsync<InventoryWorkflowException>(() => Count(scope, s, draft, quantity));
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Null((await verify.StockCountLines.SingleAsync()).CountedQuantity);
        Assert.Equal(1, (await verify.StockCountSessions.SingleAsync()).Version);
        await AssertUnposted(scope, s);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterveningStockOrReservationMutation_MakesSnapshotStale(bool reserve)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s); var saved = await Count(scope, s, draft, 12);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            if (reserve)
                Assert.True(await new ProductVariantStockRepository(db, NullLogger<ProductVariantStockRepository>.Instance)
                    .ReserveAsync(s.Tenant, s.Variant, s.Warehouse, 1, Guid.NewGuid()));
            else
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var stock = await ProductVariantStockMutationSupport.LockStockForMutationAsync(db, s.Tenant, s.Warehouse, s.Variant, false);
                stock!.Quantity++; await db.SaveChangesAsync(); await tx.CommitAsync();
            }
        }
        await using (var db = scope.CreateDbContext(s.Tenant)) Assert.Equal("STOCK_COUNT_STALE", (await Assert.ThrowsAsync<InventoryWorkflowException>(() => Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, s.Expense, "QA stale rejection")))).Code);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(StockCountStatus.Draft, (await verify.StockCountSessions.SingleAsync()).Status);
        Assert.Empty(await verify.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task CountCannotConsumeReservedUnits_AndCanonicalAdjustmentUsesSameFloor()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using (var db = scope.CreateDbContext(s.Tenant)) Assert.True(await new ProductVariantStockRepository(db, NullLogger<ProductVariantStockRepository>.Instance)
            .ReserveAsync(s.Tenant, s.Variant, s.Warehouse, 3, Guid.NewGuid()));
        Guid[] reservationMovements, reservationOutbox;
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            reservationMovements = await db.StockMovements.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
            reservationOutbox = await db.StockSyncOutboxMessages.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        }
        var draft = await Create(scope, s); Assert.Equal(3, draft.Lines.Single().ReservedSnapshot);
        var saved = await Count(scope, s, draft, 2);
        await using (var db = scope.CreateDbContext(s.Tenant)) Assert.NotNull(await Record.ExceptionAsync(() => Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, s.Expense, "QA reserved denial"))));
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            Assert.NotNull(await Record.ExceptionAsync(() => new InventoryService(db, new TestTenantContext(s.Tenant), NullLogger<InventoryService>.Instance)
                .AdjustStockAsync(s.Warehouse, s.Product, -8, StockMovementType.Adjustment, "QA floor", s.Variant)));
        }
        await AssertUnposted(scope, s, reservationMovements, reservationOutbox);
        await using var reserved = scope.CreateDbContext(s.Tenant);
        Assert.Equal(3, (await reserved.ProductVariantStocks.SingleAsync()).ReservedQuantity);
    }

    [Fact]
    public async Task CurrentSqlActorAndTenantIsolation_AreRequired_AndWarehouseCannotPostValuation()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s); var saved = await Count(scope, s, draft, 12);
        foreach (var actor in new[] { s.Cashier, s.WarehouseActor, Guid.NewGuid() })
        {
            await using var db = scope.CreateDbContext(s.Tenant);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(db, s).PostAsync(s.Tenant, draft.Id, actor, new(saved.Version, s.Expense, "QA permission denial")));
        }
        await using (var db = scope.CreateDbContext(Guid.NewGuid()))
        {
            Assert.Empty(await db.StockCountSessions.ToListAsync()); Assert.Empty(await db.StockCountLines.ToListAsync()); Assert.Empty(await db.StockCountEvents.ToListAsync());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(db, s).CancelOrReverseAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, "QA tenant denial"), false));
        }
        await AssertUnposted(scope, s);
    }

    [Fact]
    public async Task CancelledDraftRetainsSnapshot_AndDirectSnapshotHistoryEditsAreDenied()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s);
        await using (var db = scope.CreateDbContext(s.Tenant)) await Service(db, s).CancelOrReverseAsync(s.Tenant, draft.Id, s.Owner, new(draft.Version, "QA cancelled"), false);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            (await db.StockCountLines.SingleAsync()).SystemQuantity = 999;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            using var authority = InventoryWorkflowMutationScope.Authorize();
            var session = await db.StockCountSessions.SingleAsync(); session.Status = StockCountStatus.Draft;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            (await db.StockCountEvents.FirstAsync()).Reason = "Forged";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(StockCountStatus.Cancelled, (await verify.StockCountSessions.SingleAsync()).Status);
        Assert.Equal(10, (await verify.StockCountLines.SingleAsync()).SystemQuantity); Assert.Equal(2, await verify.StockCountEvents.CountAsync());
    }

    [Fact]
    public async Task ConcurrentConfirmations_PostExactlyOnce_WithOneJournalAndOneMovement()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s); var saved = await Count(scope, s, draft, 12);
        async Task<Guid?> Confirm()
        {
            await using var db = scope.CreateDbContext(s.Tenant);
            return (await Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, s.Expense, "QA concurrent confirmation"))).JournalId;
        }
        var results = await Task.WhenAll(Confirm(), Confirm()); Assert.Equal(results[0], results[1]);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Single(await verify.JournalEntries.ToListAsync()); Assert.Single(await verify.StockMovements.ToListAsync());
        Assert.Equal(12, (await verify.ProductVariantStocks.SingleAsync()).Quantity); Assert.Equal(3, await verify.StockCountEvents.CountAsync());
    }

    [Fact]
    public async Task PostedLinesCannotBeRecounted_EvenInsideTrustedMutationScope()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s); var saved = await Count(scope, s, draft, 12);
        await using (var db = scope.CreateDbContext(s.Tenant)) await Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, s.Expense, "QA posted immutability"));
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            using var authority = InventoryWorkflowMutationScope.Authorize();
            var session = await db.StockCountSessions.Include(x => x.Lines).SingleAsync(); session.Lines.Single().CountedQuantity = 20;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    private sealed record Scenario(Guid Tenant, Guid Owner, Guid Cashier, Guid WarehouseActor, Guid Warehouse, Guid Product, Guid Variant, Guid Expense);
    [Fact]
    public async Task ZeroVarianceCountCannotRetainAnInvalidOrForeignVarianceAccount()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s); var saved = await Count(scope, s, draft, 10);
        await using (var db = scope.CreateDbContext(s.Tenant))
            Assert.Equal("STOCK_COUNT_VARIANCE_ACCOUNT", (await Assert.ThrowsAsync<InventoryWorkflowException>(() => Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, Guid.NewGuid(), "QA invalid zero-variance account")))).Code);
        await AssertUnposted(scope, s);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            var posted = await Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, null, "QA zero-variance confirmation"));
            Assert.Null(posted.VarianceAccountId); Assert.Null(posted.JournalId);
        }
    }
    [Fact]
    public async Task FullAuditReasonIsRetained_WhileJournalSummariesRespectLedgerLimits()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s); var saved = await Count(scope, s, draft, 12);
        var reason = new string('Q', 1000); StockCountSession posted;
        await using (var db = scope.CreateDbContext(s.Tenant)) posted = await Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, s.Expense, reason));
        await using (var db = scope.CreateDbContext(s.Tenant)) await Service(db, s).CancelOrReverseAsync(s.Tenant, draft.Id, s.Owner, new(posted.Version, reason), true);
        await using var verify = scope.CreateDbContext(s.Tenant);
        var session = await verify.StockCountSessions.SingleAsync(); Assert.Equal(reason, session.PostingReason); Assert.Equal(reason, session.ReversalReason);
        Assert.All(await verify.JournalEntries.ToListAsync(), x => Assert.InRange(x.Description.Length, 1, 500));
        Assert.Equal(2, await verify.StockCountEvents.CountAsync(x => x.Reason == reason));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedReversal_PreservesPostedDocumentAndAllHistory(bool closedPeriod)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s); var saved = await Count(scope, s, draft, 12);
        StockCountSession posted;
        await using (var db = scope.CreateDbContext(s.Tenant)) posted = await Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, s.Expense, "QA reversal denial setup"));
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            if (closedPeriod)
            {
                var period = await db.AccountingPeriods.SingleAsync();
                await new AccountingPeriodService(db).TransitionAsync(s.Tenant, period.Id, s.Owner, "close", period.Version, "QA closed reversal period");
            }
            else
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await new InventoryService(db, new TestTenantContext(s.Tenant), NullLogger<InventoryService>.Instance)
                    .AdjustStockAsync(s.Warehouse, s.Product, -11, StockMovementType.Adjustment, "QA consume stock before reversal", s.Variant);
                await db.SaveChangesAsync(); await tx.CommitAsync();
            }
        }
        await using (var db = scope.CreateDbContext(s.Tenant)) Assert.NotNull(await Record.ExceptionAsync(() => Service(db, s)
            .CancelOrReverseAsync(s.Tenant, draft.Id, s.Owner, new(posted.Version, "QA rejected compensation"), true)));
        await using var verify = scope.CreateDbContext(s.Tenant);
        var document = await verify.StockCountSessions.SingleAsync();
        Assert.Equal(StockCountStatus.Posted, document.Status); Assert.Null(document.ReversalJournalId);
        Assert.Equal(posted.Version, document.Version); Assert.Equal(3, await verify.StockCountEvents.CountAsync());
        Assert.Single(await verify.JournalEntries.ToListAsync());
        Assert.Equal(closedPeriod ? 1 : 2, await verify.StockMovements.CountAsync());
        Assert.Equal(closedPeriod ? 12 : 1, (await verify.ProductVariantStocks.SingleAsync()).Quantity);
        Assert.Null((await verify.StockCountLines.SingleAsync()).ReversalMovementId);
    }

    [Fact]
    public async Task FrozenValuationSurvivesLaterCostChange_AndMixedGainLossPostsGrossBalancedLines()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        var secondVariant = Guid.NewGuid();
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            (await db.Products.SingleAsync()).HasVariants = true;
            db.ProductVariants.Add(new ProductVariant { Id = secondVariant, TenantId = s.Tenant, ProductId = s.Product, SKU = "QA-SECOND", CostPrice = 3, Price = 10, IsActive = true });
            db.ProductVariantStocks.Add(new ProductVariantStock { Id = Guid.NewGuid(), TenantId = s.Tenant, VariantId = secondVariant, WarehouseId = s.Warehouse, Quantity = 10 });
            await db.SaveChangesAsync();
        }
        StockCountSession draft, saved, posted;
        await using (var db = scope.CreateDbContext(s.Tenant)) draft = await Service(db, s).CreateAsync(s.Tenant, s.Owner, Request(s) with { VariantIds = [s.Variant, secondVariant] });
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            (await db.ProductVariants.SingleAsync(x => x.Id == s.Variant)).CostPrice = 9;
            await db.SaveChangesAsync();
        }
        await using (var db = scope.CreateDbContext(s.Tenant)) saved = await Service(db, s).SaveAsync(s.Tenant, draft.Id, s.Owner,
            new(draft.Version, draft.Lines.Select(l => new EnterStockCountLine(l.Id, l.VariantId == s.Variant ? 12 : 8, "QA mixed variance")).ToList()));
        await using (var db = scope.CreateDbContext(s.Tenant)) posted = await Service(db, s).PostAsync(s.Tenant, draft.Id, s.Owner, new(saved.Version, s.Expense, "QA frozen gross variance"));
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            var journal = await db.JournalEntries.Include(x => x.LedgerEntries).SingleAsync();
            Assert.Equal(4, journal.LedgerEntries.Count);
            Assert.Equal(16, journal.LedgerEntries.Sum(x => x.DebitAmount)); Assert.Equal(16, journal.LedgerEntries.Sum(x => x.CreditAmount));
            Assert.Equal(10, (await db.StockCountLines.SingleAsync(x => x.VariantId == s.Variant)).PostedValue);
            Assert.Equal(-6, (await db.StockCountLines.SingleAsync(x => x.VariantId == secondVariant)).PostedValue);
        }
        await using (var db = scope.CreateDbContext(s.Tenant)) await Service(db, s).CancelOrReverseAsync(s.Tenant, draft.Id, s.Owner, new(posted.Version, "QA mixed compensation"), true);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.All(await verify.ProductVariantStocks.ToListAsync(), x => Assert.Equal(10, x.Quantity));
        Assert.All(await verify.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
        Assert.Equal(4, await verify.StockMovements.CountAsync());
    }

    [Fact]
    public async Task StaleCountVersionAndRevokedSqlRole_CannotOverwriteSavedProgress()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); var draft = await Create(scope, s); await Count(scope, s, draft, 12);
        await Assert.ThrowsAsync<InventoryWorkflowException>(() => Count(scope, s, draft, 8));
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            (await db.Users.SingleAsync(x => x.Id == s.Owner)).Role = UserRole.Cashier; await db.SaveChangesAsync();
        }
        await using (var db = scope.CreateDbContext(s.Tenant)) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(db, s)
            .PostAsync(s.Tenant, draft.Id, s.Owner, new(2, s.Expense, "QA revoked owner denial")));
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(12, (await verify.StockCountLines.SingleAsync()).CountedQuantity);
        Assert.Equal(2, (await verify.StockCountSessions.SingleAsync()).Version);
        Assert.Empty(await verify.JournalEntries.ToListAsync());
    }

    private static async Task<Scenario> Seed(TestDatabaseScope scope)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = scope.CreateDbContext(s.Tenant);
        db.Tenants.Add(new Tenant { Id = s.Tenant, Name = "QA-ERP-R10-20261113-COUNT", Currency = "EGP", Status = TenantStatus.Active, IsActive = true });
        foreach (var (id, role) in new[] { (s.Owner, UserRole.Owner), (s.Cashier, UserRole.Cashier), (s.WarehouseActor, UserRole.Warehouse) })
            db.Users.Add(new User { Id = id, TenantId = s.Tenant, Username = $"qa-{id:N}", Email = $"{id:N}@example.invalid", PasswordHash = "not-a-login", Role = role, IsActive = true });
        db.Warehouses.Add(new Warehouse { Id = s.Warehouse, TenantId = s.Tenant, Code = "QA-COUNT", Name = "QA count warehouse", IsActive = true });
        var category = new Category { Id = Guid.NewGuid(), TenantId = s.Tenant, Name = "QA count" }; db.Categories.Add(category);
        db.Products.Add(new Product { Id = s.Product, TenantId = s.Tenant, Name = "QA counted product", CategoryId = category.Id, TrackStock = true, IsActive = true });
        db.ProductVariants.Add(new ProductVariant { Id = s.Variant, TenantId = s.Tenant, ProductId = s.Product, SKU = "QA-COUNT", IsActive = true, IsDefault = true, CostPrice = 5, Price = 10 });
        db.ProductVariantStocks.Add(new ProductVariantStock { Id = Guid.NewGuid(), TenantId = s.Tenant, VariantId = s.Variant, WarehouseId = s.Warehouse, Quantity = 10, DamagedQuantity = 2 });
        var expense = Account.Create(s.Tenant, "QA-VARIANCE", "QA inventory variance", AccountType.Expense); expense.Id = s.Expense;
        db.LedgerAccounts.AddRange(Account.Create(s.Tenant, AccountCodes.Inventory, "QA inventory", AccountType.Asset), expense);
        await db.SaveChangesAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await new AccountingPeriodService(db).CreateAsync(s.Tenant, s.Owner, "QA-COUNT", "QA count period", today.AddDays(-1), today.AddDays(1), true, "QA count acceptance");
        return s;
    }
    private static StockCountService Service(ApplicationDbContext db, Scenario s) => new(db,
        new InventoryService(db, new TestTenantContext(s.Tenant), NullLogger<InventoryService>.Instance),
        new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance), new AccountResolver(db, NullLogger<AccountResolver>.Instance));
    private static CreateStockCount Request(Scenario s) => new(Guid.NewGuid(), s.Warehouse, DateOnly.FromDateTime(DateTime.UtcNow), [s.Variant], "QA-ERP-R10-20261113-COUNT");
    private static async Task<StockCountSession> Create(TestDatabaseScope scope, Scenario s)
    { await using var db = scope.CreateDbContext(s.Tenant); return await Service(db, s).CreateAsync(s.Tenant, s.Owner, Request(s)); }
    private static async Task<StockCountSession> Count(TestDatabaseScope scope, Scenario s, StockCountSession draft, decimal quantity, string? reason = "QA physical variance")
    {
        await using var db = scope.CreateDbContext(s.Tenant);
        return await Service(db, s).SaveAsync(s.Tenant, draft.Id, s.Owner, new(draft.Version, [new(draft.Lines.Single().Id, quantity, reason)]));
    }
    private static async Task AssertUnposted(TestDatabaseScope scope, Scenario s, Guid[]? existingMovements = null, Guid[]? existingOutbox = null)
    {
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(10, (await verify.ProductVariantStocks.SingleAsync()).Quantity);
        Assert.Equal(StockCountStatus.Draft, (await verify.StockCountSessions.SingleAsync()).Status);
        Assert.Equal(existingMovements ?? [], await verify.StockMovements.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
        Assert.Empty(await verify.JournalEntries.ToListAsync());
        Assert.Equal(existingOutbox ?? [], await verify.StockSyncOutboxMessages.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
        Assert.All(await verify.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
    }
}
