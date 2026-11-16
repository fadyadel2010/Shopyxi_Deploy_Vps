using MongoDB.Driver;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Services;

namespace PointOfSale.Infrastructure.Services.Affiliate;

public sealed record AffiliateConversionMetrics(long ClickRecords, long AttributedOrders,
    long ConvertedClicks, double? ConversionRate);

/// <summary>Only persisted analytics; no SQL/financial queries, event increments or visitor inference.</summary>
public sealed class AffiliateConversionAnalyticsQuery(MongoDbContext mongo)
{
    public static (DateTime Since, DateTime Until) Window(int range, DateTime nowUtc) =>
        (nowUtc.Date.AddDays(1 - Math.Clamp(range, 1, 365)), nowUtc.Date.AddDays(1));

    public async Task<List<AffiliateOrderConversionDocument>> OrdersAsync(Guid affiliateId,
        int range, Guid? tenantId = null, Guid? productId = null, CancellationToken ct = default)
    {
        var (since, until) = Window(range, DateTime.UtcNow);
        var f = Builders<AffiliateOrderConversionDocument>.Filter;
        var filter = f.And(f.Eq(d => d.AffiliateAccountId, affiliateId),
            f.Gte(d => d.ConvertedAtUtc, since), f.Lt(d => d.ConvertedAtUtc, until));
        if (tenantId.HasValue) filter &= f.Eq(d => d.TenantId, tenantId);
        if (productId.HasValue) filter &= f.Eq(d => d.ProductId, productId);
        return await mongo.Database.GetCollection<AffiliateOrderConversionDocument>(AffiliateConversionProjector.CollectionName)
            .Find(filter).ToListAsync(ct);
    }

    public async Task<AffiliateConversionMetrics> SummaryAsync(Guid affiliateId, int range,
        Guid? tenantId = null, Guid? productId = null, CancellationToken ct = default)
    {
        var (since, until) = Window(range, DateTime.UtcNow);
        var f = Builders<AffiliateClickDocument>.Filter;
        var filter = f.And(f.Eq(d => d.AffiliateAccountId, affiliateId),
            f.Gte(d => d.ClickedAtUtc, since), f.Lt(d => d.ClickedAtUtc, until));
        if (tenantId.HasValue) filter &= f.Eq(d => d.TenantId, tenantId);
        if (productId.HasValue) filter &= f.Eq(d => d.ProductId, productId);
        var clicks = await mongo.AffiliateClicks.CountDocumentsAsync(filter, cancellationToken: ct);
        var orders = await OrdersAsync(affiliateId, range, tenantId, productId, ct);
        return new(clicks, orders.Count, orders.LongCount(o => o.ClickId.HasValue),
            clicks == 0 ? null : Math.Round(orders.Count * 100.0 / clicks, 2));
    }
}
