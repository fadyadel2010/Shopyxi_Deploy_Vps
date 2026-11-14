using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Inventory;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Repositories;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Integration;

public sealed class StockTransferWorkflowIntegrationTests
{
    [Fact]
    public void AdditiveUpgradePreservesExistingStatusValuesAndDocumentNumbers_AndMatchesModel()
    {
        using var scope = TestDatabaseScope.Open(); using var db = scope.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, new[] { (int)TransferStatus.Pending, (int)TransferStatus.Approved, (int)TransferStatus.Rejected, (int)TransferStatus.Completed, (int)TransferStatus.Cancelled });
        var script = db.GetService<IMigrator>().GenerateScript("20261113120000_StockCountSessionAuthority", "20261113130000_StockTransferDispatchReceiptAuthority");
        foreach (var forbidden in new[] { "DROP TABLE", "DELETE FROM", "TRUNCATE", "UPDATE \"StockTransfers\"", "setval(" }) Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.StockTransferEvents.IgnoreQueryFilters());
        var evidence = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (evidence != null) File.WriteAllText(Path.Combine(evidence, "reviewed-transfer-workflow-upgrade.sql"), script);
    }

    [Fact]
    public async Task DispatchReceiveAndLinkedReversal_PreserveEveryActorMovement_AndRestoreBothWarehouses()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        var dispatched = await Act(scope, s, "dispatch", 1);
        Assert.Equal(TransferStatus.Dispatched, dispatched.Status); Assert.Equal(2, dispatched.WorkflowVersion);
        await Check(scope, s, 8, 0, 1, 1);
        Assert.Equal(s.Number, dispatched.TransferNumber);
        var received = await Act(scope, s, "receive", 2);
        Assert.Equal(TransferStatus.Completed, received.Status); await Check(scope, s, 8, 2, 2, 2);
        var reversed = await Act(scope, s, "reverse", 3);
        Assert.Equal(TransferStatus.Reversed, reversed.Status); await Check(scope, s, 10, 0, 4, 3);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.All(await verify.StockMovements.ToListAsync(), x => { Assert.Equal(s.Owner, x.CreatedBy); Assert.Equal(s.Id, x.ReferenceId); Assert.Equal(s.Line, x.ReferenceLineId); Assert.Equal(s.Number, x.ReferenceNumber); });
        Assert.All(await verify.StockTransferEvents.ToListAsync(), x => { Assert.Equal(s.Owner, x.CreatedBy); Assert.False(x.IsDeleted); Assert.NotEmpty(JsonDocument.Parse(x.DetailsJson).RootElement.EnumerateArray()); });
        Assert.Equal(0, await verify.StockMovements.SumAsync(x => x.QuantityChange));
        Assert.Empty(await verify.JournalEntries.ToListAsync());
        var evidence = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (evidence != null) await File.WriteAllTextAsync(Path.Combine(evidence, "dispatch-receipt-reversal-reconciliation.json"), JsonSerializer.Serialize(new
        { localOnly = true, s.Tenant, s.Owner, s.Id, s.Number, sourceOpening = 10, dispatched = 2, destinationReceived = 2, sourceFinal = 10, destinationFinal = 0, retainedMovements = 4, historyRetained = true }));
    }

    [Theory]
    [InlineData(TransferStatus.Pending)]
    [InlineData(TransferStatus.Approved)]
    [InlineData(TransferStatus.Dispatched)]
    public async Task CancellationRestoresInTransitUnitsOnly_AndRetainsOriginalDocument(TransferStatus status)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, status == TransferStatus.Pending ? status : TransferStatus.Approved);
        var version = 1L;
        if (status == TransferStatus.Dispatched) version = (await Act(scope, s, "dispatch", version)).WorkflowVersion;
        var result = await Act(scope, s, "cancel", version);
        Assert.Equal(TransferStatus.Cancelled, result.Status);
        Assert.Equal(s.Number, result.TransferNumber); Assert.NotNull(result.CancellationReason);
        await Check(scope, s, 10, 0, status == TransferStatus.Dispatched ? 2 : 0, status == TransferStatus.Dispatched ? 2 : 1);
        await using var db = scope.CreateDbContext(s.Tenant);
        Assert.Single(await db.StockTransfers.ToListAsync()); Assert.Single(await db.StockTransferItems.ToListAsync());
    }

    [Theory]
    [InlineData("dispatch", 1)]
    [InlineData("receive", 2)]
    [InlineData("reverse", 3)]
    public async Task ConcurrentIdenticalConfirmationsAndResponseLossReplay_ApplyEachActionOnce(string action, long version)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        if (version >= 2) await Act(scope, s, "dispatch", 1);
        if (version >= 3) await Act(scope, s, "receive", 2);
        var results = await Task.WhenAll(Act(scope, s, action, version), Act(scope, s, action, version));
        Assert.Equal(results[0].WorkflowVersion, results[1].WorkflowVersion);
        Assert.Equal(results[0].WorkflowVersion, (await Act(scope, s, action, version)).WorkflowVersion);
        await Check(scope, s, action == "reverse" ? 10 : 8, action == "receive" ? 2 : 0, action == "reverse" ? 4 : (int)version, (int)version);
        await using var db = scope.CreateDbContext(s.Tenant);
        Assert.Equal("TRANSFER_CONFLICT", (await Assert.ThrowsAsync<InventoryWorkflowException>(() => Service(db, s).ExecuteAsync(s.Tenant, s.Id, s.Owner, action, new(version, "Changed confirmation")))).Code);
    }

    [Fact]
    public async Task ConcurrentReceiveAndCancel_OnlyOneCustodyTransitionCanWin()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); await Act(scope, s, "dispatch", 1);
        async Task<Exception?> Try(string action) => await Record.ExceptionAsync(() => Act(scope, s, action, 2));
        var outcomes = await Task.WhenAll(Try("receive"), Try("cancel"));
        Assert.Single(outcomes.Where(x => x == null)); Assert.IsType<InventoryWorkflowException>(Assert.Single(outcomes.Where(x => x != null)));
        await using var db = scope.CreateDbContext(s.Tenant); var transfer = await db.StockTransfers.SingleAsync();
        Assert.Contains(transfer.Status, new[] { TransferStatus.Completed, TransferStatus.Cancelled });
        await Check(scope, s, transfer.Status == TransferStatus.Completed ? 8 : 10, transfer.Status == TransferStatus.Completed ? 2 : 0, 2, 2);
    }

    [Fact]
    public async Task ReservedSourceUnitsCannotBeDispatched_AndFailedConfirmationPreservesReservationHistory()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        Guid[] before;
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            Assert.True(await new ProductVariantStockRepository(db, NullLogger<ProductVariantStockRepository>.Instance).ReserveAsync(s.Tenant, s.Variant, s.Source, 9, Guid.NewGuid()));
            before = await db.StockMovements.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        }
        await Assert.ThrowsAsync<InsufficientStockException>(() => Act(scope, s, "dispatch", 1));
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(10, (await verify.ProductVariantStocks.SingleAsync(x => x.WarehouseId == s.Source)).Quantity);
        Assert.Equal(9, (await verify.ProductVariantStocks.SingleAsync(x => x.WarehouseId == s.Source)).ReservedQuantity);
        Assert.Equal(before, await verify.StockMovements.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
        Assert.Empty(await verify.StockTransferEvents.ToListAsync()); Assert.Equal(TransferStatus.Approved, (await verify.StockTransfers.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReservedDestinationUnitsBlockReversal_WithoutAnyPartialCompensation()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); await Act(scope, s, "dispatch", 1); await Act(scope, s, "receive", 2);
        Guid[] before;
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            Assert.True(await new ProductVariantStockRepository(db, NullLogger<ProductVariantStockRepository>.Instance).ReserveAsync(s.Tenant, s.Variant, s.Target, 1, Guid.NewGuid()));
            before = await db.StockMovements.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        }
        await Assert.ThrowsAsync<InsufficientStockException>(() => Act(scope, s, "reverse", 3));
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(before, await verify.StockMovements.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
        Assert.Equal(TransferStatus.Completed, (await verify.StockTransfers.SingleAsync()).Status);
        Assert.Equal(8, (await verify.ProductVariantStocks.SingleAsync(x => x.WarehouseId == s.Source)).Quantity);
        Assert.Equal(2, (await verify.ProductVariantStocks.SingleAsync(x => x.WarehouseId == s.Target)).Quantity);
        Assert.Equal(2, await verify.StockTransferEvents.CountAsync());
    }

    [Theory]
    [InlineData(TransferStatus.Pending, "receive")]
    [InlineData(TransferStatus.Approved, "receive")]
    [InlineData(TransferStatus.Approved, "reverse")]
    [InlineData(TransferStatus.Rejected, "dispatch")]
    [InlineData(TransferStatus.Cancelled, "dispatch")]
    public async Task InvalidTransitionsAndMissingReasonFailBeforeAnyStockMutation(TransferStatus status, string action)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, status);
        await Assert.ThrowsAsync<InventoryWorkflowException>(() => Act(scope, s, action, 1));
        await using (var db = scope.CreateDbContext(s.Tenant)) await Assert.ThrowsAsync<InventoryWorkflowException>(() => Service(db, s).ExecuteAsync(s.Tenant, s.Id, s.Owner, "dispatch", new(1, " ")));
        await Check(scope, s, 10, 0, 0, 0);
    }

    [Fact]
    public async Task TenantIsolationAndCurrentSqlPermissionsRejectUnauthorizedActors()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using (var db = scope.CreateDbContext(s.Tenant)) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(db, s).ExecuteAsync(s.Tenant, s.Id, s.Cashier, "dispatch", new(1, "QA denied")));
        await using (var db = scope.CreateDbContext(Guid.NewGuid()))
        {
            Assert.Empty(await db.StockTransfers.ToListAsync()); Assert.Empty(await db.StockTransferEvents.ToListAsync());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(db, s).ExecuteAsync(s.Tenant, s.Id, s.Owner, "dispatch", new(1, "QA tenant denied")));
        }
        await Check(scope, s, 10, 0, 0, 0);
    }

    [Fact]
    public async Task ManagedDocumentsLinesAndHistoryCannotBeEditedOrDeletedEvenWithoutTrackedParent()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope); await Act(scope, s, "dispatch", 1);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            (await db.StockTransfers.SingleAsync()).Status = TransferStatus.Approved;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            (await db.StockTransferItems.SingleAsync()).Quantity = 999;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            using var authority = InventoryWorkflowMutationScope.Authorize();
            (await db.StockTransferEvents.SingleAsync()).Reason = "Forged";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(2, (await verify.StockTransferItems.SingleAsync()).Quantity); await Check(scope, s, 8, 0, 1, 1);
    }

    private sealed record Scenario(Guid Tenant, Guid Owner, Guid Cashier, Guid Source, Guid Target, Guid Product, Guid Variant, Guid Id, Guid Line, string Number);
    [Fact]
    public async Task InTransitFrozenValuation_PreservesInventoryValueAcrossDispatchReceiptAndCancellation()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        async Task<InventoryValuationDto> Report()
        {
            await using var db = scope.CreateDbContext(s.Tenant);
            var response = await new ReportsController(db, new TestTenantContext(s.Tenant), NullLogger<ReportsController>.Instance, null!, null!)
                .GetInventoryValuation(default);
            return Assert.IsType<InventoryValuationDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        }
        Assert.Equal(50, (await Report()).TotalInventoryValue);
        await Act(scope, s, "dispatch", 1);
        var inTransit = await Report();
        Assert.Equal(40, inTransit.WarehouseInventoryValue); Assert.Equal(10, inTransit.InTransitValue);
        Assert.Equal(50, inTransit.TotalInventoryValue); Assert.Equal(2, inTransit.InTransitQuantity);
        Assert.Equal(1, inTransit.InTransitTransferCount); Assert.Equal(0, inTransit.UnvaluedInTransitLines);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            Assert.Equal(5, (await db.StockTransferItems.SingleAsync()).DispatchedUnitCost);
            (await db.ProductVariants.SingleAsync()).CostPrice = 7; await db.SaveChangesAsync();
        }
        var repriced = await Report(); Assert.Equal(56, repriced.WarehouseInventoryValue); Assert.Equal(10, repriced.InTransitValue);
        await Act(scope, s, "receive", 2);
        var received = await Report(); Assert.Equal(70, received.WarehouseInventoryValue); Assert.Equal(0, received.InTransitValue);
        await Act(scope, s, "reverse", 3);
        await Check(scope, s, 10, 0, 4, 3);
        Assert.Equal(70, (await Report()).TotalInventoryValue);
    }
    [Fact]
    public async Task ExistingImmediateCompletedTransfer_CanBeCanonicallyReversedWithoutRewritingItsNumberOrOriginalMovements()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        Guid[] originals;
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await new InventoryService(db, new TestTenantContext(s.Tenant), NullLogger<InventoryService>.Instance)
                .TransferStockAsync(s.Source, s.Target, s.Product, 2, "QA retained immediate transfer", s.Variant);
            var transfer = await db.StockTransfers.SingleAsync(); transfer.Status = TransferStatus.Completed; transfer.CompletedById = s.Owner; transfer.CompletedAt = DateTime.UtcNow;
            foreach (var movement in db.StockMovements.Local) { movement.CreatedBy = s.Owner; movement.ReferenceId = s.Id; movement.ReferenceNumber = s.Number; movement.ReferenceType = nameof(StockTransfer); }
            await db.SaveChangesAsync(); await tx.CommitAsync();
            originals = await db.StockMovements.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        }
        var result = await Act(scope, s, "reverse", 1); Assert.Equal(s.Number, result.TransferNumber);
        await Check(scope, s, 10, 0, 4, 1);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(originals, await verify.StockMovements.Where(x => originals.Contains(x.Id)).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
    }

    [Fact]
    public async Task InvalidLegacyPendingDocumentMayBeCancelledWithoutInventingStockMovements()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, TransferStatus.Pending);
        await using (var db = scope.CreateDbContext(s.Tenant)) { (await db.StockTransferItems.SingleAsync()).ProductVariantId = null; await db.SaveChangesAsync(); }
        Assert.Equal(TransferStatus.Cancelled, (await Act(scope, s, "cancel", 1)).Status);
        await Check(scope, s, 10, 0, 0, 1);
    }
    private static async Task<Scenario> Seed(TestDatabaseScope scope, TransferStatus status = TransferStatus.Approved)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "QA-ERP-R10-20261113-TRANSFER");
        await using var db = scope.CreateDbContext(s.Tenant);
        db.Tenants.Add(new Tenant { Id = s.Tenant, Name = s.Number, Currency = "EGP", Status = TenantStatus.Active, IsActive = true });
        foreach (var (id, role) in new[] { (s.Owner, UserRole.Owner), (s.Cashier, UserRole.Cashier) }) db.Users.Add(new User
        { Id = id, TenantId = s.Tenant, Username = $"qa-{id:N}", Email = $"{id:N}@example.invalid", PasswordHash = "not-a-login", IsActive = true, Role = role });
        db.Warehouses.AddRange(new Warehouse { Id = s.Source, TenantId = s.Tenant, Code = "QA-SOURCE", Name = "QA source", IsActive = true },
            new Warehouse { Id = s.Target, TenantId = s.Tenant, Code = "QA-TARGET", Name = "QA target", IsActive = true });
        var category = new Category { Id = Guid.NewGuid(), TenantId = s.Tenant, Name = "QA transfer" }; db.Categories.Add(category);
        db.Products.Add(new Product { Id = s.Product, TenantId = s.Tenant, Name = "QA transfer product", CategoryId = category.Id, IsActive = true, TrackStock = true });
        db.ProductVariants.Add(new ProductVariant { Id = s.Variant, TenantId = s.Tenant, ProductId = s.Product, SKU = "QA-TRANSFER", CostPrice = 5, Price = 10, IsActive = true, IsDefault = true });
        db.ProductVariantStocks.Add(new ProductVariantStock { Id = Guid.NewGuid(), TenantId = s.Tenant, VariantId = s.Variant, WarehouseId = s.Source, Quantity = 10 });
        db.StockTransfers.Add(new StockTransfer { Id = s.Id, TenantId = s.Tenant, TransferNumber = s.Number, TransferDate = DateTime.UtcNow,
            FromWarehouseId = s.Source, ToWarehouseId = s.Target, Status = status, RequestedById = s.Owner,
            ApprovedById = status == TransferStatus.Approved ? s.Owner : null, ApprovedAt = status == TransferStatus.Approved ? DateTime.UtcNow : null,
            Items = [new StockTransferItem { Id = s.Line, StockTransferId = s.Id, ProductId = s.Product, ProductVariantId = s.Variant, Quantity = 2 }] });
        await db.SaveChangesAsync(); return s;
    }
    private static StockTransferWorkflowService Service(ApplicationDbContext db, Scenario s) => new(db, new InventoryService(db, new TestTenantContext(s.Tenant), NullLogger<InventoryService>.Instance));
    private static async Task<StockTransfer> Act(TestDatabaseScope scope, Scenario s, string action, long version)
    {
        await using var db = scope.CreateDbContext(s.Tenant);
        return await Service(db, s).ExecuteAsync(s.Tenant, s.Id, s.Owner, action, new(version, "QA-ERP-R10-20261113-TRANSFER-" + action));
    }
    private static async Task Check(TestDatabaseScope scope, Scenario s, decimal source, decimal target, int movements, int events)
    {
        await using var db = scope.CreateDbContext(s.Tenant);
        Assert.Equal(source, (await db.ProductVariantStocks.SingleAsync(x => x.WarehouseId == s.Source)).Quantity);
        Assert.Equal(target, await db.ProductVariantStocks.Where(x => x.WarehouseId == s.Target).SumAsync(x => x.Quantity));
        Assert.Equal(movements, await db.StockMovements.CountAsync()); Assert.Equal(events, await db.StockTransferEvents.CountAsync());
        var transfer = await db.StockTransfers.Include(x => x.Items).SingleAsync();
        var inTransit = transfer.Status == TransferStatus.Dispatched ? transfer.Items.Sum(x => x.Quantity) : 0;
        Assert.Equal(10, source + target + inTransit);
        Assert.All(await db.ProductVariantStocks.ToListAsync(), x => Assert.Equal(0, x.DamagedQuantity));
    }
}
