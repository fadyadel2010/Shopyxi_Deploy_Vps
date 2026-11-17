using PointOfSale.API.Services.CommerceHub;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;

namespace PointOfSale.Tests.CommerceHub;

public sealed class RefundFinancialCalculatorTests
{
    [Fact]
    public void CumulativeRounding_AllCentSplitsMustConserveGrossNetPromoAndVendorValue()
    {
        for (var grossCents = 1; grossCents <= 100; grossCents++)
        for (var promoCents = 0; promoCents <= grossCents; promoCents++)
        {
            var item = new CommerceHubSubOrderItem
            {
                Quantity = 3, UnitPrice = 1m, PlatformCommissionAmount = grossCents / 100m,
                FinalPlatformCommissionAmount = (grossCents - promoCents) / 100m,
                PromoDiscountAmount = promoCents / 100m
            };
            var parts = Enumerable.Range(0, 3)
                .Select(before => RefundFinancialCalculator.CalculateItemBreakdown(item, 1, before)).ToList();
            Assert.All(parts, part =>
            {
                Assert.Equal(part.GrossCommissionReversalAmount,
                    part.CommissionReversalAmount + part.PromoDiscountReversalAmount);
                Assert.True(part.CommissionReversalAmount >= 0m && part.PromoDiscountReversalAmount >= 0m);
                Assert.Equal(1m, part.GrossCommissionReversalAmount + part.VendorEarningReversalAmount);
            });
            Assert.Equal(item.PlatformCommissionAmount, parts.Sum(part => part.GrossCommissionReversalAmount));
            Assert.Equal(item.FinalPlatformCommissionAmount, parts.Sum(part => part.CommissionReversalAmount));
            Assert.Equal(item.PromoDiscountAmount, parts.Sum(part => part.PromoDiscountReversalAmount));
        }
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(10, 11, 0)]
    [InlineData(10, 7, 4)]
    [InlineData(10, 0, 11)]
    public void InvalidFrozenCommissionSplit_MustFailClosed(int gross, int net, int promo)
    {
        var item = new CommerceHubSubOrderItem
        {
            Quantity = 1, UnitPrice = 100m, PlatformCommissionAmount = gross,
            FinalPlatformCommissionAmount = net, PromoDiscountAmount = promo
        };
        Assert.Throws<InvalidOperationException>(() => RefundFinancialCalculator.CalculateItemBreakdown(item, 1));
    }

    [Fact]
    public void FullyFundedCommission_MustNotFallBackFromValidZeroToGross()
    {
        var item = new CommerceHubSubOrderItem
        {
            Quantity = 1, UnitPrice = 100m, PlatformCommissionAmount = 10m,
            FinalPlatformCommissionAmount = 0m, PromoDiscountAmount = 10m
        };
        var result = RefundFinancialCalculator.CalculateItemBreakdown(item, 1);
        Assert.Equal(10m, result.GrossCommissionReversalAmount);
        Assert.Equal(0m, result.CommissionReversalAmount);
        Assert.Equal(10m, result.PromoDiscountReversalAmount);
        Assert.Equal(90m, result.VendorEarningReversalAmount);
    }

    [Fact]
    public void FinalRefundAfterPartial_MustOnlyReverseRemainingCommission()
    {
        var order = new CommerceHubSubOrder
        {
            Subtotal = 100m, Shipping = 10m, Total = 110m,
            PlatformCommissionAmount = 10m, FinalPlatformCommissionAmount = 7m,
            PromoDiscountAmount = 3m
        };
        var first = RefundFinancialCalculator.CalculateSubOrderBreakdown(order, 0m, 60m, false);
        var final = RefundFinancialCalculator.CalculateSubOrderBreakdown(order, 60m, 50m, true);
        Assert.Equal(10m, first.GrossCommissionReversalAmount + final.GrossCommissionReversalAmount);
        Assert.Equal(7m, first.CommissionReversalAmount + final.CommissionReversalAmount);
        Assert.Equal(3m, first.PromoDiscountReversalAmount + final.PromoDiscountReversalAmount);
        Assert.Equal(100m, first.VendorEarningReversalAmount + final.VendorEarningReversalAmount);
    }

    [Fact]
    public void RepeatedItemRefunds_MustConserveCentsAndCommissionSplit()
    {
        var item = new CommerceHubSubOrderItem
        {
            Quantity = 3, UnitPrice = 1m, PlatformCommissionAmount = 0.10m,
            FinalPlatformCommissionAmount = 0.05m, PromoDiscountAmount = 0.05m
        };
        var parts = Enumerable.Range(0, 3)
            .Select(before => RefundFinancialCalculator.CalculateItemBreakdown(item, 1, before)).ToList();
        Assert.All(parts, part =>
            Assert.Equal(part.GrossCommissionReversalAmount, part.CommissionReversalAmount + part.PromoDiscountReversalAmount));
        Assert.Equal(0.10m, parts.Sum(part => part.GrossCommissionReversalAmount));
        Assert.Equal(0.05m, parts.Sum(part => part.CommissionReversalAmount));
        Assert.Equal(0.05m, parts.Sum(part => part.PromoDiscountReversalAmount));
        Assert.Equal(2.90m, parts.Sum(part => part.VendorEarningReversalAmount));
        Assert.Throws<ArgumentOutOfRangeException>(() => RefundFinancialCalculator.CalculateItemBreakdown(item, 1, 3));
    }

    [Fact]
    public void CalculateItemBreakdown_ShouldProratePromoDiscountByRefundedQuantity()
    {
        var item = new CommerceHubSubOrderItem
        {
            Id = Guid.NewGuid(),
            Quantity = 5,
            UnitPrice = 20m,
            Total = 100m,
            PlatformCommissionAmount = 10m,
            FinalPlatformCommissionAmount = 7.5m,
            PromoDiscountAmount = 2.5m
        };

        var breakdown = RefundFinancialCalculator.CalculateItemBreakdown(item, quantity: 2);

        Assert.Equal(4m, breakdown.GrossCommissionReversalAmount);
        Assert.Equal(3m, breakdown.CommissionReversalAmount);
        Assert.Equal(1m, breakdown.PromoDiscountReversalAmount);
        Assert.Equal(36m, breakdown.VendorEarningReversalAmount);
    }

    [Fact]
    public void CalculateSubOrderBreakdown_ShouldReversePromoDiscountProportionallyAcrossPartialRefunds()
    {
        var subOrder = new CommerceHubSubOrder
        {
            Id = Guid.NewGuid(),
            OrderNumber = "CH-REF-PROP-1-A",
            Currency = "EGP",
            Subtotal = 100m,
            Shipping = 0m,
            Total = 100m,
            PlatformCommissionAmount = 10m,
            FinalPlatformCommissionAmount = 7m,
            PromoDiscountAmount = 3m,
            VendorEarningAmount = 90m,
            Status = OrderStatus.Pending,
            PaymentStatus = PaymentStatus.Collected
        };

        var first = RefundFinancialCalculator.CalculateSubOrderBreakdown(
            subOrder,
            refundedAmountSoFar: 0m,
            refundAmount: 30m,
            isFullRefund: false);
        var second = RefundFinancialCalculator.CalculateSubOrderBreakdown(
            subOrder,
            refundedAmountSoFar: 30m,
            refundAmount: 20m,
            isFullRefund: false);

        Assert.Equal(3m, first.GrossCommissionReversalAmount);
        Assert.Equal(2.1m, first.CommissionReversalAmount);
        Assert.Equal(0.9m, first.PromoDiscountReversalAmount);
        Assert.Equal(27m, first.VendorEarningReversalAmount);

        Assert.Equal(2m, second.GrossCommissionReversalAmount);
        Assert.Equal(1.4m, second.CommissionReversalAmount);
        Assert.Equal(0.6m, second.PromoDiscountReversalAmount);
        Assert.Equal(18m, second.VendorEarningReversalAmount);
    }
}
