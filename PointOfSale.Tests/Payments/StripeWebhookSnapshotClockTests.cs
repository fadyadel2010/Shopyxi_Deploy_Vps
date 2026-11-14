using PointOfSale.API.Services.Payments.Stripe;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;

namespace PointOfSale.Tests.Payments;

public sealed class StripeWebhookSnapshotClockTests
{
    [Fact]
    public void DelayedProviderEvidence_IsNotComparedAgainstAReconstructedProcessingTime()
    {
        var created = DateTime.UtcNow.AddMinutes(-2);
        var providerTime = DateTime.UtcNow.AddMinutes(-1);
        var intent = new PaymentIntent { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), OrderId = Guid.NewGuid(),
            Amount = 35m, Currency = "EGP", PaymentReference = "P515-clock", Status = PaymentIntentStatus.Pending, CreatedAt = created };
        var request = new StripeWebhookRuntimeAdmissionRequest(intent.TenantId,
            new Stripe.Event { Id = "evt_clock", Type = "payment_intent.succeeded", Created = providerTime }, "", "", "", "clock-test");
        var inputs = StripeWebhookRuntimeIntegrationService.BuildRuntimeInputs(request, intent);
        Assert.Equal(new DateTimeOffset(created, TimeSpan.Zero), inputs.Session.LastUpdatedUtc);
        Assert.True(inputs.Session.LastUpdatedUtc < new DateTimeOffset(providerTime, TimeSpan.Zero));
        Assert.True(inputs.Context.ExecutionTimestamp > new DateTimeOffset(providerTime, TimeSpan.Zero));
    }
}
