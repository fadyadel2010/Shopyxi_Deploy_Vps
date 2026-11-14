using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Enums;

namespace PointOfSale.API.Services.CommerceHub;

public partial class CommerceHubPayoutService
{
    // Enforced at reservation and before a new external handoff, not just in a
    // dashboard. Completion of a committed handoff remains reconciliation: do
    // not strand an already executed transfer by reapplying a new claim policy.
    private async Task EnsureClaimWindowsClosedAsync(Guid tenantId, IEnumerable<Guid> subOrderIds, CancellationToken ct)
    {
        var ids = subOrderIds.Distinct().ToArray();
        var items = await _dbContext.CommerceHubSubOrderItems.AsNoTracking()
            .Where(i => ids.Contains(i.SubOrderId) && i.SubOrder.StoreId == tenantId && !i.IsDeleted)
            .Select(i => new { i.SubOrderId, i.Status, i.DeliveredAt }).ToListAsync(ct);
        if (ids.Length == 0 || ids.Any(id => !items.Any(i => i.SubOrderId == id)) ||
            items.Any(i => i.Status != CommerceHubOrderItemStatus.Delivered || !i.DeliveredAt.HasValue ||
                i.DeliveredAt.Value > (_acceptanceClock?.ForSubOrder(i.SubOrderId) ?? DateTime.UtcNow)))
            throw new CommerceHubCheckoutException("PAYOUT_DELIVERY_NOT_CONFIRMED",
                "Every selected order item must have a recorded completed delivery before payout.", 409);
        if (items.Any(i => CustomerRefundRequestPolicy.IsAnyClaimWindowOpen(i.DeliveredAt, _refundSettings,
                _acceptanceClock?.ForSubOrder(i.SubOrderId) ?? DateTime.UtcNow)))
            throw new CommerceHubCheckoutException("PAYOUT_REFUND_WINDOW_OPEN",
                "Payout is held while an applicable Shopixy return or defect/non-conformity claim window remains open.", 409);
        if (await _financialSafetyGuardService.HasBlockingPayoutLockAsync(tenantId, ct))
            throw new CommerceHubCheckoutException("PAYOUT_BLOCKED_BY_REFUND_LOCK",
                "An unresolved refund or return continues to block payout after its initiation window closes.", 409);
    }
}
