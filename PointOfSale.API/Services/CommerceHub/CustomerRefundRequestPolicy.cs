using PointOfSale.API.Configuration;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;

namespace PointOfSale.API.Services.CommerceHub;

internal readonly record struct CustomerRefundEligibility(
    int MaxRefundableQuantity,
    bool CanRefund,
    string? DisabledReason);

internal static class CustomerRefundRequestPolicy
{
    public const string PolicyVersion = "SHOPIXY-MARKETPLACE-14-30-20261029";
    public const string ItemNotDeliveredDisabledReason = "Refund not available: Item not delivered";
    public const string RefundWindowExpiredDisabledReason = "Refund window expired";
    public const string MaximumRefundableQuantityReachedDisabledReason = "Maximum refundable quantity reached";

    public static bool CountsAgainstAvailableQuantity(CommerceHubRefundRequestStatus status) =>
        status is not (CommerceHubRefundRequestStatus.Rejected or CommerceHubRefundRequestStatus.Cancelled);

    public static int CalculateReservedQuantity(IEnumerable<CommerceHubRefundRequest> refundRequests) =>
        refundRequests
            .Where(refundRequest => !refundRequest.IsDeleted && CountsAgainstAvailableQuantity(refundRequest.Status))
            .Sum(refundRequest => refundRequest.Quantity);

    public static CustomerRefundEligibility Evaluate(
        CommerceHubOrderItemStatus itemStatus,
        DateTime? deliveredAt,
        int purchasedQuantity,
        IEnumerable<CommerceHubRefundRequest> refundRequests,
        RefundSettings settings,
        DateTime nowUtc,
        RefundReasonCode? reasonCode = null)
    {
        if (itemStatus != CommerceHubOrderItemStatus.Delivered)
        {
            return new CustomerRefundEligibility(0, false, ItemNotDeliveredDisabledReason);
        }

        // A projection without a selected reason must retain the action while any
        // ordinary claim is still possible. Commands always supply the explicit reason.
        var windowOpen = reasonCode.HasValue
            ? IsWithinRefundWindow(deliveredAt, settings, nowUtc, reasonCode.Value)
            : IsAnyClaimWindowOpen(deliveredAt, settings, nowUtc);
        if (!windowOpen)
        {
            return new CustomerRefundEligibility(0, false, RefundWindowExpiredDisabledReason);
        }

        var reservedQuantity = CalculateReservedQuantity(refundRequests);
        var maxRefundableQuantity = Math.Max(0, purchasedQuantity - reservedQuantity);

        if (maxRefundableQuantity <= 0)
        {
            return new CustomerRefundEligibility(
                0,
                false,
                MaximumRefundableQuantityReachedDisabledReason);
        }

        return new CustomerRefundEligibility(maxRefundableQuantity, true, null);
    }

    public static bool IsDefectOrNonConformity(RefundReasonCode reasonCode) => reasonCode is
        RefundReasonCode.DamagedItem or RefundReasonCode.NotAsDescribed or RefundReasonCode.WrongItem or
        RefundReasonCode.MissingParts or RefundReasonCode.QualityIssue;

    public static int StandardWindowDays(RefundSettings settings) => Math.Max(14, settings.RefundWindowDays);
    public static int DefectWindowDays(RefundSettings settings) =>
        Math.Max(30, Math.Max(StandardWindowDays(settings), settings.DefectClaimWindowDays));

    public static bool IsAnyClaimWindowOpen(DateTime? deliveredAt, RefundSettings settings, DateTime nowUtc) =>
        IsOpen(deliveredAt, DefectWindowDays(settings), nowUtc);

    public static bool IsWithinRefundWindow(DateTime? deliveredAt, RefundSettings settings, DateTime nowUtc,
        RefundReasonCode reasonCode = RefundReasonCode.Other) =>
        IsOpen(deliveredAt, IsDefectOrNonConformity(reasonCode) ? DefectWindowDays(settings) : StandardWindowDays(settings), nowUtc);

    private static bool IsOpen(DateTime? deliveredAt, int days, DateTime nowUtc)
    {
        if (!deliveredAt.HasValue)
        {
            return false;
        }

        return deliveredAt.Value <= nowUtc &&
               (decimal)(nowUtc - deliveredAt.Value).Ticks <= (decimal)days * TimeSpan.TicksPerDay;
    }
}
