using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Controllers;

[ApiController]
[Route("api/accounting-periods")]
[Authorize(Policy = PosPermissions.FinanceView)]
public sealed class AccountingPeriodsController(ApplicationDbContext db, ITenantContext tenant, AccountingPeriodService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] AccountingPeriodStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (status.HasValue && !Enum.IsDefined(status.Value))) return BadRequest();
        var query = db.AccountingPeriods.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Code.Contains(search) || x.Name.Contains(search));
        if (status.HasValue) query = query.Where(x => x.Status == status);
        var items = await query.OrderByDescending(x => x.StartDate).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total = await query.CountAsync(ct), page, pageSize, items = items.Select(PeriodResponse.From).ToList() });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var period = await db.AccountingPeriods.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.RequiredTenantId && x.Id == id, ct);
        if (period == null) return NotFound();
        var totals = await service.TotalsAsync(tenant.RequiredTenantId, period, ct);
        var history = await db.AccountingPeriodEvents.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId && x.AccountingPeriodId == id)
            .OrderBy(x => x.Version).Select(x => new { x.Id, x.CreatedBy, x.CreatedAt, x.Action, x.Reason, x.PreviousStatus,
                x.Status, x.Version, x.JournalCount, x.TotalDebits, x.TotalCredits }).ToListAsync(ct);
        return Ok(new { period = PeriodResponse.From(period), totals, history });
    }

    [HttpGet("{id:guid}/journals")]
    [Authorize(Policy = PosPermissions.ReportsFinancialView)]
    public async Task<IActionResult> Journals(Guid id, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (page < 1) return BadRequest();
        var period = await db.AccountingPeriods.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.RequiredTenantId && x.Id == id, ct);
        if (period == null) return NotFound();
        var from = AccountingPeriodService.Utc(period.StartDate);
        var until = AccountingPeriodService.Utc(period.EndDate).AddDays(1);
        var query = db.JournalEntries.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId && x.JournalDate >= from && x.JournalDate < until);
        return Ok(new { total = await query.CountAsync(ct), page, pageSize = 50,
            items = await query.OrderBy(x => x.JournalDate).ThenBy(x => x.JournalNumber).Skip((page - 1) * 50).Take(50)
                .Select(x => new { x.Id, x.JournalNumber, x.JournalDate, x.SourceType, x.SourceId, x.SourceReference,
                    x.CreatedByUserId, x.AccountingPeriodId, debit = x.LedgerEntries.Sum(l => l.DebitAmount), credit = x.LedgerEntries.Sum(l => l.CreditAmount) }).ToListAsync(ct) });
    }

    [HttpPost]
    [Authorize(Policy = PosPermissions.FinanceAdmin)]
    public Task<IActionResult> Create(CreatePeriodRequest request, CancellationToken ct) => Mutate(async actor =>
        await service.CreateAsync(tenant.RequiredTenantId, actor, request.Code, request.Name, request.StartDate, request.EndDate, request.Open, request.Reason, ct));

    [HttpGet("{id:guid}/journals/{journalId:guid}")]
    [Authorize(Policy = PosPermissions.ReportsFinancialView)]
    public async Task<IActionResult> Journal(Guid id, Guid journalId, CancellationToken ct)
    {
        var period = await db.AccountingPeriods.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.RequiredTenantId && x.Id == id, ct);
        if (period == null) return NotFound();
        var from = AccountingPeriodService.Utc(period.StartDate);
        var until = AccountingPeriodService.Utc(period.EndDate).AddDays(1);
        var journal = await db.JournalEntries.AsNoTracking()
            .Where(x => x.TenantId == tenant.RequiredTenantId && x.Id == journalId && x.JournalDate >= from && x.JournalDate < until)
            .Select(x => new { x.Id, x.JournalNumber, x.JournalDate, x.Description, x.SourceType, x.SourceId, x.SourceReference, x.CreatedByUserId,
                lines = x.LedgerEntries.OrderBy(l => l.LineNumber).Select(l => new { l.LineNumber, l.DebitAmount, l.CreditAmount, l.LineDescription,
                    accountCode = l.Account.AccountCode, accountName = l.Account.Name }).ToList() }).SingleOrDefaultAsync(ct);
        return journal == null ? NotFound() : Ok(journal);
    }

    [HttpPost("{id:guid}/{operation:regex(^(open|close|reopen)$)}")]
    [Authorize(Policy = PosPermissions.FinanceAdmin)]
    public Task<IActionResult> Transition(Guid id, string operation, PeriodTransitionRequest request, CancellationToken ct) => Mutate(async actor =>
        await service.TransitionAsync(tenant.RequiredTenantId, id, actor, operation, request.Version, request.Reason, ct));

    private async Task<IActionResult> Mutate(Func<Guid, Task<AccountingPeriod>> operation)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Unauthorized();
        try { return Ok(PeriodResponse.From(await operation(actor))); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (AccountingPeriodException ex)
        {
            return ex.Code == "PERIOD_NOT_FOUND" ? NotFound(new { code = ex.Code, message = ex.Message })
                : Conflict(new { code = ex.Code, message = ex.Message });
        }
        catch (DbUpdateConcurrencyException) { return Conflict(new { code = "PERIOD_CONFLICT", message = "Reload the accounting period before confirming." }); }
    }
}

public sealed record CreatePeriodRequest([Required, MaxLength(50)] string Code, [Required, MaxLength(200)] string Name,
    DateOnly StartDate, DateOnly EndDate, bool Open, [Required, MaxLength(1000)] string Reason);
public sealed record PeriodTransitionRequest([Range(1, long.MaxValue)] long Version, [Required, MaxLength(1000)] string Reason);

public sealed record PeriodResponse(Guid Id, string Code, string Name, DateOnly StartDate, DateOnly EndDate,
    AccountingPeriodStatus Status, long Version, Guid? CreatedBy, DateTime CreatedAt, Guid? ClosedBy, DateTime? ClosedAtUtc,
    Guid? ReopenedBy, DateTime? ReopenedAtUtc, string Reason)
{
    public static PeriodResponse From(AccountingPeriod p) => new(p.Id, p.Code, p.Name, p.StartDate, p.EndDate, p.Status, p.Version,
        p.CreatedBy, p.CreatedAt, p.ClosedBy, p.ClosedAtUtc, p.ReopenedBy, p.ReopenedAtUtc, p.Reason);
}
