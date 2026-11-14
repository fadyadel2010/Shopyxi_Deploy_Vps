using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.API.DTOs;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Repositories;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Integration;

public sealed class StockTransfersConsistencyIntegrationTests
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task SequenceMigration_IncludesDeletedLegacyNumbers_AndNeverRewindsOnReupgrade()
    {
        using var scope = TestDatabaseScope.Open();
        var tenant = new TestTenantContext(Guid.NewGuid());
        await using var db = scope.CreateDbContext(tenant);
        var scenario = await SeedScenarioAsync(db, tenant.RequiredTenantId);
        var id = await SeedPendingTransferAsync(db, tenant.RequiredTenantId, scenario);
        var legacy = await db.StockTransfers.SingleAsync(x => x.Id == id);
        legacy.TransferNumber = "TRF-20261101-5000";
        legacy.IsDeleted = true;
        await db.SaveChangesAsync();
        // Replay this additive allocator upgrade without down-migrating any later
        // retained accounting-period history. All original uniqueness assertions remain.
        var migration = new PointOfSale.Infrastructure.Migrations.StockTransferNumberSequenceAuthority { ActiveProvider = db.Database.ProviderName! };
        var sql = Assert.Single(migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>()).Sql;
        // Execute the exact reviewed migration SQL, not EF's composite-format
        // ExecuteSqlRaw overload (the regex's {8} is literal SQL syntax).
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        Assert.Equal(5001L, await db.Database.SqlQueryRaw<long>("SELECT nextval('public.stock_transfer_number_seq') AS \"Value\"").SingleAsync());
        await command.ExecuteNonQueryAsync();
        Assert.Equal(5002L, await db.Database.SqlQueryRaw<long>("SELECT nextval('public.stock_transfer_number_seq') AS \"Value\"").SingleAsync());
        Assert.Equal("TRF-20261101-5000", (await db.StockTransfers.IgnoreQueryFilters().SingleAsync(x => x.Id == id)).TransferNumber);
    }

    [Fact]
    public async Task ApprovedTransferAndCompensatingReturn_PreserveMovementsAndRestoreBothWarehouses()
    {
        using var scope = TestDatabaseScope.Open();
        var tenantId = Guid.NewGuid();
        var tenant = new TestTenantContext(tenantId);
        await using var seed = scope.CreateDbContext(tenant);
        var scenario = await SeedScenarioAsync(seed, tenantId);
        async Task<Guid> Move(Guid from, Guid to, string reference)
        {
            await using var db = scope.CreateDbContext(tenant);
            var controller = CreateController(db, tenant, scenario.UserId);
            var request = BuildCreateTransferRequest(scenario, reference);
            request.FromWarehouseId = from; request.ToWarehouseId = to;
            var response = ReadCreateTransferResponse(await controller.CreateTransfer(request));
            Assert.IsType<OkObjectResult>(await controller.ApproveTransfer(response.TransferId, new ApprovalRequest { Notes = reference }));
            Assert.IsType<OkObjectResult>(await controller.CompleteTransfer(response.TransferId));
            await using var replay = scope.CreateDbContext(tenant);
            Assert.IsType<BadRequestObjectResult>(await CreateController(replay, tenant, scenario.UserId).CompleteTransfer(response.TransferId));
            return response.TransferId;
        }
        var forward = await Move(scenario.SourceWarehouseId, scenario.TargetWarehouseId, "QA-ERP-R9I-FIN-2026-TRANSFER");
        await using (var verify = scope.CreateDbContext(tenant))
        {
            Assert.Equal(8m, (await verify.ProductVariantStocks.SingleAsync(x => x.WarehouseId == scenario.SourceWarehouseId)).Quantity);
            Assert.Equal(2m, (await verify.ProductVariantStocks.SingleAsync(x => x.WarehouseId == scenario.TargetWarehouseId)).Quantity);
            var movements = await verify.StockMovements.ToListAsync(); Assert.Equal(2, movements.Count);
            Assert.All(movements, x => { Assert.Equal(forward, x.ReferenceId); Assert.Equal(scenario.UserId, x.CreatedBy); Assert.Equal(nameof(StockTransfer), x.ReferenceType); });
            Assert.Equal(0m, movements.Sum(x => x.QuantityChange));
        }
        var compensation = await Move(scenario.TargetWarehouseId, scenario.SourceWarehouseId, "QA-ERP-R9I-FIN-2026-TRANSFER-COMPENSATION");
        await using var final = scope.CreateDbContext(tenant);
        Assert.Equal(10m, (await final.ProductVariantStocks.SingleAsync(x => x.WarehouseId == scenario.SourceWarehouseId)).Quantity);
        Assert.Equal(0m, (await final.ProductVariantStocks.SingleAsync(x => x.WarehouseId == scenario.TargetWarehouseId)).Quantity);
        Assert.Equal(2, await final.StockTransfers.CountAsync()); Assert.Equal(4, await final.StockMovements.CountAsync());
        Assert.All(await final.ProductVariantStocks.ToListAsync(), x => { Assert.Equal(0m, x.ReservedQuantity); Assert.Equal(0m, x.DamagedQuantity); });
        var evidence = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(evidence))
            await File.WriteAllTextAsync(Path.Combine(evidence, "warehouse-transfer-reconciliation.json"), JsonSerializer.Serialize(new { isolatedQa = true, forward, compensation, sourceFinal = 10m, destinationFinal = 0m, retainedTransfers = 2, retainedMovements = 4, netDelta = 0m, explicitReversalState = false, actualStateMachine = "Pending -> Approved -> Completed; separate compensating return transfer" }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task AdjustmentRequiresReason_PreservesActor_ReplaysOnce_AndCompensatesWithoutDeletingHistory()
    {
        using var scope = TestDatabaseScope.Open();
        var tenant = new TestTenantContext(Guid.NewGuid());
        await using var seed = scope.CreateDbContext(tenant);
        var scenario = await SeedScenarioAsync(seed, tenant.RequiredTenantId);
        async Task<IActionResult> Adjust(decimal change, string reason, string key, StockMovementType type = StockMovementType.Adjustment)
        {
            await using var db = scope.CreateDbContext(tenant);
            var controller = CreateWarehouseController(db, tenant, scenario.UserId);
            controller.Request.Headers["Idempotency-Key"] = key;
            return await controller.AdjustStock(new StockAdjustmentDto { WarehouseId = scenario.SourceWarehouseId, ProductId = scenario.ProductId, ProductVariantId = scenario.VariantId, QuantityChange = change, Reason = reason, Type = type });
        }
        Assert.IsType<BadRequestObjectResult>(await Adjust(2m, " ", "QA-ADJ-EMPTY"));
        Assert.IsType<BadRequestObjectResult>(await Adjust(2m, "Cannot impersonate transfer", "QA-ADJ-TYPE", StockMovementType.TransferIn));
        Assert.IsType<BadRequestObjectResult>(await Adjust(0m, "No-op", "QA-ADJ-ZERO"));
        Assert.IsType<OkObjectResult>(await Adjust(2m, "QA-ERP-R9I-FIN-2026-COUNT-CORRECTION", "QA-ADJ-POST"));
        Assert.IsAssignableFrom<ObjectResult>(await Adjust(2m, "QA-ERP-R9I-FIN-2026-COUNT-CORRECTION", "QA-ADJ-POST"));
        Assert.IsType<ConflictObjectResult>(await Adjust(3m, "QA-ERP-R9I-FIN-2026-COUNT-CORRECTION", "QA-ADJ-POST"));
        await using (var nonzero = scope.CreateDbContext(tenant))
        {
            Assert.Equal(12m, (await nonzero.ProductVariantStocks.SingleAsync()).Quantity);
            Assert.Single(await nonzero.StockMovements.ToListAsync());
        }
        Assert.IsType<OkObjectResult>(await Adjust(-2m, "QA-ERP-R9I-FIN-2026-COMPENSATES-QA-ADJ-POST", "QA-ADJ-CORRECT"));
        await using var final = scope.CreateDbContext(tenant);
        Assert.Equal(10m, (await final.ProductVariantStocks.SingleAsync()).Quantity);
        var movements = await final.StockMovements.ToListAsync(); Assert.Equal(2, movements.Count);
        Assert.All(movements, x => { Assert.Equal(scenario.UserId, x.CreatedBy); Assert.Equal(x.Id, x.ReferenceId); Assert.Equal("StockAdjustment", x.ReferenceType); Assert.False(string.IsNullOrWhiteSpace(x.Notes)); });
        Assert.Equal(0m, movements.Sum(x => x.QuantityChange));
        var evidence = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(evidence))
            await File.WriteAllTextAsync(Path.Combine(evidence, "stock-adjustment-reconciliation.json"), JsonSerializer.Serialize(new
            {
                isolatedQa = true, tenantId = tenant.RequiredTenantId, actorId = scenario.UserId,
                warehouseId = scenario.SourceWarehouseId, variantId = scenario.VariantId,
                fixtureOpeningQuantity = 10m, intermediateQuantity = 12m, finalQuantity = 10m,
                movementIds = movements.Select(x => x.Id), retainedMovements = movements.Count,
                netDelta = movements.Sum(x => x.QuantityChange), changedPayloadConflict = true,
                compensation = "Separate -2 adjustment references original operation; no deletion"
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Theory]
    [InlineData(false, "inactive")]
    [InlineData(true, "inactive")]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    public async Task InventoryMutation_RequiresCurrentSameTenantActiveActor(bool directTransfer, string defect)
    {
        using var scope = TestDatabaseScope.Open();
        var tenant = new TestTenantContext(Guid.NewGuid());
        await using var seed = scope.CreateDbContext(tenant);
        var scenario = await SeedScenarioAsync(seed, tenant.RequiredTenantId);
        if (defect == "inactive")
        {
            (await seed.Users.SingleAsync()).IsActive = false;
            await seed.SaveChangesAsync();
        }
        await using var db = scope.CreateDbContext(tenant);
        var controller = CreateWarehouseController(db, tenant, defect == "unknown" ? Guid.NewGuid() : scenario.UserId);
        if (defect == "missing") controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        controller.Request.Headers["Idempotency-Key"] = "QA-ERP-R10-INVENTORY-ACTOR-DENIAL";
        var result = directTransfer
            ? await controller.TransferStock(new StockTransferDto { SourceWarehouseId = scenario.SourceWarehouseId, TargetWarehouseId = scenario.TargetWarehouseId, ProductId = scenario.ProductId, ProductVariantId = scenario.VariantId, Quantity = 1m, Notes = "Denied actor" })
            : await controller.AdjustStock(new StockAdjustmentDto { WarehouseId = scenario.SourceWarehouseId, ProductId = scenario.ProductId, ProductVariantId = scenario.VariantId, QuantityChange = 1m, Type = StockMovementType.Adjustment, Reason = "Denied actor" });
        if (defect == "missing") Assert.IsType<UnauthorizedObjectResult>(result);
        else Assert.IsType<ForbidResult>(result);
        await using var verify = scope.CreateDbContext(tenant);
        Assert.Empty(await verify.StockMovements.ToListAsync());
        Assert.Empty(await verify.StockTransferRequestIdempotencyRecords.ToListAsync());
        Assert.Equal(10m, (await verify.ProductVariantStocks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task CreateTransfer_ReplayingSameClientRequestId_ReturnsExistingTransfer()
    {
        if (!RealPostgresTestSupport.TryCreateScope(out var databaseScope, out var tenantId, out var tenantContext) ||
            databaseScope is null ||
            tenantContext is null)
        {
            return;
        }

        using var scope = databaseScope;
        await using var seedContext = scope.CreateDbContext(tenantContext);
        var seeded = await SeedScenarioAsync(seedContext, tenantId);

        var request = BuildCreateTransferRequest(seeded, "stock-transfer-create-001");

        await using var actionContext = scope.CreateDbContext(tenantContext);
        var controller = CreateController(actionContext, tenantContext, seeded.UserId);
        var firstResult = await controller.CreateTransfer(request);
        var firstResponse = ReadCreateTransferResponse(firstResult);

        await using var replayContext = scope.CreateDbContext(tenantContext);
        var replayController = CreateController(replayContext, tenantContext, seeded.UserId);
        var replayResult = await replayController.CreateTransfer(BuildCreateTransferRequest(seeded, "stock-transfer-create-001"));
        var replayResponse = ReadCreateTransferResponse(replayResult);

        Assert.Equal(firstResponse.TransferId, replayResponse.TransferId);
        Assert.Equal(firstResponse.TransferNumber, replayResponse.TransferNumber);

        await using var verifyContext = scope.CreateDbContext(tenantContext);
        Assert.Equal(1, await verifyContext.StockTransfers.CountAsync());
        Assert.Equal(1, await verifyContext.StockTransferItems.CountAsync());
        Assert.Equal(1, await verifyContext.StockTransferRequestIdempotencyRecords.CountAsync());
    }

    [Fact]
    public async Task RejectTransfer_WhenCommitFails_RollsBackPendingStatusMutation()
    {
        if (!RealPostgresTestSupport.TryCreateScope(out var databaseScope, out var tenantId, out var tenantContext) ||
            databaseScope is null ||
            tenantContext is null)
        {
            return;
        }

        using var scope = databaseScope;
        await using var seedContext = scope.CreateDbContext(tenantContext);
        var seeded = await SeedScenarioAsync(seedContext, tenantId);
        var transferId = await SeedPendingTransferAsync(seedContext, tenantId, seeded);

        var connectionString = seedContext.Database.GetDbConnection().ConnectionString;
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .EnableDetailedErrors()
            .AddInterceptors(new CommitFailureInterceptor())
            .Options;

        await using var actionContext = new ApplicationDbContext(options, tenantContext);
        var controller = CreateController(actionContext, tenantContext, seeded.UserId);

        var result = await controller.RejectTransfer(transferId, new ApprovalRequest { Notes = "Reject with forced failure" });
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);

        await using var verifyContext = scope.CreateDbContext(tenantContext);
        var transfer = await verifyContext.StockTransfers.SingleAsync(entity => entity.Id == transferId);
        Assert.Equal(TransferStatus.Pending, transfer.Status);
        Assert.Null(transfer.ApprovedById);
        Assert.Null(transfer.ApprovedAt);
        Assert.Null(transfer.ApprovalNotes);
    }

    [Fact]
    public async Task ApproveAndRejectTransfer_WhenExecutedConcurrently_OnlyOneStateTransitionSucceeds()
    {
        if (!RealPostgresTestSupport.TryCreateScope(out var databaseScope, out var tenantId, out var tenantContext) ||
            databaseScope is null ||
            tenantContext is null)
        {
            return;
        }

        using var scope = databaseScope;
        await using var seedContext = scope.CreateDbContext(tenantContext);
        var seeded = await SeedScenarioAsync(seedContext, tenantId);
        var transferId = await SeedPendingTransferAsync(seedContext, tenantId, seeded);
        var connectionString = seedContext.Database.GetDbConnection().ConnectionString;

        var blockingSaveInterceptor = new BlockingSaveChangesInterceptor();
        var rejectLockProbe = new MatchingCommandInterceptor("FOR UPDATE");

        var approveOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .EnableDetailedErrors()
            .AddInterceptors(blockingSaveInterceptor)
            .Options;

        var rejectOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .EnableDetailedErrors()
            .AddInterceptors(rejectLockProbe)
            .Options;

        await using var approveContext = new ApplicationDbContext(approveOptions, tenantContext);
        await using var rejectContext = new ApplicationDbContext(rejectOptions, tenantContext);

        var approveController = CreateController(approveContext, tenantContext, seeded.UserId);
        var rejectController = CreateController(rejectContext, tenantContext, seeded.UserId);

        var approveTask = approveController.ApproveTransfer(
            transferId,
            new ApprovalRequest { Notes = "approve-wins" });

        await blockingSaveInterceptor.WaitUntilBlockedAsync();

        var rejectTask = rejectController.RejectTransfer(
            transferId,
            new ApprovalRequest { Notes = "reject-loses" });

        await rejectLockProbe.WaitForMatchAsync();
        await Task.Delay(150);
        Assert.False(rejectTask.IsCompleted);

        blockingSaveInterceptor.Release();

        var approveResult = await approveTask;
        var rejectResult = await rejectTask;

        Assert.IsType<OkObjectResult>(approveResult);

        var badRequest = Assert.IsType<BadRequestObjectResult>(rejectResult);
        Assert.Contains("not pending", JsonSerializer.Serialize(badRequest.Value), StringComparison.OrdinalIgnoreCase);

        await using var verifyContext = scope.CreateDbContext(tenantContext);
        var transfer = await verifyContext.StockTransfers.SingleAsync(entity => entity.Id == transferId);
        Assert.Equal(TransferStatus.Approved, transfer.Status);
        Assert.Equal(seeded.UserId, transfer.ApprovedById);
        Assert.Equal("approve-wins", transfer.ApprovalNotes);
    }

    [Fact]
    public async Task WarehouseTransfer_ReplayingSameRequestId_DoesNotDoubleApplyStock()
    {
        if (!RealPostgresTestSupport.TryCreateScope(out var databaseScope, out var tenantId, out var tenantContext) ||
            databaseScope is null ||
            tenantContext is null)
        {
            return;
        }

        using var scope = databaseScope;
        await using var seedContext = scope.CreateDbContext(tenantContext);
        var seeded = await SeedScenarioAsync(seedContext, tenantId);

        var dto = new StockTransferDto
        {
            SourceWarehouseId = seeded.SourceWarehouseId,
            TargetWarehouseId = seeded.TargetWarehouseId,
            ProductId = seeded.ProductId,
            ProductVariantId = seeded.VariantId,
            Quantity = 3m,
            Notes = "direct-transfer"
        };

        await using var actionContext = scope.CreateDbContext(tenantContext);
        var controller = CreateWarehouseController(actionContext, tenantContext, seeded.UserId);
        controller.Request.Headers["Idempotency-Key"] = "warehouse-transfer-001";

        var firstResult = await controller.TransferStock(dto);
        var firstObjectResult = Assert.IsType<OkObjectResult>(firstResult);
        var firstResponse = Assert.IsType<StockMovementDto>(firstObjectResult.Value);

        await using var replayContext = scope.CreateDbContext(tenantContext);
        var replayController = CreateWarehouseController(replayContext, tenantContext, seeded.UserId);
        replayController.Request.Headers["Idempotency-Key"] = "warehouse-transfer-001";

        var replayResult = await replayController.TransferStock(dto);
        var replayObjectResult = Assert.IsAssignableFrom<ObjectResult>(replayResult);
        Assert.Equal(StatusCodes.Status200OK, replayObjectResult.StatusCode ?? StatusCodes.Status200OK);

        await using var verifyContext = scope.CreateDbContext(tenantContext);
        var stocks = await verifyContext.ProductVariantStocks
            .Where(stock =>
                stock.TenantId == tenantId &&
                stock.VariantId == seeded.VariantId)
            .ToListAsync();

        Assert.Equal(2, stocks.Count);
        Assert.Equal(7m, stocks.Single(stock => stock.WarehouseId == seeded.SourceWarehouseId).Quantity);
        Assert.Equal(3m, stocks.Single(stock => stock.WarehouseId == seeded.TargetWarehouseId).Quantity);

        var movements = await verifyContext.StockMovements
            .Where(movement =>
                movement.ProductVariantId == seeded.VariantId &&
                (movement.MovementType == StockMovementType.TransferOut ||
                 movement.MovementType == StockMovementType.TransferIn))
            .ToListAsync();

        Assert.Equal(2, movements.Count);
        Assert.Equal(1, await verifyContext.StockTransferRequestIdempotencyRecords.CountAsync());
        Assert.Contains(
            await verifyContext.StockTransferRequestIdempotencyRecords.Select(record => record.Key).ToListAsync(),
            key => key.Contains("warehouse-transfer-001", StringComparison.Ordinal));
        Assert.Equal(3m, firstResponse.QuantityChange);
        Assert.All(movements, movement => Assert.Equal(seeded.UserId, movement.CreatedBy));

        await using var correction = scope.CreateDbContext(tenantContext);
        var correctionController = CreateWarehouseController(correction, tenantContext, seeded.UserId);
        correctionController.Request.Headers["Idempotency-Key"] = "QA-ERP-R9I-FIN-2026-DIRECT-TRANSFER-COMPENSATION";
        Assert.IsType<OkObjectResult>(await correctionController.TransferStock(new StockTransferDto
        {
            SourceWarehouseId = seeded.TargetWarehouseId, TargetWarehouseId = seeded.SourceWarehouseId,
            ProductId = seeded.ProductId, ProductVariantId = seeded.VariantId, Quantity = 3m,
            Notes = "Compensates warehouse-transfer-001 without deleting movement history"
        }));
        await using var final = scope.CreateDbContext(tenantContext);
        Assert.Equal(10m, (await final.ProductVariantStocks.SingleAsync(x => x.WarehouseId == seeded.SourceWarehouseId)).Quantity);
        Assert.Equal(0m, (await final.ProductVariantStocks.SingleAsync(x => x.WarehouseId == seeded.TargetWarehouseId)).Quantity);
        Assert.Equal(4, await final.StockMovements.CountAsync());
        var evidence = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(evidence))
            await File.WriteAllTextAsync(Path.Combine(evidence, "direct-transfer-reconciliation.json"), JsonSerializer.Serialize(new
            {
                isolatedQa = true, tenantId, actorId = seeded.UserId, variantId = seeded.VariantId,
                sourceWarehouse = seeded.SourceWarehouseId, destinationWarehouse = seeded.TargetWarehouseId,
                sourceNonzero = 7m, destinationNonzero = 3m, sourceFinal = 10m, destinationFinal = 0m,
                retainedMovements = 4, netDelta = 0m, replayDoubleApplied = false,
                movements = await final.StockMovements.Select(x => new { x.Id, x.CreatedBy, x.ReferenceId, x.ReferenceType, x.ReferenceNumber, x.QuantityChange }).ToListAsync()
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task CreateTransfer_WithDuplicateVariantLines_UsesAggregatedLockedAvailability()
    {
        if (!RealPostgresTestSupport.TryCreateScope(out var databaseScope, out var tenantId, out var tenantContext) ||
            databaseScope is null ||
            tenantContext is null)
        {
            return;
        }

        using var scope = databaseScope;
        await using var seedContext = scope.CreateDbContext(tenantContext);
        var seeded = await SeedScenarioAsync(seedContext, tenantId);
        // Establish a real reservation rather than an unexplained projection
        // balance, so the fixture can release it through the canonical service.
        var reservations = new ProductVariantStockRepository(seedContext, NullLogger<ProductVariantStockRepository>.Instance);
        Assert.True(await reservations.ReserveAsync(tenantId, seeded.VariantId,
            seeded.SourceWarehouseId, 2m, Guid.NewGuid()));

        var request = new CreateTransferRequest
        {
            ClientRequestId = "stock-transfer-duplicate-lines-001",
            FromWarehouseId = seeded.SourceWarehouseId,
            ToWarehouseId = seeded.TargetWarehouseId,
            Notes = "Duplicate line aggregation",
            Items =
            [
                new TransferItemRequest
                {
                    ProductId = seeded.ProductId,
                    ProductVariantId = seeded.VariantId,
                    Quantity = 5m,
                    Notes = "line-1"
                },
                new TransferItemRequest
                {
                    ProductId = seeded.ProductId,
                    ProductVariantId = seeded.VariantId,
                    Quantity = 4m,
                    Notes = "line-2"
                }
            ]
        };

        // Capture source stock state before the request
        await using var preActionContext = scope.CreateDbContext(tenantContext);
        var sourceStockSnapshot = await preActionContext.ProductVariantStocks
            .AsNoTracking()
            .SingleAsync(stock =>
                stock.TenantId == tenantId &&
                stock.WarehouseId == seeded.SourceWarehouseId &&
                stock.VariantId == seeded.VariantId);
        var snapshotId = sourceStockSnapshot.Id;
        var snapshotQuantity = sourceStockSnapshot.Quantity;
        var snapshotReservedQuantity = sourceStockSnapshot.ReservedQuantity;
        var snapshotDamagedQuantity = sourceStockSnapshot.DamagedQuantity;
        var snapshotUpdatedAt = sourceStockSnapshot.UpdatedAt;

        await using var actionContext = scope.CreateDbContext(tenantContext);
        var controller = CreateController(actionContext, tenantContext, seeded.UserId);
        var result = await controller.CreateTransfer(request);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode ?? StatusCodes.Status400BadRequest);
        Assert.Contains("Insufficient stock", JsonSerializer.Serialize(badRequest.Value), StringComparison.Ordinal);

        await using var verifyContext = scope.CreateDbContext(tenantContext);
        Assert.Equal(0, await verifyContext.StockTransfers.CountAsync());
        Assert.Equal(0, await verifyContext.StockTransferItems.CountAsync());
        Assert.Equal(0, await verifyContext.StockTransferRequestIdempotencyRecords.CountAsync());

        // Verify source stock remains unchanged after validation failure
        var sourceStockAfter = await verifyContext.ProductVariantStocks
            .AsNoTracking()
            .SingleAsync(stock => stock.Id == snapshotId);
        Assert.Equal(snapshotQuantity, sourceStockAfter.Quantity);
        Assert.Equal(snapshotReservedQuantity, sourceStockAfter.ReservedQuantity);
        Assert.Equal(snapshotDamagedQuantity, sourceStockAfter.DamagedQuantity);
        Assert.Equal(snapshotUpdatedAt, sourceStockAfter.UpdatedAt);
    }

    private static StockTransfersController CreateController(
        ApplicationDbContext context,
        TestTenantContext tenantContext,
        Guid userId)
    {
        return new StockTransfersController(
            context,
            tenantContext,
            new InventoryService(context, tenantContext, NullLogger<InventoryService>.Instance),
            Mock.Of<IEventPublisher>(),
            NullLogger<StockTransfersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = BuildHttpContext(userId)
            }
        };
    }

    private static WarehousesController CreateWarehouseController(
        ApplicationDbContext context,
        TestTenantContext tenantContext,
        Guid userId)
    {
        return new WarehousesController(
            context,
            tenantContext,
            new InventoryService(context, tenantContext, NullLogger<InventoryService>.Instance),
            Mock.Of<IEventPublisher>(),
            NullLogger<WarehousesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = BuildHttpContext(userId)
            }
        };
    }

    private static CreateTransferRequest BuildCreateTransferRequest(SeededTransferScenario seeded, string clientRequestId)
    {
        return new CreateTransferRequest
        {
            ClientRequestId = clientRequestId,
            FromWarehouseId = seeded.SourceWarehouseId,
            ToWarehouseId = seeded.TargetWarehouseId,
            Notes = "Integration transfer",
            Items =
            [
                new TransferItemRequest
                {
                    ProductId = seeded.ProductId,
                    ProductVariantId = seeded.VariantId,
                    Quantity = 2m,
                    Notes = "move-2"
                }
            ]
        };
    }

    private static CreateTransferResponse ReadCreateTransferResponse(ActionResult result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode ?? StatusCodes.Status200OK);

        if (objectResult.Value is CreateTransferResponse response)
        {
            return response;
        }

        if (objectResult.Value is JsonElement payload)
        {
            return JsonSerializer.Deserialize<CreateTransferResponse>(payload.GetRawText(), ResponseJsonOptions)
                   ?? throw new InvalidOperationException("Stored stock transfer response payload could not be deserialized.");
        }

        return JsonSerializer.Deserialize<CreateTransferResponse>(
                   JsonSerializer.Serialize(objectResult.Value),
                   ResponseJsonOptions)
               ?? throw new InvalidOperationException("Stock transfer response payload could not be deserialized.");
    }

    private static async Task<SeededTransferScenario> SeedScenarioAsync(ApplicationDbContext context, Guid tenantId)
    {
        var utcNow = DateTime.UtcNow;
        var categoryId = Guid.NewGuid();
        var sourceWarehouseId = Guid.NewGuid();
        var targetWarehouseId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var userId = Guid.Parse("40000000-0000-0000-0000-000000000001");

        context.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Stock Transfer Consistency Tenant",
            Currency = "EGP",
            IsActive = true,
            Status = TenantStatus.Active
        });

        context.Users.Add(new User
        {
            Id = userId,
            TenantId = tenantId,
            Username = "transfer-user",
            Email = "transfer-user@test.local",
            PasswordHash = "hashed",
            FirstName = "Transfer",
            LastName = "Tester",
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = utcNow
        });

        context.Categories.Add(new Category
        {
            Id = categoryId,
            TenantId = tenantId,
            Name = "Transfers",
            CreatedAt = utcNow
        });

        context.Warehouses.AddRange(
            new Warehouse
            {
                Id = sourceWarehouseId,
                TenantId = tenantId,
                Name = "Source Warehouse",
                Code = "WH-SRC",
                IsActive = true,
                CreatedAt = utcNow
            },
            new Warehouse
            {
                Id = targetWarehouseId,
                TenantId = tenantId,
                Name = "Target Warehouse",
                Code = "WH-TGT",
                IsActive = true,
                CreatedAt = utcNow
            });

        context.Products.Add(new Product
        {
            Id = productId,
            TenantId = tenantId,
            Name = "Transfer Product",
            CategoryId = categoryId,
            PurchasePrice = 5m,
            SalePrice = 10m,
            OnlinePrice = 10m,
            PriceIncludesTax = false,
            TaxRate = 0m,
            Unit = "Piece",
            IsActive = true,
            TrackStock = true,
            CreatedAt = utcNow
        });

        context.ProductVariants.Add(new ProductVariant
        {
            Id = variantId,
            TenantId = tenantId,
            ProductId = productId,
            SKU = "TRF-IT-001",
            Price = 10m,
            OnlinePrice = 10m,
            CostPrice = 5m,
            IsDefault = true,
            IsActive = true,
            CreatedAt = utcNow
        });

        context.ProductVariantStocks.Add(new ProductVariantStock
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WarehouseId = sourceWarehouseId,
            VariantId = variantId,
            Quantity = 10m,
            ReservedQuantity = 0m,
            DamagedQuantity = 0m,
            CreatedAt = utcNow
        });

        await context.SaveChangesAsync();

        return new SeededTransferScenario(
            SourceWarehouseId: sourceWarehouseId,
            TargetWarehouseId: targetWarehouseId,
            ProductId: productId,
            VariantId: variantId,
            UserId: userId);
    }

    private static async Task<Guid> SeedPendingTransferAsync(
        ApplicationDbContext context,
        Guid tenantId,
        SeededTransferScenario seeded)
    {
        var transferId = Guid.NewGuid();
        var utcNow = DateTime.UtcNow;

        var transfer = new StockTransfer
        {
            Id = transferId,
            TenantId = tenantId,
            TransferNumber = "TRF-TEST-0001",
            TransferDate = utcNow,
            FromWarehouseId = seeded.SourceWarehouseId,
            ToWarehouseId = seeded.TargetWarehouseId,
            Status = TransferStatus.Pending,
            RequestedById = seeded.UserId,
            CreatedAt = utcNow
        };

        transfer.Items.Add(new StockTransferItem
        {
            Id = Guid.NewGuid(),
            StockTransferId = transferId,
            ProductId = seeded.ProductId,
            ProductVariantId = seeded.VariantId,
            Quantity = 1m,
            CreatedAt = utcNow
        });

        context.StockTransfers.Add(transfer);
        await context.SaveChangesAsync();

        return transferId;
    }

    private static DefaultHttpContext BuildHttpContext(Guid userId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, $"tester-{userId:N}")
        ], "TestAuth");

        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
    }

    private sealed record SeededTransferScenario(
        Guid SourceWarehouseId,
        Guid TargetWarehouseId,
        Guid ProductId,
        Guid VariantId,
        Guid UserId);

    private sealed class CommitFailureInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<InterceptionResult>(
                new InvalidOperationException("Simulated commit failure."));
        }
    }

    private sealed class BlockingSaveChangesInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _saveEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitUntilBlockedAsync()
        {
            await _saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Release()
        {
            _releaseGate.TrySetResult();
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _saveEntered.TrySetResult();
            await _releaseGate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class MatchingCommandInterceptor : DbCommandInterceptor
    {
        private readonly string _pattern;
        private readonly TaskCompletionSource _matchSeen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MatchingCommandInterceptor(string pattern)
        {
            _pattern = pattern;
        }

        public async Task WaitForMatchAsync()
        {
            await _matchSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(_pattern, StringComparison.OrdinalIgnoreCase))
            {
                _matchSeen.TrySetResult();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
