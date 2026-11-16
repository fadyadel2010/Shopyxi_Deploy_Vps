using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Services;

namespace PointOfSale.Infrastructure.Services.Affiliate;

/// <summary>Shared normal-delivery/replay/repair path. Reads SQL authority, writes only analytics.
/// Exceptions must reach the existing durable broker retry/error queue. No success on queueing.</summary>
public sealed class AffiliateConversionProjector(ApplicationDbContext sql, MongoDbContext mongo,
    AffiliateRankingService ranking)
{
    public const string CollectionName = "affiliate_order_conversions";

    public async Task ProjectAsync(Guid tenantId, Guid orderId, Guid? expectedAffiliateId = null,
        CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || orderId == Guid.Empty)
            throw new InvalidOperationException("AFFILIATE_CONVERSION_AUTHORITY_REQUIRED");
        // A separate consumer scope can only see committed SQL rows. No writes,
        // financial services, status setters, or current-offer admission here.
        var order = await sql.Orders.AsNoTracking().IgnoreQueryFilters()
            .SingleOrDefaultAsync(o => o.Id == orderId && o.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("AFFILIATE_CONVERSION_ORDER_NOT_FOUND");
        var attribution = await sql.AffiliateAttributions.AsNoTracking().IgnoreQueryFilters()
            .SingleOrDefaultAsync(a => a.OrderId == orderId && a.TenantId == tenantId, ct);
        if (attribution == null)
        {
            if (order.AffiliateAccountId.HasValue || expectedAffiliateId.HasValue)
                throw new InvalidOperationException("AFFILIATE_CONVERSION_ATTRIBUTION_MISSING");
            return; // An ordinary order is never a conversion.
        }
        if (attribution.AffiliateAccountId != order.AffiliateAccountId ||
            attribution.ReferralCode != order.AffiliateReferralCode ||
            attribution.PromotionLinkId != order.PromotionLinkId ||
            (expectedAffiliateId.HasValue && expectedAffiliateId != attribution.AffiliateAccountId))
            throw new InvalidOperationException("AFFILIATE_CONVERSION_AUTHORITY_MISMATCH");

        Guid? productId = attribution.ReferralProductId;
        if (attribution.PromotionLinkId.HasValue)
        {
            var link = await sql.AffiliatePromotionLinks.AsNoTracking().IgnoreQueryFilters()
                .SingleOrDefaultAsync(l => l.Id == attribution.PromotionLinkId && l.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException("AFFILIATE_CONVERSION_LINK_MISMATCH");
            if (link.ReferralCode != attribution.ReferralCode ||
                (link.AffiliateAccountId.HasValue && link.AffiliateAccountId != attribution.AffiliateAccountId) ||
                (attribution.ReferralClickId.HasValue && productId != link.ProductId))
                throw new InvalidOperationException("AFFILIATE_CONVERSION_PRODUCT_MISMATCH");
            productId = link.ProductId;
            if (!await sql.OrderItems.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(i => i.OrderId == orderId && i.ProductId == productId, ct))
                throw new InvalidOperationException("AFFILIATE_CONVERSION_ORDER_PRODUCT_MISMATCH");
        }

        // BSON date precision is milliseconds. Replays use the same persisted
        // order-created milestone, never processing time or refund/payment time.
        var created = order.CreatedAt;
        if (created == default) throw new InvalidOperationException("AFFILIATE_CONVERSION_TIME_MISSING");
        var convertedAt = new DateTime(created.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
        var conversions = mongo.Database.GetCollection<AffiliateOrderConversionDocument>(CollectionName);
        var projected = new AffiliateOrderConversionDocument
        {
            OrderId = orderId, TenantId = tenantId, AttributionId = attribution.Id,
            AffiliateAccountId = attribution.AffiliateAccountId, ClickId = attribution.ReferralClickId,
            ProductId = productId, ConvertedAtUtc = convertedAt
        };

        if (attribution.ReferralClickId.HasValue)
        {
            var f = Builders<AffiliateClickDocument>.Filter;
            if (await mongo.AffiliateClicks.Find(f.And(f.Eq(c => c.TenantId, tenantId),
                f.Eq(c => c.OrderId, orderId), f.Ne(c => c.Id, attribution.ReferralClickId.Value))).AnyAsync(ct))
                throw new InvalidOperationException("AFFILIATE_CONVERSION_ORDER_CLICK_CONFLICT");
            var identity = f.And(f.Eq(c => c.Id, attribution.ReferralClickId.Value),
                f.Eq(c => c.TenantId, tenantId), f.Eq(c => c.AffiliateAccountId, attribution.AffiliateAccountId),
                f.Eq(c => c.ReferralCode, attribution.ReferralCode), f.Eq(c => c.ProductId, attribution.ReferralProductId));
            var state = f.Or(f.And(f.Eq(c => c.OrderId, null), f.Eq(c => c.ConvertedAtUtc, null)),
                f.And(f.Eq(c => c.OrderId, orderId), f.Eq(c => c.ConvertedAtUtc, convertedAt)));
            var result = await mongo.AffiliateClicks.UpdateOneAsync(f.And(identity, state),
                Builders<AffiliateClickDocument>.Update.Set(c => c.OrderId, orderId)
                    .Set(c => c.ConvertedAtUtc, convertedAt), cancellationToken: ct);
            if (result.MatchedCount != 1)
                throw new InvalidOperationException("AFFILIATE_CONVERSION_CLICK_MISSING_OR_CONFLICT");
        }

        // Insert-only order ID is a permanent dedupe key. A timeout after either
        // write is safely retried; the report only counts persisted documents.
        try { await conversions.InsertOneAsync(projected, cancellationToken: ct); }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await conversions.Find(c => c.OrderId == orderId).SingleAsync(ct);
            if (existing.TenantId != projected.TenantId || existing.AffiliateAccountId != projected.AffiliateAccountId ||
                existing.AttributionId != projected.AttributionId || existing.ClickId != projected.ClickId ||
                existing.ProductId != projected.ProductId || existing.ConvertedAtUtc != projected.ConvertedAtUtc)
                throw new InvalidOperationException("AFFILIATE_CONVERSION_PROJECTION_CONFLICT", ex);
        }
        if (productId.HasValue)
            await ranking.ApplyConversionSignalOnceAsync(productId.Value, tenantId, orderId, convertedAt, ct);
    }
}
