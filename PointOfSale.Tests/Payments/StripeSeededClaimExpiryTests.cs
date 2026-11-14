using PointOfSale.API.Services.Payments.Stripe;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.API.Services.Checkout;
using System.Reflection;

namespace PointOfSale.Tests.Payments;

public sealed class StripeSeededClaimExpiryTests
{
    [Fact]
    public void OnlyPendingUnmaterializedStripeClaim_CanResumeWithoutAnExpiry()
    {
        var check = typeof(CheckoutService).GetMethod("CanRegenerateOnlineProviderHandoff", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool CanResume(PaymentIntent intent) => (bool)check.Invoke(null, [intent])!;
        Assert.True(CanResume(new() { Provider = PaymentProvider.Stripe, Status = PaymentIntentStatus.Pending }));
        Assert.False(CanResume(new() { Provider = PaymentProvider.Stripe, Status = PaymentIntentStatus.Authorized }));
        Assert.False(CanResume(new() { Provider = PaymentProvider.Stripe, Status = PaymentIntentStatus.Pending, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) }));
        Assert.False(CanResume(new() { Provider = PaymentProvider.Paymob, Status = PaymentIntentStatus.Pending }));
    }
    [Fact]
    public void MaterializedCheckoutClaim_GetsABoundedExpiry_ThatReplayDoesNotRenew()
    {
        var now = new DateTime(2026, 10, 26, 21, 0, 0, DateTimeKind.Utc);
        var intent = new PaymentIntent();
        StripePaymentService.EnsureStorefrontSessionExpiry(intent, now);
        var expiry = Assert.IsType<DateTime>(intent.ExpiresAtUtc);
        Assert.InRange(expiry, now.AddMinutes(1), now.AddHours(1));
        StripePaymentService.EnsureStorefrontSessionExpiry(intent, now.AddMinutes(10));
        Assert.Equal(expiry, intent.ExpiresAtUtc);
        StripePaymentService.EnsureStorefrontSessionExpiry(intent, expiry.AddMinutes(1));
        Assert.Equal(expiry, intent.ExpiresAtUtc); // Expired authority is not extended either.
    }
}
