using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services.Inventory;

public sealed record CreateStockCount(Guid OperationId, Guid WarehouseId, DateOnly CountDate, IReadOnlyList<Guid> VariantIds, string Reason);
public sealed record EnterStockCountLine(Guid LineId, decimal? CountedQuantity, string? Reason);
public sealed record SaveStockCount(long Version, IReadOnlyList<EnterStockCountLine> Lines);
public sealed record PostStockCount(long Version, Guid? VarianceAccountId, string Reason);
public sealed record StockCountAction(long Version, string Reason);

public sealed class StockCountService(ApplicationDbContext db, IInventoryService inventory, IFinancialPostingService posting, IAccountResolver accounts)
{
    public async Task<StockCountSession> CreateAsync(Guid tenantId, Guid actor, CreateStockCount request, CancellationToken ct = default)
    {
        CleanContext();
        var reason = InventoryWorkflowAuthority.Reason(request.Reason);
        if (request.OperationId == Guid.Empty || request.CountDate == default || request.VariantIds == null || request.VariantIds.Count is < 1 or > 500 ||
            request.VariantIds.Distinct().Count() != request.VariantIds.Count)
            throw new InventoryWorkflowException("STOCK_COUNT_INVALID", "Select a warehouse, count date and 1–500 distinct variants.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { request.WarehouseId, request.CountDate, variants = request.VariantIds.OrderBy(x => x), reason }))));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await InventoryWorkflowAuthority.RequireActorAsync(db, tenantId, actor, PosPermissions.InventoryAdjust, false, ct);
        // Serialize duplicate creates without ever interpreting a cross-tenant document.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({request.OperationId.ToString()}, 0))", ct);
        var existing = await db.StockCountSessions.Include(x => x.Lines).SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.OperationId, ct);
        if (existing != null)
        {
            if (existing.RequestFingerprint != fingerprint) throw new InventoryWorkflowException("STOCK_COUNT_KEY_REUSED", "This operation ID belongs to a different stock count request.");
            return existing;
        }
        if (await db.StockCountSessions.IgnoreQueryFilters().AnyAsync(x => x.Id == request.OperationId, ct))
            throw new InventoryWorkflowException("STOCK_COUNT_KEY_UNAVAILABLE", "This operation ID cannot be used. Generate a new operation ID.");
        if (!await db.Warehouses.AnyAsync(x => x.TenantId == tenantId && x.Id == request.WarehouseId && x.IsActive && !x.IsDeleted, ct))
            throw new InventoryWorkflowException("STOCK_COUNT_WAREHOUSE", "Select an active same-tenant warehouse.");
        var variants = await db.ProductVariants.Include(x => x.Product).Where(x => x.TenantId == tenantId && request.VariantIds.Contains(x.Id) &&
            x.IsActive && !x.IsDeleted && x.Product.TenantId == tenantId && x.Product.IsActive && !x.Product.IsDeleted && x.Product.TrackStock).ToListAsync(ct);
        if (variants.Count != request.VariantIds.Count || variants.Any(x => !InventoryWorkflowAuthority.Quantity(x.CostPrice)))
            throw new InventoryWorkflowException("STOCK_COUNT_VARIANTS", "Only active, tracked same-tenant product variants with valid costs can be counted.");
        var stocks = await ProductVariantStockMutationSupport.LockMultipleStocksAsync(db, tenantId,
            variants.Select(x => new StockLockTarget(request.WarehouseId, x.Id, true)), ct);
        var suffix = await db.Database.SqlQueryRaw<long>("SELECT nextval('public.stock_count_number_seq') AS \"Value\"").SingleAsync(ct);
        using var authority = InventoryWorkflowMutationScope.Authorize();
        var session = new StockCountSession
        {
            Id = request.OperationId, TenantId = tenantId, Number = $"SC-{DateTime.UtcNow:yyyyMMdd}-{suffix}", RequestFingerprint = fingerprint,
            WarehouseId = request.WarehouseId, CountDate = request.CountDate, SnapshotAtUtc = DateTime.UtcNow,
            CreatedBy = actor, CreatedAt = DateTime.UtcNow, Reason = reason
        };
        foreach (var variant in variants.OrderBy(x => x.Id))
        {
            var stock = stocks[(request.WarehouseId, variant.Id)];
            session.Lines.Add(new StockCountLine
            {
                Id = Guid.NewGuid(), TenantId = tenantId, StockCountSessionId = session.Id, ProductId = variant.ProductId, VariantId = variant.Id,
                ProductName = variant.Product.Name, Sku = variant.SKU ?? string.Empty, SystemQuantity = stock.Quantity,
                ReservedSnapshot = stock.ReservedQuantity, DamagedSnapshot = stock.DamagedQuantity,
                StockVersion = stock.ProjectionMutationVersion, UnitCost = variant.CostPrice, CreatedBy = actor, CreatedAt = session.SnapshotAtUtc
            });
        }
        db.StockCountSessions.Add(session);
        Event(session, actor, "Create", reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return session;
    }

    public async Task<StockCountSession> SaveAsync(Guid tenantId, Guid id, Guid actor, SaveStockCount request, CancellationToken ct = default)
    {
        CleanContext();
        if (request.Lines == null || request.Lines.Count is < 1 or > 500 || request.Lines.Select(x => x.LineId).Distinct().Count() != request.Lines.Count)
            throw new InventoryWorkflowException("STOCK_COUNT_LINES", "Supply distinct count lines.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await InventoryWorkflowAuthority.RequireActorAsync(db, tenantId, actor, PosPermissions.InventoryAdjust, false, ct);
        var session = await Lock(tenantId, id, ct);
        Version(session, request.Version, StockCountStatus.Draft);
        using var authority = InventoryWorkflowMutationScope.Authorize();
        foreach (var input in request.Lines)
        {
            var line = session.Lines.SingleOrDefault(x => x.Id == input.LineId)
                ?? throw new InventoryWorkflowException("STOCK_COUNT_LINE", "A count line does not belong to this session.");
            if (input.CountedQuantity.HasValue && !InventoryWorkflowAuthority.Quantity(input.CountedQuantity.Value))
                throw new InventoryWorkflowException("STOCK_COUNT_QUANTITY", "Counted quantities must be nonnegative, with at most four decimal places.");
            if (input.Reason?.Trim().Length > 1000) throw new InventoryWorkflowException("STOCK_COUNT_REASON", "Variance reasons cannot exceed 1000 characters.");
            line.CountedQuantity = input.CountedQuantity;
            line.VarianceReason = input.Reason?.Trim();
            line.CountedBy = actor; line.CountedAtUtc = DateTime.UtcNow; line.UpdatedBy = actor; line.UpdatedAt = DateTime.UtcNow;
        }
        session.CountedBy = actor; session.CountedAtUtc = DateTime.UtcNow;
        Advance(session, actor); Event(session, actor, "Count", "Saved physical count progress");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return session;
    }

    public async Task<StockCountSession> PostAsync(Guid tenantId, Guid id, Guid actor, PostStockCount request, CancellationToken ct = default)
    {
        CleanContext();
        var reason = InventoryWorkflowAuthority.Reason(request.Reason);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await InventoryWorkflowAuthority.RequireActorAsync(db, tenantId, actor, PosPermissions.InventoryApprove, true, ct);
        var session = await Lock(tenantId, id, ct);
        if (session.Status == StockCountStatus.Posted)
        {
            if (session.Version != request.Version + 1 || session.PostingReason != reason || session.VarianceAccountId != request.VarianceAccountId)
                throw new InventoryWorkflowException("STOCK_COUNT_POST_CONFLICT", "The posted count belongs to a different confirmation.");
            return session;
        }
        Version(session, request.Version, StockCountStatus.Draft);
        var stocks = await Stocks(session, ct);
        foreach (var line in session.Lines)
        {
            var stock = stocks[(session.WarehouseId, line.VariantId)];
            if (stock.ProjectionMutationVersion != line.StockVersion || stock.Quantity != line.SystemQuantity ||
                stock.ReservedQuantity != line.ReservedSnapshot || stock.DamagedQuantity != line.DamagedSnapshot)
                throw new InventoryWorkflowException("STOCK_COUNT_STALE", "Inventory or reservations changed after the snapshot. Cancel this draft and create a fresh count; recount the affected physical units.");
            if (!line.CountedQuantity.HasValue || !InventoryWorkflowAuthority.Quantity(line.CountedQuantity.Value))
                throw new InventoryWorkflowException("STOCK_COUNT_INCOMPLETE", "Enter a valid physical count for every line.");
            if (line.Variance != 0 && string.IsNullOrWhiteSpace(line.VarianceReason))
                throw new InventoryWorkflowException("STOCK_COUNT_VARIANCE_REASON", "Every nonzero variance requires a reason.");
            ProductVariantStockMutationSupport.ValidateQuantityChange(stock, line.Variance!.Value, false, "Stock count");
        }
        // Stock -> period -> ledger is the same lock order as canonical sales/purchases.
        await AccountingPeriodPostingGuard.RequireOpenAsync(db, tenantId, AccountingPeriodService.Utc(session.CountDate), ct);
        using var authority = InventoryWorkflowMutationScope.Authorize();
        var changes = new List<PendingProductVariantStockChange>();
        var movementIds = new Dictionary<Guid, Guid>();
        var values = session.Lines.ToDictionary(x => x.Id, x => decimal.Round(x.Variance!.Value * x.UnitCost, 4, MidpointRounding.AwayFromZero));
        if (values.Values.Any(value => !InventoryWorkflowAuthority.Quantity(Math.Abs(value))))
            throw new InventoryWorkflowException("STOCK_COUNT_VALUE", "A line valuation exceeds the supported ledger amount. Split the physical count into smaller sessions.");
        foreach (var line in session.Lines.OrderBy(x => x.VariantId))
        {
            if (line.Variance == 0) continue;
            var movement = await inventory.AdjustStockAsync(session.WarehouseId, line.ProductId, line.Variance!.Value,
                StockMovementType.Adjustment, line.VarianceReason!, line.VariantId, session.Id, session.Number);
            Stamp(movement, session, line, actor, false); movementIds[line.Id] = movement.Id;
            changes.Add(Change(movement));
        }
        var gain = values.Values.Where(x => x > 0).Sum();
        var loss = -values.Values.Where(x => x < 0).Sum();
        if (!InventoryWorkflowAuthority.Quantity(gain) || !InventoryWorkflowAuthority.Quantity(loss))
            throw new InventoryWorkflowException("STOCK_COUNT_VALUE", "The stock count valuation exceeds the supported ledger amount. Split the count into smaller sessions.");
        Account? varianceAccount = null;
        if (request.VarianceAccountId.HasValue || gain > 0 || loss > 0)
        {
            varianceAccount = await db.LedgerAccounts.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.VarianceAccountId &&
                x.IsActive && !x.IsDeleted && x.AccountType == AccountType.Expense, ct)
                ?? throw new InventoryWorkflowException("STOCK_COUNT_VARIANCE_ACCOUNT", "Select an active tenant expense account for inventory count gains/losses.");
        }
        if (gain > 0 || loss > 0)
        {
            var asset = await accounts.GetByCodeOrDefaultAsync(tenantId, AccountCodes.Inventory, ct);
            if (asset == null || !asset.IsActive || asset.IsDeleted || asset.AccountType != AccountType.Asset)
                throw new InventoryWorkflowException("STOCK_COUNT_INVENTORY_ACCOUNT", "An active tenant inventory asset account (1310) is required before posting the count.");
            var lines = new List<PostingLine>();
            if (gain > 0) { lines.Add(PostingLine.Debit(asset.Id, gain, "Stock count gain")); lines.Add(PostingLine.Credit(varianceAccount!.Id, gain, "Stock count gain")); }
            if (loss > 0) { lines.Add(PostingLine.Debit(varianceAccount!.Id, loss, "Stock count loss")); lines.Add(PostingLine.Credit(asset.Id, loss, "Stock count loss")); }
            var journal = await posting.PostJournalAsync(new PostingRequest
            {
                TenantId = tenantId, JournalDate = AccountingPeriodService.Utc(session.CountDate), SourceType = "StockCountSession", SourceId = id,
                SourceReference = session.Number, Description = JournalReason(reason), UserId = actor, IdempotencyKey = $"STOCK_COUNT:{tenantId}:{id}", Lines = lines
            }, ct);
            session.JournalId = journal.Id;
        }
        foreach (var line in session.Lines)
        {
            line.PostedValue = values[line.Id];
            line.MovementId = movementIds.TryGetValue(line.Id, out var movementId) ? movementId : null;
        }
        session.Status = StockCountStatus.Posted; session.PostedBy = actor; session.PostedAtUtc = DateTime.UtcNow;
        session.PostingReason = reason; session.VarianceAccountId = request.VarianceAccountId;
        Advance(session, actor); Event(session, actor, "Post", reason);
        await db.SaveChangesAsync(ct);
        await ProductVariantStockMutationSupport.PublishCommittedStockEventsAsync(db, tenantId, changes, ct);
        await tx.CommitAsync(ct); return session;
    }

    public async Task<StockCountSession> CancelOrReverseAsync(Guid tenantId, Guid id, Guid actor, StockCountAction request, bool reverse, CancellationToken ct = default)
    {
        CleanContext();
        var reason = InventoryWorkflowAuthority.Reason(request.Reason);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await InventoryWorkflowAuthority.RequireActorAsync(db, tenantId, actor, reverse ? PosPermissions.InventoryApprove : PosPermissions.InventoryAdjust, reverse, ct);
        var session = await Lock(tenantId, id, ct);
        Version(session, request.Version, reverse ? StockCountStatus.Posted : StockCountStatus.Draft);
        using var authority = InventoryWorkflowMutationScope.Authorize();
        var changes = new List<PendingProductVariantStockChange>();
        if (reverse)
        {
            var reversalMovementIds = new Dictionary<Guid, Guid>();
            var stocks = await Stocks(session, ct);
            foreach (var line in session.Lines)
                ProductVariantStockMutationSupport.ValidateQuantityChange(stocks[(session.WarehouseId, line.VariantId)], -line.Variance!.Value, false, "Stock count reversal");
            await AccountingPeriodPostingGuard.RequireOpenAsync(db, tenantId, DateTime.UtcNow, ct);
            foreach (var line in session.Lines.OrderBy(x => x.VariantId).Where(x => x.Variance != 0))
            {
                var movement = await inventory.AdjustStockAsync(session.WarehouseId, line.ProductId, -line.Variance!.Value,
                    StockMovementType.Adjustment, reason, line.VariantId, session.Id, session.Number);
                Stamp(movement, session, line, actor, true); reversalMovementIds[line.Id] = movement.Id; changes.Add(Change(movement));
            }
            if (session.JournalId.HasValue)
                session.ReversalJournalId = (await posting.ReverseJournalAsync(tenantId, session.JournalId.Value, JournalReason(reason), actor, ct)).Id;
            foreach (var line in session.Lines.Where(x => x.Variance != 0)) line.ReversalMovementId = reversalMovementIds[line.Id];
            session.Status = StockCountStatus.Reversed; session.ReversedBy = actor; session.ReversedAtUtc = DateTime.UtcNow; session.ReversalReason = reason;
        }
        else session.Status = StockCountStatus.Cancelled;
        Advance(session, actor); Event(session, actor, reverse ? "Reverse" : "Cancel", reason);
        await db.SaveChangesAsync(ct);
        await ProductVariantStockMutationSupport.PublishCommittedStockEventsAsync(db, tenantId, changes, ct);
        await tx.CommitAsync(ct); return session;
    }

    private async Task<StockCountSession> Lock(Guid tenantId, Guid id, CancellationToken ct)
    {
        var session = await db.StockCountSessions.FromSqlInterpolated($"SELECT * FROM \"StockCountSessions\" WHERE \"TenantId\"={tenantId} AND \"Id\"={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new InventoryWorkflowException("STOCK_COUNT_NOT_FOUND", "Stock count session not found.");
        await db.Entry(session).ReloadAsync(ct);
        await db.Entry(session).Collection(x => x.Lines).LoadAsync(ct);
        foreach (var line in session.Lines) await db.Entry(line).ReloadAsync(ct);
        return session;
    }
    private async Task<Dictionary<(Guid WarehouseId, Guid VariantId), ProductVariantStock>> Stocks(StockCountSession session, CancellationToken ct)
    {
        if (!await db.Warehouses.AnyAsync(x => x.TenantId == session.TenantId && x.Id == session.WarehouseId && x.IsActive && !x.IsDeleted, ct))
            throw new InventoryWorkflowException("STOCK_COUNT_WAREHOUSE", "The count warehouse is no longer active.");
        var variantIds = session.Lines.Select(x => x.VariantId).ToArray();
        if (await db.ProductVariants.CountAsync(x => x.TenantId == session.TenantId && variantIds.Contains(x.Id) && x.IsActive && !x.IsDeleted &&
            x.Product.TenantId == session.TenantId && x.Product.IsActive && !x.Product.IsDeleted && x.Product.TrackStock, ct) != variantIds.Length)
            throw new InventoryWorkflowException("STOCK_COUNT_VARIANTS", "A counted product variant is no longer active inventory. No count changes were applied.");
        var stocks = await ProductVariantStockMutationSupport.LockMultipleStocksAsync(db, session.TenantId,
            session.Lines.Select(x => new StockLockTarget(session.WarehouseId, x.VariantId, false)), ct);
        if (stocks.Count != session.Lines.Count)
            throw new InventoryWorkflowException("STOCK_COUNT_STOCK_MISSING", "A snapshot stock row is no longer available. No count changes were applied.");
        return stocks;
    }
    private static void Version(StockCountSession session, long version, StockCountStatus required)
    {
        if (session.Version != version || session.Status != required)
            throw new InventoryWorkflowException("STOCK_COUNT_CONFLICT", "The stock count changed or its status does not permit this action. Reload before confirming.");
    }
    private void CleanContext()
    {
        if (db.ChangeTracker.HasChanges()) throw new InventoryWorkflowException("STOCK_COUNT_PENDING_STATE", "The workflow cannot flush unrelated caller changes.");
    }
    private static void Advance(StockCountSession session, Guid actor)
    { session.Version++; session.UpdatedBy = actor; session.UpdatedAt = DateTime.UtcNow; }
    // Keep the full 1000-character reason on the immutable count history; ledger
    // descriptions have a 500-character limit including the reversal prefix.
    private static string JournalReason(string reason) => reason.Length <= 400 ? reason : reason[..360] + "… (full reason in stock count history)";
    private void Event(StockCountSession session, Guid actor, string action, string reason) => db.StockCountEvents.Add(new StockCountEvent
    { Id = Guid.NewGuid(), TenantId = session.TenantId, StockCountSessionId = session.Id, Version = session.Version, Status = session.Status, Action = action, Reason = reason, CreatedBy = actor, CreatedAt = DateTime.UtcNow });
    private static void Stamp(StockMovement movement, StockCountSession session, StockCountLine line, Guid actor, bool reverse)
    {
        movement.CreatedBy = actor; movement.ReferenceType = reverse ? "StockCountReversal" : nameof(StockCountSession);
        movement.ReferenceLineId = line.Id; movement.Price = line.UnitCost; movement.Source = nameof(StockCountService);
    }
    private static PendingProductVariantStockChange Change(StockMovement m) => ProductVariantStockMutationSupport.BuildPendingStockChange(
        m.ProductVariantId!.Value, m.ProductId, m.WarehouseId, m.QuantityAfter - m.QuantityChange, m.QuantityAfter, m.Notes!, m.ReferenceId, m.ReferenceType);
}
