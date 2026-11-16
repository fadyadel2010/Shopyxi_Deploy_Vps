using MassTransit;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Infrastructure.Consumers.Affiliate;

/// <summary>Durable order-created conversion; independent from payment and commission consumers.</summary>
public sealed class AffiliateOrderConversionConsumer(AffiliateConversionProjector projector)
    : IConsumer<AffiliateOrderPlacedEvent>
{
    public Task Consume(ConsumeContext<AffiliateOrderPlacedEvent> context)
    {
        var e = context.Message;
        if (!e.TenantId.HasValue || e.TenantId == Guid.Empty || e.StoreTenantId != e.TenantId ||
            e.EventVersion <= 0 || e.AffiliateAccountId == Guid.Empty)
            throw new InvalidOperationException("AFFILIATE_CONVERSION_EVENT_AUTHORITY_INVALID");
        return projector.ProjectAsync(e.TenantId.Value, e.OrderId, e.AffiliateAccountId, context.CancellationToken);
    }
}
