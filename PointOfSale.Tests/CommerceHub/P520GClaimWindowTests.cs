using PointOfSale.API.Configuration;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Infrastructure.Data;
using PointOfSale.API.Services.CommerceHub.Finance;

namespace PointOfSale.Tests.CommerceHub;

public class P520GClaimWindowTests
{
    private static readonly DateTime Delivered = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(14, 0, RefundReasonCode.Other, true)]
    [InlineData(14, 1, RefundReasonCode.Other, false)]
    [InlineData(15, 0, RefundReasonCode.Other, false)]
    [InlineData(15, 0, RefundReasonCode.DamagedItem, true)]
    [InlineData(15, 0, RefundReasonCode.NotAsDescribed, true)]
    [InlineData(15, 0, RefundReasonCode.WrongItem, true)]
    [InlineData(15, 0, RefundReasonCode.MissingParts, true)]
    [InlineData(15, 0, RefundReasonCode.QualityIssue, true)]
    [InlineData(30, 0, RefundReasonCode.DamagedItem, true)]
    [InlineData(30, 1, RefundReasonCode.DamagedItem, false)]
    [InlineData(31, 0, RefundReasonCode.NotAsDescribed, false)]
    public void Exact_reason_specific_boundaries(int days, int ticks, RefundReasonCode reason, bool allowed)
    {
        var now = Delivered.AddDays(days).AddTicks(ticks);
        var eligibility = CustomerRefundRequestPolicy.Evaluate(CommerceHubOrderItemStatus.Delivered,
            Delivered, 2, [], new RefundSettings { RefundWindowDays = 7 }, now, reason);
        Assert.Equal(allowed, eligibility.CanRefund);
    }

    [Theory]
    [InlineData(14, 0, true)]
    [InlineData(15, 0, true)]
    [InlineData(30, 0, true)]
    [InlineData(30, 1, false)]
    public void Payout_hold_uses_longest_applicable_window(int days, int ticks, bool held) =>
        Assert.Equal(held, CustomerRefundRequestPolicy.IsAnyClaimWindowOpen(Delivered, new(), Delivered.AddDays(days).AddTicks(ticks)));

    [Theory]
    [InlineData(45, 30, 45, 45)]
    [InlineData(14, 60, 14, 60)]
    [InlineData(0, 0, 14, 30)]
    [InlineData(7, 7, 14, 30)]
    public void Configuration_cannot_shorten_rights_and_preserves_longer_rights(int standard, int defect, int expectedStandard, int expectedDefect)
    {
        var settings = new RefundSettings { RefundWindowDays = standard, DefectClaimWindowDays = defect };
        Assert.Equal(expectedStandard, CustomerRefundRequestPolicy.StandardWindowDays(settings));
        Assert.Equal(expectedDefect, CustomerRefundRequestPolicy.DefectWindowDays(settings));
        Assert.True(CustomerRefundRequestPolicy.IsAnyClaimWindowOpen(Delivered, settings, Delivered.AddDays(expectedDefect)));
        Assert.False(CustomerRefundRequestPolicy.IsAnyClaimWindowOpen(Delivered, settings, Delivered.AddDays(expectedDefect).AddTicks(1)));
    }

    [Fact]
    public void No_delivery_or_future_delivery_cannot_create_a_claim()
    {
        Assert.False(CustomerRefundRequestPolicy.IsAnyClaimWindowOpen(null, new(), Delivered));
        Assert.False(CustomerRefundRequestPolicy.IsAnyClaimWindowOpen(Delivered.AddDays(1), new(), Delivered));
        Assert.False(CustomerRefundRequestPolicy.Evaluate(CommerceHubOrderItemStatus.Shipped, Delivered, 2, [], new(), Delivered).CanRefund);
    }

    [Fact]
    public void Expiry_does_not_release_existing_claim_quantity_or_mutate_pending_state()
    {
        var pending = new CommerceHubRefundRequest { Quantity = 1, Status = CommerceHubRefundRequestStatus.Requested };
        var eligibility = CustomerRefundRequestPolicy.Evaluate(CommerceHubOrderItemStatus.Delivered,
            Delivered, 2, [pending], new(), Delivered.AddDays(31));
        Assert.False(eligibility.CanRefund); // new claim refused, existing claim untouched
        Assert.Equal(1, CustomerRefundRequestPolicy.CalculateReservedQuantity([pending]));
        Assert.Equal(CommerceHubRefundRequestStatus.Requested, pending.Status);
    }

    [Theory]
    [InlineData(CommerceHubRefundRequestStatus.Requested, true)]
    [InlineData(CommerceHubRefundRequestStatus.Failed, true)]
    [InlineData(CommerceHubRefundRequestStatus.WaitingForReturn, true)]
    [InlineData(CommerceHubRefundRequestStatus.Paid, false)]
    [InlineData(CommerceHubRefundRequestStatus.Rejected, false)]
    public async Task Pending_and_retryable_claims_hold_payout_even_after_window_expiry(CommerceHubRefundRequestStatus status, bool held)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var vendor = Guid.NewGuid();
        db.CommerceHubRefundRequests.Add(new() { Id = Guid.NewGuid(), VendorId = vendor, Status = status,
            Quantity = 1, CreatedAt = Delivered });
        await db.SaveChangesAsync();
        var guard = new CommerceHubFinancialSafetyGuardService(db);
        if (status == CommerceHubRefundRequestStatus.Failed)
            Assert.Contains(status, (CommerceHubRefundRequestStatus[])typeof(CommerceHubFinancialSafetyGuardService)
                .GetField("BlockingRefundStatuses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!);
        Assert.Equal(held, await guard.HasBlockingPayoutLockAsync(vendor));
        Assert.Equal(status, (await db.CommerceHubRefundRequests.SingleAsync()).Status);
        Assert.Empty(db.CommerceHubFinancialTransactions);
    }
}
