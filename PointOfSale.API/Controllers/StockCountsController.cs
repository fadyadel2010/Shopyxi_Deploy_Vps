using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Services.Inventory;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Controllers;

[ApiController]
[Route("api/stock-counts")]
[Authorize(Policy = PosPermissions.InventoryView)]
public sealed class StockCountsController(ApplicationDbContext db, ITenantContext tenant, StockCountService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] StockCountStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (page < 1 || page > 1000000 || pageSize is < 1 or > 100 || (status.HasValue && !Enum.IsDefined(status.Value))) return BadRequest();
        var query = db.StockCountSessions.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Number.Contains(search) || x.Reason.Contains(search));
        if (status.HasValue) query = query.Where(x => x.Status == status);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total = await query.CountAsync(ct), page, pageSize, items = items.Select(StockCountResponse.From).ToList() });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var session = await db.StockCountSessions.AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.TenantId == tenant.RequiredTenantId && x.Id == id, ct);
        if (session == null) return NotFound();
        var history = await db.StockCountEvents.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId && x.StockCountSessionId == id)
            .OrderBy(x => x.Version).Select(x => new { x.Id, x.Version, x.Status, x.Action, x.Reason, x.CreatedBy, x.CreatedAt }).ToListAsync(ct);
        return Ok(new { session = StockCountResponse.From(session), history });
    }

    [HttpGet("variants")]
    public async Task<IActionResult> Variants([FromQuery] string? search, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (page is < 1 or > 1000000) return BadRequest();
        var query = db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId && x.IsActive && !x.IsDeleted &&
            x.Product.TenantId == tenant.RequiredTenantId && x.Product.IsActive && !x.Product.IsDeleted && x.Product.TrackStock);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Product.Name.Contains(search) || (x.SKU != null && x.SKU.Contains(search)));
        return Ok(new { total = await query.CountAsync(ct), page, pageSize = 50,
            items = await query.OrderBy(x => x.Product.Name).ThenBy(x => x.Id).Skip((page - 1) * 50).Take(50)
                .Select(x => new { x.Id, x.ProductId, productName = x.Product.Name, x.SKU }).ToListAsync(ct) });
    }

    [HttpGet("{id:guid}/journals/{journalId:guid}")]
    [Authorize(Policy = PosPermissions.ReportsFinancialView)]
    public async Task<IActionResult> Journal(Guid id, Guid journalId, CancellationToken ct)
    {
        if (!await db.StockCountSessions.AnyAsync(x => x.TenantId == tenant.RequiredTenantId && x.Id == id &&
            (x.JournalId == journalId || x.ReversalJournalId == journalId), ct)) return NotFound();
        var journal = await db.JournalEntries.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId && x.Id == journalId)
            .Select(x => new { x.Id, x.JournalNumber, x.JournalDate, x.SourceType, x.SourceId, x.SourceReference, x.Description, x.CreatedByUserId,
                x.ReversesJournalId, lines = x.LedgerEntries.OrderBy(l => l.LineNumber)
                    .Select(l => new { l.LineNumber, l.DebitAmount, l.CreditAmount, accountCode = l.Account.AccountCode, accountName = l.Account.Name }).ToList() })
            .SingleOrDefaultAsync(ct);
        return journal == null ? NotFound() : Ok(journal);
    }

    [HttpGet("variance-accounts")]
    [Authorize(Policy = PosPermissions.FinanceOperate)]
    public async Task<IActionResult> VarianceAccounts(CancellationToken ct) => Ok(await db.LedgerAccounts.AsNoTracking()
        .Where(x => x.TenantId == tenant.RequiredTenantId && x.IsActive && !x.IsDeleted && x.AccountType == AccountType.Expense)
        .OrderBy(x => x.AccountCode).Select(x => new { x.Id, x.AccountCode, x.Name }).ToListAsync(ct));

    [HttpPost]
    [Authorize(Policy = PosPermissions.InventoryAdjust)]
    public Task<IActionResult> Create(CreateStockCount request, CancellationToken ct) => Mutate(actor => service.CreateAsync(tenant.RequiredTenantId, actor, request, ct));

    [HttpPut("{id:guid}/counts")]
    [Authorize(Policy = PosPermissions.InventoryAdjust)]
    public Task<IActionResult> Save(Guid id, SaveStockCount request, CancellationToken ct) => Mutate(actor => service.SaveAsync(tenant.RequiredTenantId, id, actor, request, ct));

    [HttpPost("{id:guid}/post")]
    [Authorize(Policy = PosPermissions.InventoryApprove)]
    [Authorize(Policy = PosPermissions.FinanceOperate)]
    public Task<IActionResult> Post(Guid id, PostStockCount request, CancellationToken ct) => Mutate(actor => service.PostAsync(tenant.RequiredTenantId, id, actor, request, ct));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = PosPermissions.InventoryAdjust)]
    public Task<IActionResult> Cancel(Guid id, StockCountAction request, CancellationToken ct) => Mutate(actor => service.CancelOrReverseAsync(tenant.RequiredTenantId, id, actor, request, false, ct));

    [HttpPost("{id:guid}/reverse")]
    [Authorize(Policy = PosPermissions.InventoryApprove)]
    [Authorize(Policy = PosPermissions.FinanceOperate)]
    public Task<IActionResult> Reverse(Guid id, StockCountAction request, CancellationToken ct) => Mutate(actor => service.CancelOrReverseAsync(tenant.RequiredTenantId, id, actor, request, true, ct));

    private async Task<IActionResult> Mutate(Func<Guid, Task<StockCountSession>> operation)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Unauthorized();
        try { return Ok(StockCountResponse.From(await operation(actor))); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InventoryWorkflowException ex)
        {
            return ex.Code == "STOCK_COUNT_NOT_FOUND" ? NotFound(new { code = ex.Code, message = ex.Message })
                : Conflict(new { code = ex.Code, message = ex.Message });
        }
        catch (DbUpdateConcurrencyException) { return Conflict(new { code = "STOCK_COUNT_CONFLICT", message = "Reload the stock count before confirming." }); }
        catch (InsufficientStockException ex) { return Conflict(new { code = "STOCK_COUNT_RESERVED_OR_UNAVAILABLE", message = ex.Message }); }
    }
}

public sealed record StockCountResponse(Guid Id, string Number, Guid WarehouseId, DateOnly CountDate, DateTime SnapshotAtUtc,
    StockCountStatus Status, long Version, string Reason, Guid? CreatedBy, DateTime CreatedAt, Guid? CountedBy, DateTime? CountedAtUtc,
    Guid? PostedBy, DateTime? PostedAtUtc, string? PostingReason, Guid? VarianceAccountId, Guid? JournalId,
    Guid? ReversalJournalId, Guid? ReversedBy, DateTime? ReversedAtUtc, string? ReversalReason, IReadOnlyList<StockCountLineResponse> Lines)
{
    public static StockCountResponse From(StockCountSession x) => new(x.Id, x.Number, x.WarehouseId, x.CountDate, x.SnapshotAtUtc,
        x.Status, x.Version, x.Reason, x.CreatedBy, x.CreatedAt, x.CountedBy, x.CountedAtUtc, x.PostedBy, x.PostedAtUtc, x.PostingReason,
        x.VarianceAccountId, x.JournalId, x.ReversalJournalId, x.ReversedBy, x.ReversedAtUtc, x.ReversalReason,
        x.Lines.OrderBy(l => l.ProductName).ThenBy(l => l.Sku).Select(StockCountLineResponse.From).ToList());
}

public sealed record StockCountLineResponse(Guid Id, Guid ProductId, Guid VariantId, string ProductName, string Sku, decimal SystemQuantity,
    decimal ReservedSnapshot, decimal DamagedSnapshot, decimal UnitCost, decimal? CountedQuantity, decimal? Variance, string? VarianceReason,
    Guid? CountedBy, DateTime? CountedAtUtc, decimal PostedValue, Guid? MovementId, Guid? ReversalMovementId)
{
    public static StockCountLineResponse From(StockCountLine x) => new(x.Id, x.ProductId, x.VariantId, x.ProductName, x.Sku, x.SystemQuantity,
        x.ReservedSnapshot, x.DamagedSnapshot, x.UnitCost, x.CountedQuantity, x.Variance, x.VarianceReason, x.CountedBy, x.CountedAtUtc,
        x.PostedValue, x.MovementId, x.ReversalMovementId);
}
