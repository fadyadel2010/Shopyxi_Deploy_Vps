using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using System.Data;

namespace PointOfSale.Infrastructure.Inventory;

/// <summary>
/// Current-cost operational valuation, not a GL valuation. Reserved stock is
/// owned; damaged stock is a separate physical bucket. Dispatch removes origin
/// stock, so only Dispatched transfers are added, exactly once, until receipt.
/// </summary>
public static class InventoryReportingReader
{
    public static IQueryable<ProductVariantStock> EligiblePositions(ApplicationDbContext db, Guid tenantId) =>
        db.ProductVariantStocks.AsNoTracking()
            .Where(s => s.TenantId == tenantId && !s.IsDeleted)
            .Where(s => s.Warehouse.TenantId == tenantId && s.Warehouse.IsActive && !s.Warehouse.IsDeleted)
            .Where(s => s.Variant.TenantId == tenantId && s.Variant.IsActive && !s.Variant.IsDeleted)
            .Where(s => s.Variant.SemanticLifecycleState == ProductVariantSemanticLifecycleState.Current &&
                        s.Variant.SemanticSupersededAtUtc == null && s.Variant.SemanticReplacementVariantId == null)
            .Where(s => s.Variant.Product.TenantId == tenantId && s.Variant.Product.IsActive &&
                        !s.Variant.Product.IsDeleted && s.Variant.Product.TrackStock);

    public static IQueryable<ProductVariantStock> LowStockPositions(ApplicationDbContext db, Guid tenantId, Guid? warehouseId = null)
    {
        var query = EligiblePositions(db, tenantId)
            // Zero is a configured zero. Never substitute MinimumStockLevel or 10.
            .Where(s => s.Quantity - s.ReservedQuantity <= s.Variant.Product.LowStockThreshold);
        return warehouseId.HasValue ? query.Where(s => s.WarehouseId == warehouseId.Value) : query;
    }

    public static IQueryable<StockTransferItem> InTransitLines(ApplicationDbContext db, Guid tenantId) =>
        db.StockTransfers.AsNoTracking()
            .Where(t => t.TenantId == tenantId && !t.IsDeleted && t.Status == TransferStatus.Dispatched)
            .SelectMany(t => t.Items.Where(i => !i.IsDeleted));

    public static async Task<InventoryValuationSnapshot> ReadValuationAsync(
        ApplicationDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        await PointOfSale.Infrastructure.Accounting.FinancialReportingCurrency.RequireTenantAsync(db, tenantId, ct);
        // Dispatch/receipt changes stock and transfer state atomically. Read both
        // buckets in one snapshot to avoid counting across different commits.
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var onHand = await EligiblePositions(db, tenantId)
            .SumAsync(s => (decimal?)((s.Quantity + s.DamagedQuantity) * s.Variant.CostPrice), ct) ?? 0m;
        var lines = InTransitLines(db, tenantId);
        var missing = await lines.CountAsync(i => i.DispatchedUnitCost == null, ct);
        var knownTransit = await lines.SumAsync(i => (decimal?)(i.Quantity * (i.DispatchedUnitCost ?? 0m)), ct) ?? 0m;
        var quantity = await lines.SumAsync(i => (decimal?)i.Quantity, ct) ?? 0m;
        if (transaction != null) await transaction.CommitAsync(ct);
        return new(onHand, missing == 0 ? knownTransit : null, quantity, missing);
    }
}

public sealed record InventoryValuationSnapshot(
    decimal WarehouseValue, decimal? InTransitValue, decimal InTransitQuantity, int UnvaluedInTransitLines)
{
    public bool IsComplete => UnvaluedInTransitLines == 0;
    public decimal? TotalOwnedValue => IsComplete ? WarehouseValue + InTransitValue : null;
}
