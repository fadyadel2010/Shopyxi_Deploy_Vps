using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Exceptions.Accounting;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Controllers;

[ApiController]
[Route("api/financial-vouchers")]
[Authorize(Policy = PosPermissions.FinanceView)]
public sealed class FinancialVouchersController(ApplicationDbContext db, ITenantContext tenant, FinancialVoucherService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] FinancialVoucherKind? kind,
        [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (page is < 1 or > 1000000 || (kind.HasValue && !Enum.IsDefined(kind.Value))) return BadRequest();
        var query = db.FinancialVouchers.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId);
        if (kind.HasValue) query = query.Where(x => x.Kind == kind);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Number.Contains(search) || x.PartyName.Contains(search));
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * 50).Take(50).ToListAsync(ct);
        return Ok(new { total = await query.CountAsync(ct), page, pageSize = 50, items = items.Select(VoucherResponse.From).ToList() });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var voucher = await db.FinancialVouchers.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.RequiredTenantId && x.Id == id, ct);
        if (voucher == null) return NotFound();
        var history = await db.FinancialVoucherEvents.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId && x.FinancialVoucherId == id)
            .OrderBy(x => x.Version).Select(x => new { x.Id, x.Action, x.Reason, x.Version, x.JournalId, x.CreatedBy, x.CreatedAt }).ToListAsync(ct);
        return Ok(new { voucher = VoucherResponse.From(voucher), history });
    }

    [HttpGet("sources")]
    [Authorize(Policy = PosPermissions.FinanceOperate)]
    public async Task<IActionResult> Sources([FromQuery] FinancialVoucherKind kind, [FromQuery] string? search,
        [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind) || page is < 1 or > 1000000) return BadRequest();
        var tenantId = tenant.RequiredTenantId;
        IQueryable<VoucherSourceResponse> query;
        if (kind == FinancialVoucherKind.Receipt)
            query = db.CustomerPayments.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Amount > 0 &&
                x.Customer.TenantId == tenantId && x.Customer.IsActive && !x.Customer.IsDeleted &&
                !db.FinancialVouchers.Any(v => v.TenantId == tenantId && v.Kind == kind && v.SourcePaymentId == x.Id))
                .Select(x => new VoucherSourceResponse { Id = x.Id, PartyName = x.Customer.Name, PaymentDate = x.PaymentDate, Amount = x.Amount, ReferenceNumber = x.ReferenceNumber,
                    Posted = db.JournalEntries.Any(j => j.TenantId == tenantId && j.SourceType == nameof(CustomerPayment) && j.SourceId == x.Id && j.Status == PointOfSale.Core.Enums.JournalStatus.Posted) });
        else
            query = db.SupplierPayments.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Amount > 0 && x.PurchaseInvoiceId == null &&
                x.Supplier.TenantId == tenantId && x.Supplier.IsActive && !x.Supplier.IsDeleted &&
                !db.FinancialVouchers.Any(v => v.TenantId == tenantId && v.Kind == kind && v.SourcePaymentId == x.Id))
                .Select(x => new VoucherSourceResponse { Id = x.Id, PartyName = x.Supplier.Name, PaymentDate = x.PaymentDate, Amount = x.Amount, ReferenceNumber = x.ReferenceNumber,
                    Posted = db.JournalEntries.Any(j => j.TenantId == tenantId && j.SourceType == nameof(SupplierPayment) && j.SourceId == x.Id && j.Status == PointOfSale.Core.Enums.JournalStatus.Posted) });
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.PartyName.Contains(search) || (x.ReferenceNumber != null && x.ReferenceNumber.Contains(search)));
        return Ok(new { total = await query.CountAsync(ct), page, pageSize = 50,
            items = await query.OrderByDescending(x => x.PaymentDate).ThenBy(x => x.Id).Skip((page - 1) * 50).Take(50).ToListAsync(ct) });
    }

    [HttpGet("{id:guid}/journals/{journalId:guid}")]
    [Authorize(Policy = PosPermissions.ReportsFinancialView)]
    public async Task<IActionResult> Journal(Guid id, Guid journalId, CancellationToken ct)
    {
        if (!await db.FinancialVouchers.AnyAsync(x => x.TenantId == tenant.RequiredTenantId && x.Id == id && (x.JournalId == journalId || x.ReversalJournalId == journalId), ct)) return NotFound();
        var journal = await db.JournalEntries.AsNoTracking().Where(x => x.TenantId == tenant.RequiredTenantId && x.Id == journalId)
            .Select(x => new { x.Id, x.JournalNumber, x.JournalDate, x.SourceType, x.SourceId, x.SourceReference, x.Description, x.CreatedByUserId,
                x.ReversesJournalId, x.Status, lines = x.LedgerEntries.OrderBy(l => l.LineNumber)
                    .Select(l => new { l.LineNumber, l.DebitAmount, l.CreditAmount, accountCode = l.Account.AccountCode, accountName = l.Account.Name }).ToList() }).SingleOrDefaultAsync(ct);
        return journal == null ? NotFound() : Ok(journal);
    }

    [HttpPost]
    [Authorize(Policy = PosPermissions.FinanceOperate)]
    public Task<IActionResult> Issue(IssueFinancialVoucher request, CancellationToken ct) => Mutate(actor => service.IssueAsync(tenant.RequiredTenantId, actor, request, ct));

    [HttpPost("{id:guid}/reverse")]
    [Authorize(Policy = PosPermissions.FinanceOperate)]
    public Task<IActionResult> Reverse(Guid id, ReverseFinancialVoucher request, CancellationToken ct) => Mutate(actor => service.ReverseAsync(tenant.RequiredTenantId, id, actor, request, ct));

    private async Task<IActionResult> Mutate(Func<Guid, Task<FinancialVoucher>> operation)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Unauthorized();
        try { return Ok(VoucherResponse.From(await operation(actor))); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (FinancialVoucherException ex) { return ex.Code == "VOUCHER_NOT_FOUND" ? NotFound(new { code = ex.Code, message = ex.Message }) : Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { code = "VOUCHER_CONFLICT", message = "Reload the voucher before confirming." }); }
        catch (InsufficientFundsException) { return Conflict(new { code = "VOUCHER_INSUFFICIENT_FUNDS", message = "The original receipt account lacks funds for compensation. No partial reversal was applied." }); }
    }
}

public sealed class VoucherSourceResponse
{
    public Guid Id { get; init; }
    public string PartyName { get; init; } = string.Empty;
    public DateTime PaymentDate { get; init; }
    public decimal Amount { get; init; }
    public string? ReferenceNumber { get; init; }
    public bool Posted { get; init; }
}

public sealed record VoucherResponse(Guid Id, string Number, FinancialVoucherKind Kind, Guid SourcePaymentId, Guid PartyId, string PartyName,
    decimal Amount, string Currency, DateTime PaymentDate, Guid JournalId, string Reason, long Version, FinancialVoucherStatus Status,
    Guid? CreatedBy, DateTime CreatedAt, Guid? ReversalJournalId, Guid? ReversedBy, DateTime? ReversedAtUtc, string? ReversalReason)
{
    public static VoucherResponse From(FinancialVoucher x) => new(x.Id, x.Number, x.Kind, x.SourcePaymentId, x.PartyId, x.PartyName, x.Amount,
        x.Currency, x.PaymentDate, x.JournalId, x.Reason, x.Version, x.Status, x.CreatedBy, x.CreatedAt, x.ReversalJournalId, x.ReversedBy, x.ReversedAtUtc, x.ReversalReason);
}
