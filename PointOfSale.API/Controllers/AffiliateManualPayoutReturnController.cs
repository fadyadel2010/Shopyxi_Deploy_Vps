using System.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.API.Controllers;

[ApiController]
[Authorize(Roles = "Owner,Admin")]
[Route("api/admin/affiliates/payouts/{requestId:guid}/manual-payout/return")]
public sealed class AffiliateManualPayoutReturnController(ApplicationDbContext db, ITenantContext tenant,
    IAffiliateSettlementOrchestrator orchestrator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Review(Guid requestId, CancellationToken ct)
    {
        var store = tenant.RequiredTenantId;
        var payout = await db.AffiliateManualPayouts.AsNoTracking().SingleOrDefaultAsync(m => m.StoreTenantId == store && m.RequestId == requestId, ct);
        if (payout == null) return NotFound(new { code = "AFFILIATE_PAYOUT_REQUEST_NOT_FOUND" });
        var request = await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.Id == requestId && r.StoreTenantId == store, ct);
        var key = AffiliateSettlementOrchestrator.ManualJournalKey(store, requestId);
        var journal = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(j => j.TenantId == store && j.SourceId == requestId && j.IdempotencyKey == key, ct);
        var source = await AffiliateManualCashSource.ResolveAsync(db, store, payout.CashboxId, ct);
        var receipt = await db.AffiliateManualPayoutReturns.AsNoTracking().Where(r => r.StoreTenantId == store && r.RequestId == requestId)
            .Select(r => new { r.Id, r.Amount, r.Currency, r.RecordedAtUtc, r.ReceiptReference, r.Reason }).SingleOrDefaultAsync(ct);
        if (receipt != null) await AffiliatePayoutReturnAuthority.LoadAsync(db, payout.AffiliateAccountId, store, ct);
        var canReturn = request.Status == PayoutRequestStatus.Paid && journal?.Status == PointOfSale.Core.Enums.JournalStatus.Posted &&
            source?.AccountId == payout.SourceAccountId && source.Id == payout.TreasuryAccountId && receipt == null;
        return Ok(new { requestId, payout.AffiliateAccountId, payout.Amount, payout.Currency, payout.CashboxId,
            sourceAccountCode = payout.SourceAccountCode, status = request.Status.ToString(), canReturn, receipt,
            attestation = AffiliateManualPayoutReturn.AttestationText,
            reviewFingerprint = canReturn ? AffiliateSettlementOrchestrator.ReturnReviewFingerprint(payout, journal!.Id,
                AffiliateManualCashSource.Fingerprint(payout.CashboxId, source!)) : null });
    }

    [HttpPost]
    public async Task<IActionResult> Execute(Guid requestId, [FromBody] AdminManualPayoutReturnCommand command, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Unauthorized();
        try
        {
            var result = await orchestrator.ReturnAdminManualPayoutAsync(requestId, tenant.RequiredTenantId, actor, command, ct);
            return Ok(new { requestId, status = "Returned", result.ReturnId, result.RecordedAtUtc, result.WasIdempotentSkip });
        }
        catch (SecurityException) { return StatusCode(403, new { code = "AFFILIATE_MANUAL_ADMIN_REQUIRED", message = "An active administrator of this store is required." }); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DbUpdateException)
        {
            var error = AffiliateFinancialErrorContract.From(ex);
            return StatusCode(error.StatusCode, new { code = error.Code, message = error.Message });
        }
    }
}
