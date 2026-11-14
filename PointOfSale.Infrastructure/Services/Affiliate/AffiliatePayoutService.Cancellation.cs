using System.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;

namespace PointOfSale.Infrastructure.Services.Affiliate;

public partial class AffiliatePayoutService
{
    public async Task<HashSet<Guid>> GetCancellablePayoutIdsAsync(Guid affiliateAccountId,
        IReadOnlyCollection<AffiliatePayoutRequest> requests, CancellationToken ct = default)
    {
        var candidates = requests.Where(r => r.AffiliateAccountId == affiliateAccountId && r.IsCancellationCandidate)
            .Select(r => r.Id).ToArray();
        var debits = await _db.AffiliateBalanceLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.ReferenceId.HasValue && candidates.Contains(l.ReferenceId.Value))
            .Select(l => l.ReferenceId!.Value).ToListAsync(ct);
        var allocations = await _db.AffiliatePayoutTransferAllocations.IgnoreQueryFilters().AsNoTracking()
            .Where(l => candidates.Contains(l.ReferenceId)).Select(l => l.ReferenceId).ToListAsync(ct);
        return candidates.Except(debits).Except(allocations).ToHashSet();
    }

    public async Task<AffiliatePayoutRequest> CancelPayoutAsync(Guid requestId, Guid affiliateAccountId,
        Guid affiliateUserId, string? reason, Guid? trustedStoreTenantId = null, CancellationToken ct = default)
    {
        if (affiliateUserId == Guid.Empty || affiliateAccountId == Guid.Empty)
            throw new SecurityException("AFFILIATE_PAYOUT_CANCELLATION_FORBIDDEN");
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason?.Length > 1000) throw new ArgumentException("Cancellation reason is too long.");
        // Resolve ownership before the transition helper takes any lock. The store is
        // obtained from the owned persisted row, never a caller-supplied affiliate ID.
        var store = await _db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Id == requestId && r.AffiliateAccountId == affiliateAccountId &&
                (!trustedStoreTenantId.HasValue || r.StoreTenantId == trustedStoreTenantId.Value))
            .Select(r => (Guid?)r.StoreTenantId).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("AFFILIATE_PAYOUT_REQUEST_NOT_FOUND");

        return await ExecuteWithRowLockAsync(requestId, store, async (request, _, token) =>
        {
            // Repeat all authority checks inside the serializable transaction.
            if (request.AffiliateAccountId != affiliateAccountId ||
                !await _db.AffiliateUsers.IgnoreQueryFilters().AnyAsync(u => u.Id == affiliateUserId &&
                    u.AffiliateAccountId == affiliateAccountId && !u.IsDeleted && u.EmailVerified, token) ||
                !await _db.AffiliateAccounts.IgnoreQueryFilters().AnyAsync(a => a.Id == affiliateAccountId &&
                    a.UserId == affiliateUserId && !a.IsDeleted, token))
                throw new SecurityException("AFFILIATE_PAYOUT_CANCELLATION_FORBIDDEN");
            if (request.Status == PayoutRequestStatus.Cancelled)
                throw new InvalidOperationException("AFFILIATE_PAYOUT_ALREADY_CANCELLED");
            if (!(await GetCancellablePayoutIdsAsync(affiliateAccountId, new[] { request }, token)).Contains(request.Id))
                throw new InvalidOperationException("AFFILIATE_PAYOUT_CANCELLATION_UNSAFE");

            var before = JsonSerializer.Serialize(new { request.Id, request.StoreTenantId,
                request.AffiliateAccountId, Status = request.Status.ToString(), request.RequestedAmount, request.RemainingAmount });
            request.Cancel();
            _db.AuditLogs.Add(new AuditLog
            {
                TenantId = store, UserId = affiliateUserId, TimestampUtc = DateTime.UtcNow,
                ActionType = "AffiliateWithdrawalCancelled", EntityType = nameof(AffiliatePayoutRequest),
                EntityId = request.Id.ToString(), Source = "AffiliatePortal", SuccessFlag = true,
                Description = reason ?? "Affiliate cancelled their pending withdrawal reservation.",
                BeforeDataJson = before,
                AfterDataJson = JsonSerializer.Serialize(new { request.Id, request.StoreTenantId,
                    request.AffiliateAccountId, Status = request.Status.ToString(), request.RequestedAmount,
                    request.RemainingAmount, ReleasedReservation = request.RequestedAmount, Reason = reason,
                    ActorId = affiliateUserId })
            });
        }, null, ct);
    }
}
