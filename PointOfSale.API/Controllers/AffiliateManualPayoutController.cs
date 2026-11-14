using System.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.API.Controllers;

[ApiController]
[Authorize(Roles = "Owner,Admin")]
[Route("api/admin/affiliates/payouts/{requestId:guid}/manual-payout")]
public sealed class AffiliateManualPayoutController(ApplicationDbContext db, ITenantContext tenant,
    IAffiliateSettlementOrchestrator orchestrator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Review(Guid requestId, CancellationToken ct)
    {
        var store = tenant.RequiredTenantId;
        var r = await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == requestId && r.StoreTenantId == store, ct);
        if (r == null) return NotFound(new { code = "AFFILIATE_PAYOUT_REQUEST_NOT_FOUND" });
        var affiliate = await db.AffiliateAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == r.AffiliateAccountId, ct);
        var cashboxes = await db.Cashboxes.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == store && c.IsActive && !c.IsDeleted).ToListAsync(ct);
        var sources = new List<object>();
        foreach (var cashbox in cashboxes)
        {
            var source = await AffiliateManualCashSource.ResolveAsync(db, store, cashbox.Id, ct);
            if (source != null) sources.Add(new { cashboxId = cashbox.Id, cashboxName = cashbox.Name,
                treasuryAccountId = source.Id, sourceAccountId = source.AccountId, accountCode = source.Account!.AccountCode,
                sourceFingerprint = AffiliateManualCashSource.Fingerprint(cashbox.Id, source), availableFunds = source.Account.CurrentBalance });
        }
        // Return a bounded read contract, not a domain entity (including when null).
        var confirmation = await db.AffiliateManualPayouts.AsNoTracking().Where(m => m.RequestId == requestId && m.StoreTenantId == store)
            .Select(m => new { m.Id, m.RequestId, m.StoreTenantId, m.AffiliateAccountId, m.AffiliateUserId,
                m.Amount, m.Currency, m.Method, m.DestinationNumber, m.DestinationType, m.DestinationCapturedAtUtc,
                m.CashboxId, m.TreasuryAccountId, m.SourceAccountId, m.SourceAccountCode, m.SourceAccountName,
                m.AdministratorId, m.AdministratorRole, m.ExecutedAtUtc, m.Attestation,
                m.InternalReference, m.Notes, m.BalanceLedgerId, m.AllocationsJson }).SingleOrDefaultAsync(ct);
        var journals = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Where(j => j.TenantId == store &&
            j.SourceId == requestId && j.SourceType == "AffiliateCommissionPaid")
            .Select(j => new { j.Id, j.JournalNumber, j.IdempotencyKey, j.CreatedAt }).ToListAsync(ct);
        var eligibility = await AffiliateCommissionEligibility.LoadAsync(db, r.AffiliateAccountId, store, ct);
        return Ok(new { requestId, r.AffiliateAccountId, affiliateUserId = affiliate.UserId, affiliateName = affiliate.DisplayName,
            storeTenantId = store, amount = r.ApprovedAmount ?? r.RequestedAmount, r.Currency, status = r.Status.ToString(),
            destination = new { number = r.PayoutWalletNumberSnapshot, type = r.PayoutWalletTypeSnapshot,
                updatedAtUtc = r.PayoutWalletUpdatedAtUtcSnapshot, capturedAtUtc = r.PayoutWalletSnapshotCapturedAtUtc },
            destinationFingerprint = AffiliateSettlementOrchestrator.DestinationFingerprint(r), sources,
            methods = new[] { "Cash" }, attestation = AffiliateManualPayout.AttestationText, confirmation, journals,
            eligibleCommissions = eligibility.RealEntries.Where(e => e.EntryType == CommissionEntryType.Approved)
                .Select(e => new { e.Id, e.OrderId, e.OrderItemId, e.CommissionAmount, e.Currency }).ToList(),
            netApproved = eligibility.NetApproved });
    }

    [HttpPost]
    public async Task<IActionResult> Execute(Guid requestId, [FromBody] AdminManualPayoutCommand command, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Unauthorized();
        try
        {
            var result = await orchestrator.ExecuteAdminManualPayoutAsync(requestId, tenant.RequiredTenantId, actor, command, ct);
            return Ok(new { requestId, status = "Paid", result.ManualPayoutId, result.ExecutedAtUtc, result.WasIdempotentSkip });
        }
        catch (SecurityException) { return StatusCode(403, new { code = "AFFILIATE_MANUAL_ADMIN_REQUIRED", message = "An active administrator of this store is required." }); }
        catch (InvalidOperationException ex) { var error = AffiliateFinancialErrorContract.From(ex); return StatusCode(error.StatusCode, new { code = error.Code, message = error.Message }); }
        catch (PointOfSale.Core.Exceptions.Accounting.InsufficientFundsException) { return Conflict(new { code = "AFFILIATE_MANUAL_SOURCE_UNDERFUNDED", message = "Refresh the selected cashbox funding before retrying." }); }
    }
}
