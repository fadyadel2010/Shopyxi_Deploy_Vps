using PointOfSale.Core.Entities;

namespace PointOfSale.API.Services.CommerceHub;

internal sealed record RefundFinancialBreakdown(
    decimal GrossCommissionReversalAmount,
    decimal CommissionReversalAmount,
    decimal PromoDiscountReversalAmount,
    decimal VendorEarningReversalAmount);

internal static class RefundFinancialCalculator
{
    public static RefundFinancialBreakdown CalculateItemBreakdown(
        CommerceHubSubOrderItem orderItem,
        int quantity,
        int refundedQuantitySoFar = 0)
    {
        var refundAmount = CalculateRefundAmount(orderItem, quantity);
        if (quantity <= 0 || orderItem.Quantity <= 0 || refundAmount <= 0m)
        {
            return new RefundFinancialBreakdown(0m, 0m, 0m, 0m);
        }

        if (refundedQuantitySoFar < 0 || (long)refundedQuantitySoFar + quantity > orderItem.Quantity)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Refund quantity exceeds the remaining item quantity.");
        }

        ValidateCommissionSplit(orderItem.PlatformCommissionAmount,
            orderItem.FinalPlatformCommissionAmount, orderItem.PromoDiscountAmount);
        return CalculateCumulativeBreakdown(
            orderItem.PlatformCommissionAmount, orderItem.PromoDiscountAmount,
            refundedQuantitySoFar / (decimal)orderItem.Quantity,
            (refundedQuantitySoFar + quantity) / (decimal)orderItem.Quantity, refundAmount);
    }

    public static RefundFinancialBreakdown CalculateSubOrderBreakdown(
        CommerceHubSubOrder subOrder,
        decimal refundedAmountSoFar,
        decimal refundAmount,
        bool isFullRefund)
    {
        if (refundAmount <= 0m)
        {
            return new RefundFinancialBreakdown(0m, 0m, 0m, 0m);
        }

        var grossCommission = RoundCurrency(Math.Max(0m, subOrder.PlatformCommissionAmount));
        var promoDiscount = RoundCurrency(Math.Max(0m, subOrder.PromoDiscountAmount));
        ValidateCommissionSplit(subOrder.PlatformCommissionAmount,
            subOrder.FinalPlatformCommissionAmount, subOrder.PromoDiscountAmount);

        if (grossCommission <= 0m)
        {
            return new RefundFinancialBreakdown(
                0m,
                0m,
                0m,
                RoundCurrency(refundAmount));
        }

        if (subOrder.Subtotal <= 0m)
        {
            return new RefundFinancialBreakdown(
                0m,
                0m,
                0m,
                RoundCurrency(refundAmount));
        }

        var commissionableRefundedBefore = Math.Clamp(refundedAmountSoFar - subOrder.Shipping, 0m, subOrder.Subtotal);
        var commissionableRefundedAfter = Math.Clamp(refundedAmountSoFar + refundAmount - subOrder.Shipping, 0m, subOrder.Subtotal);

        // A final refund reverses only the remaining commission, never the
        // original commission again. The existing shipping-first allocation
        // remains unchanged; the lifecycle flag cannot override money bounds.
        return CalculateCumulativeBreakdown(
            grossCommission, promoDiscount,
            commissionableRefundedBefore / subOrder.Subtotal,
            commissionableRefundedAfter / subOrder.Subtotal, refundAmount);
    }

    public static decimal CalculateCommissionReversal(
        CommerceHubSubOrderItem orderItem,
        int quantity)
    {
        return CalculateItemBreakdown(orderItem, quantity).CommissionReversalAmount;
    }

    public static decimal CalculateVendorEarningReversal(
        CommerceHubSubOrderItem orderItem,
        int quantity)
    {
        return CalculateItemBreakdown(orderItem, quantity).VendorEarningReversalAmount;
    }

    public static decimal CalculateRefundAmount(
        CommerceHubSubOrderItem orderItem,
        int quantity)
    {
        if (quantity <= 0)
        {
            return 0m;
        }

        return RoundCurrency(orderItem.UnitPrice * quantity);
    }

    public static decimal RoundCurrency(decimal amount) =>
        CommissionCalculator.RoundAmount(amount);

    private static RefundFinancialBreakdown CalculateCumulativeBreakdown(
        decimal grossCommission, decimal promoDiscount, decimal fractionBefore,
        decimal fractionAfter, decimal refundAmount)
    {
        var grossBefore = RoundCurrency(grossCommission * fractionBefore);
        var grossAfter = RoundCurrency(grossCommission * fractionAfter);
        // Allocate the already-rounded gross cents, not three independent
        // rounded amounts. This preserves gross = net + promo at every step
        // and recovers all residual cents on the last partial refund.
        var promoBefore = grossCommission == 0m ? 0m : RoundCurrency(promoDiscount * grossBefore / grossCommission);
        var promoAfter = grossCommission == 0m ? 0m : RoundCurrency(promoDiscount * grossAfter / grossCommission);
        var grossDelta = RoundCurrency(grossAfter - grossBefore);
        var promoDelta = RoundCurrency(promoAfter - promoBefore);
        return new RefundFinancialBreakdown(grossDelta,
            RoundCurrency(grossDelta - promoDelta), promoDelta,
            RoundCurrency(refundAmount - grossDelta));
    }

    private static void ValidateCommissionSplit(decimal grossAmount, decimal storedFinalAmount, decimal promoAmount)
    {
        if (grossAmount < 0m || storedFinalAmount < 0m || promoAmount < 0m || promoAmount > grossAmount ||
            (storedFinalAmount > 0m && RoundCurrency(storedFinalAmount + promoAmount) != RoundCurrency(grossAmount)))
        {
            throw new InvalidOperationException("Refund commission snapshot does not reconcile gross, net, and platform funding.");
        }
        // A stored zero is valid when fully platform-funded. For legacy
        // missing final fields, gross and frozen promo still define the split.
    }
}
