using MassTransit;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Infrastructure.Consumers.Affiliate;

/// <summary>Append-only click capture. Protected touch IDs deduplicate navigations;
/// legacy session/product deduplication remains supported. No financial mutations.</summary>
public class AffiliateLinkClickedConsumer(
    MongoDbContext mongoDbContext,
    ILogger<AffiliateLinkClickedConsumer> logger,
    AffiliateRankingService rankingService) : IConsumer<AffiliateLinkClickedEvent>
{
    public async Task Consume(ConsumeContext<AffiliateLinkClickedEvent> context)
    {
        var e = context.Message;
        if (!e.TenantId.HasValue || e.TenantId == Guid.Empty || e.AffiliateAccountId == Guid.Empty ||
            !e.ClickId.HasValue || e.ClickId.Value == Guid.Empty || string.IsNullOrWhiteSpace(e.ReferralCode))
            throw new InvalidOperationException("AFFILIATE_CLICK_IDENTITY_INVALID");
        var click = new AffiliateClickDocument
        {
            Id = e.ClickId.Value, TenantId = e.TenantId.Value,
            AffiliateAccountId = e.AffiliateAccountId, ReferralCode = e.ReferralCode,
            ProductId = e.ProductId, SessionId = e.ClickId.Value.ToString("N"),
            IpHash = e.IpHash, UserAgent = e.UserAgent, ClickedAtUtc = e.OccurredAtUtc
        };
        try { await mongoDbContext.AffiliateClicks.InsertOneAsync(click, cancellationToken: context.CancellationToken); }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            if (e.ClickId.HasValue)
            {
                var existing = await mongoDbContext.AffiliateClicks.Find(c => c.Id == e.ClickId.Value)
                    .SingleOrDefaultAsync(context.CancellationToken);
                if (existing == null || existing.TenantId != click.TenantId ||
                    existing.AffiliateAccountId != click.AffiliateAccountId ||
                    existing.ReferralCode != click.ReferralCode || existing.ProductId != click.ProductId)
                    throw new InvalidOperationException("AFFILIATE_CLICK_IDENTITY_CONFLICT", ex);
            }
            return; // Never replace click time, metadata, or an existing conversion.
        }
        logger.LogInformation("Affiliate click persisted | ClickId: {ClickId}", click.Id);
        if (click.ProductId.HasValue)
            await rankingService.IncrementClickSignalAsync(click.ProductId.Value, click.TenantId, context.CancellationToken);
    }
}

/// <summary>Compatibility for attribution producers outside checkout. Never guesses a click.
/// The normal checkout bridge consumes the durable AffiliateOrderPlacedEvent directly.</summary>
public class AffiliateClickConversionConsumer(AffiliateConversionProjector projector)
    : IConsumer<AffiliateAttributedEvent>
{
    public Task Consume(ConsumeContext<AffiliateAttributedEvent> context)
    {
        var e = context.Message;
        if (!e.TenantId.HasValue || e.TenantId == Guid.Empty || e.StoreTenantId != e.TenantId || e.EventVersion <= 0)
            throw new InvalidOperationException("AFFILIATE_CONVERSION_EVENT_AUTHORITY_INVALID");
        return projector.ProjectAsync(e.TenantId.Value, e.OrderId, e.AffiliateAccountId, context.CancellationToken);
    }
}
