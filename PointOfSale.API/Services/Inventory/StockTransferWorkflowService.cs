using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services.Inventory;

public sealed record TransferWorkflowAction(long Version, string Reason);

/// <summary>Full-quantity dispatch/receipt. Dispatched document quantities are the in-transit authority.</summary>
public sealed class StockTransferWorkflowService(ApplicationDbContext db, IInventoryService inventory)
{
    public async Task<StockTransfer> ExecuteAsync(Guid tenantId, Guid id, Guid actor, string action, TransferWorkflowAction request, CancellationToken ct = default)
    {
        if (db.ChangeTracker.HasChanges()) throw new InventoryWorkflowException("TRANSFER_PENDING_STATE", "The workflow cannot flush unrelated caller changes.");
        if (action is not ("dispatch" or "receive" or "cancel" or "reverse")) throw new InventoryWorkflowException("TRANSFER_ACTION", "Unknown transfer action.");
        var reason = InventoryWorkflowAuthority.Reason(request.Reason);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await InventoryWorkflowAuthority.RequireActorAsync(db, tenantId, actor,
            action == "reverse" ? PosPermissions.InventoryApprove : PosPermissions.InventoryTransfer, false, ct);
        var transfer = await db.StockTransfers.FromSqlInterpolated($"SELECT * FROM \"StockTransfers\" WHERE \"TenantId\"={tenantId} AND \"Id\"={id} AND NOT \"IsDeleted\" FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new InventoryWorkflowException("TRANSFER_NOT_FOUND", "Transfer not found.");
        await db.Entry(transfer).ReloadAsync(ct);
        await db.Entry(transfer).Collection(x => x.Items).LoadAsync(ct);
        foreach (var item in transfer.Items) await db.Entry(item).ReloadAsync(ct);
        // Identical response-loss replay only; a later/different workflow cannot be mistaken for success.
        if (transfer.WorkflowVersion == request.Version + 1)
        {
            var previous = await db.StockTransferEvents.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.StockTransferId == id && x.Version == transfer.WorkflowVersion, ct);
            if (previous?.Action == action && previous.Reason == reason) return transfer;
        }
        if (request.Version < 1 || transfer.WorkflowVersion != request.Version || !Permits(transfer.Status, action))
            throw new InventoryWorkflowException("TRANSFER_CONFLICT", "The transfer version or status changed. Reload before confirming.");
        var hasStockEffect = action != "cancel" || transfer.Status == TransferStatus.Dispatched;
        var dispatchCosts = new Dictionary<Guid, decimal>();
        if (hasStockEffect && (transfer.FromWarehouseId == transfer.ToWarehouseId || transfer.Items.Count is < 1 or > 500 ||
            transfer.Items.Any(x => x.IsDeleted || !x.ProductVariantId.HasValue || x.Quantity <= 0 || !InventoryWorkflowAuthority.Quantity(x.Quantity)) ||
            transfer.Items.Select(x => x.ProductVariantId).Distinct().Count() != transfer.Items.Count))
            throw new InventoryWorkflowException("TRANSFER_LINES", "The transfer requires distinct valid variant lines and different warehouses.");
        if (hasStockEffect)
        {
            var variantIds = transfer.Items.Select(x => x.ProductVariantId!.Value).ToArray();
            var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.Id) && x.IsActive && !x.IsDeleted &&
                x.Product.TenantId == tenantId && x.Product.IsActive && !x.Product.IsDeleted && x.Product.TrackStock).Select(x => new { x.Id, x.ProductId, x.CostPrice }).ToListAsync(ct);
            if (variants.Count != transfer.Items.Count || transfer.Items.Any(i => !variants.Any(v => v.Id == i.ProductVariantId && v.ProductId == i.ProductId)))
                throw new InventoryWorkflowException("TRANSFER_VARIANTS", "A transfer line no longer references active same-tenant inventory. No stock movements were applied.");
            if (action == "dispatch")
            {
                if (variants.Any(x => !InventoryWorkflowAuthority.Quantity(x.CostPrice)))
                    throw new InventoryWorkflowException("TRANSFER_COST", "Transfer dispatch requires a valid nonnegative unit-cost snapshot.");
                dispatchCosts = variants.ToDictionary(x => x.Id, x => x.CostPrice);
            }
        }
        if (hasStockEffect && await db.Warehouses.CountAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive &&
            (x.Id == transfer.FromWarehouseId || x.Id == transfer.ToWarehouseId), ct) != 2)
            throw new InventoryWorkflowException("TRANSFER_WAREHOUSE", "Both transfer warehouses must be active in this tenant.");
        var targets = new List<StockLockTarget>();
        foreach (var item in transfer.Items)
        {
            if (action is "dispatch" or "reverse") targets.Add(new(transfer.FromWarehouseId, item.ProductVariantId!.Value, action == "reverse"));
            if (action is "receive" or "reverse") targets.Add(new(transfer.ToWarehouseId, item.ProductVariantId!.Value, action == "receive"));
            if (action == "cancel" && hasStockEffect) targets.Add(new(transfer.FromWarehouseId, item.ProductVariantId!.Value, false));
        }
        var stocks = await ProductVariantStockMutationSupport.LockMultipleStocksAsync(db, tenantId, targets, ct);
        foreach (var target in targets)
        {
            if (!stocks.TryGetValue((target.WarehouseId, target.VariantId), out var stock))
                throw new InventoryWorkflowException("TRANSFER_STOCK_MISSING", "A required transfer stock row is unavailable. No movements were applied.");
            var quantity = transfer.Items.Single(x => x.ProductVariantId == target.VariantId).Quantity;
            if (action == "dispatch" || (action == "reverse" && target.WarehouseId == transfer.ToWarehouseId))
                ProductVariantStockMutationSupport.ValidateQuantityChange(stock, -quantity, false, "Transfer " + action);
        }
        using var authority = InventoryWorkflowMutationScope.Authorize();
        var changes = new List<PendingProductVariantStockChange>();
        var movements = new List<StockMovement>();
        foreach (var item in transfer.Items.OrderBy(x => x.ProductVariantId))
        {
            if (action == "dispatch") item.DispatchedUnitCost = dispatchCosts[item.ProductVariantId!.Value];
            async Task Move(Guid warehouse, decimal quantity, StockMovementType type)
            {
                var movement = await inventory.AdjustStockAsync(warehouse, item.ProductId, quantity, type, reason, item.ProductVariantId, id, transfer.TransferNumber);
                movement.ReferenceType = action == "reverse" || action == "cancel" ? "StockTransferCompensation" : nameof(StockTransfer);
                movement.ReferenceLineId = item.Id; movement.CreatedBy = actor; movement.Source = nameof(StockTransferWorkflowService);
                movement.Price = item.DispatchedUnitCost ?? 0;
                movement.ToWarehouseId = warehouse == transfer.FromWarehouseId ? transfer.ToWarehouseId : transfer.FromWarehouseId;
                movements.Add(movement);
                changes.Add(ProductVariantStockMutationSupport.BuildPendingStockChange(item.ProductVariantId!.Value, item.ProductId, warehouse,
                    movement.QuantityAfter - quantity, movement.QuantityAfter, reason, id, movement.ReferenceType));
            }
            if (action == "dispatch") await Move(transfer.FromWarehouseId, -item.Quantity, StockMovementType.TransferOut);
            if (action == "receive") await Move(transfer.ToWarehouseId, item.Quantity, StockMovementType.TransferIn);
            if (action == "cancel" && hasStockEffect) await Move(transfer.FromWarehouseId, item.Quantity, StockMovementType.TransferIn);
            if (action == "reverse")
            {
                await Move(transfer.ToWarehouseId, -item.Quantity, StockMovementType.TransferOut);
                await Move(transfer.FromWarehouseId, item.Quantity, StockMovementType.TransferIn);
            }
        }
        var now = DateTime.UtcNow;
        if (action == "dispatch") { transfer.Status = TransferStatus.Dispatched; transfer.DispatchedById = actor; transfer.DispatchedAt = now; }
        if (action == "receive") { transfer.Status = TransferStatus.Completed; transfer.CompletedById = actor; transfer.CompletedAt = now; }
        if (action == "cancel") { transfer.Status = TransferStatus.Cancelled; transfer.CancellationReason = reason; }
        if (action == "reverse") { transfer.Status = TransferStatus.Reversed; transfer.ReversedById = actor; transfer.ReversedAt = now; transfer.ReversalReason = reason; }
        transfer.WorkflowVersion++; transfer.UpdatedBy = actor; transfer.UpdatedAt = now;
        db.StockTransferEvents.Add(new StockTransferEvent
        {
            Id = Guid.NewGuid(), TenantId = tenantId, StockTransferId = id, Version = transfer.WorkflowVersion, Action = action,
            Reason = reason, Status = transfer.Status, CreatedBy = actor, CreatedAt = now,
            DetailsJson = JsonSerializer.Serialize(movements.Select(x => new { x.Id, x.ReferenceLineId, x.WarehouseId, x.ProductVariantId, x.QuantityChange, x.QuantityAfter }))
        });
        await db.SaveChangesAsync(ct);
        await ProductVariantStockMutationSupport.PublishCommittedStockEventsAsync(db, tenantId, changes, ct);
        await tx.CommitAsync(ct);
        return transfer;
    }

    private static bool Permits(TransferStatus status, string action) => action switch
    {
        "dispatch" => status == TransferStatus.Approved,
        "receive" => status == TransferStatus.Dispatched,
        "cancel" => status is TransferStatus.Pending or TransferStatus.Approved or TransferStatus.Dispatched,
        "reverse" => status == TransferStatus.Completed,
        _ => false
    };
}
