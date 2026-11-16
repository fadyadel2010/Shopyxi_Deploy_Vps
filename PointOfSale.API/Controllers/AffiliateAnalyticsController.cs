using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Documents;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.API.Controllers;

/// <summary>
/// AF-8 + AF-24: Affiliate Analytics API.
///
/// ARCHITECTURE RULES:
/// 1. All analytics data from MongoDB projections — ZERO SQL for analytics
/// 2. Read-only — only GET endpoints
/// 3. No PII in responses
/// 4. No ledger access
/// 5. Affiliate can only see their own analytics
///
/// AF-24 ADDITIONS:
/// - GET /summary?range=30 — aggregated KPIs (clicks, orders, earnings, conversion rate)
/// - GET /trend?range=30 — daily time-series data
/// - GET /top-products?range=30 — top earning products
/// - GET /top-stores?range=30 — top earning stores
///
/// PostgreSQL is ONLY used to resolve UserId → AffiliateAccountId.
/// </summary>
[Authorize(Policy = "AffiliateOnly")]
[ApiController]
[Route("api/affiliate/analytics")]
public class AffiliateAnalyticsController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly MongoDbContext _mongoDbContext;

    public AffiliateAnalyticsController(
        ApplicationDbContext dbContext,
        MongoDbContext mongoDbContext)
    {
        _dbContext = dbContext;
        _mongoDbContext = mongoDbContext;
    }

    /// <summary>
    /// GET /api/affiliate/analytics/summary?range=30
    /// AF-24: Aggregated KPIs from daily analytics projections.
    /// Returns total clicks, orders, earnings, and conversion rate for the given range.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] int range = 30,
        CancellationToken ct = default,
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? productId = null)
    {
        var affiliateId = await ResolveAffiliateAccountId(ct);
        if (affiliateId == null) return NotFound(new { message = "Affiliate account not found" });

        range = Math.Clamp(range, 1, 365);

        Response.Headers.CacheControl = "no-store";
        var window = AffiliateConversionAnalyticsQuery.Window(range, DateTime.UtcNow);
        var sinceDate = window.Since.ToString("yyyy-MM-dd");
        var untilDate = window.Until.ToString("yyyy-MM-dd");

        var filter = Builders<AffiliateAnalyticsDailyDocument>.Filter.And(
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Eq(d => d.AffiliateAccountId, affiliateId.Value),
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Gte(d => d.Date, sinceDate),
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Lt(d => d.Date, untilDate)
        );
        if (tenantId.HasValue) filter &= Builders<AffiliateAnalyticsDailyDocument>.Filter.Eq(d => d.TenantId, tenantId.Value);

        var docs = await _mongoDbContext.AffiliateAnalyticsDaily
            .Find(filter)
            .ToListAsync(ct);

        // Preserve historical tracked-event totals (109 in retained evidence).
        // They are not deduplicated click records or visitors. Daily projections
        // do not contain per-product click events; expose unknown for that filter.
        long? totalClicks = productId.HasValue ? null : docs.Sum(d => d.Clicks);
        var productEarnings = docs.SelectMany(d => d.ProductEarnings).Where(p => p.ProductId == productId).ToList();
        var totalOrders = productId.HasValue ? productEarnings.Sum(p => p.Orders) : docs.Sum(d => d.Orders);
        var totalEarnings = productId.HasValue ? productEarnings.Sum(p => p.Earnings) : docs.Sum(d => d.Earnings);
        var commissionOrderRate = totalClicks > 0
            ? Math.Round((double)totalOrders / totalClicks.Value * 100, 2)
            : 0.0;
        var metrics = await new AffiliateConversionAnalyticsQuery(_mongoDbContext)
            .SummaryAsync(affiliateId.Value, range, tenantId, productId, ct);

        var result = new
        {
            totalClicks,
            totalOrders,
            totalEarnings,
            conversionRate = metrics.ConversionRate,
            clickRecords = metrics.ClickRecords,
            attributedOrders = metrics.AttributedOrders,
            convertedClicks = metrics.ConvertedClicks,
            commissionOrderRate,
            conversionRateDenominator = "clickRecords",
            conversionMilestone = "committed-attributed-order",
            timezone = "UTC",
            range
        };

        return Ok(result);
    }

    /// <summary>
    /// GET /api/affiliate/analytics/trend?range=30
    /// AF-24: Daily time-series data for charting.
    /// Returns per-day clicks, orders, and earnings.
    /// </summary>
    [HttpGet("trend")]
    public async Task<IActionResult> GetTrend(
        [FromQuery] int range = 30,
        CancellationToken ct = default,
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? productId = null)
    {
        var affiliateId = await ResolveAffiliateAccountId(ct);
        if (affiliateId == null) return NotFound(new { message = "Affiliate account not found" });

        range = Math.Clamp(range, 1, 365);
        Response.Headers.CacheControl = "no-store";
        var window = AffiliateConversionAnalyticsQuery.Window(range, DateTime.UtcNow);
        var sinceDate = window.Since.ToString("yyyy-MM-dd");
        var untilDate = window.Until.ToString("yyyy-MM-dd");

        var filter = Builders<AffiliateAnalyticsDailyDocument>.Filter.And(
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Eq(d => d.AffiliateAccountId, affiliateId.Value),
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Gte(d => d.Date, sinceDate),
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Lt(d => d.Date, untilDate)
        );
        if (tenantId.HasValue) filter &= Builders<AffiliateAnalyticsDailyDocument>.Filter.Eq(d => d.TenantId, tenantId.Value);

        var docs = await _mongoDbContext.AffiliateAnalyticsDaily
            .Find(filter)
            .SortBy(d => d.Date)
            .ToListAsync(ct);

        // Aggregate across tenants per day (affiliate sees ALL stores combined)
        var conversions = await new AffiliateConversionAnalyticsQuery(_mongoDbContext)
            .OrdersAsync(affiliateId.Value, range, tenantId, productId, ct);
        var dates = docs.Select(d => d.Date).Concat(conversions.Select(c => c.ConvertedAtUtc.ToString("yyyy-MM-dd"))).Distinct();
        var trend = dates
            .Select(date => new
            {
                date,
                // Daily click events have no per-product dimension. As in summary,
                // unknown is not zero and must never become another product's total.
                clicks = productId.HasValue ? (long?)null : docs.Where(d => d.Date == date).Sum(d => d.Clicks),
                orders = productId.HasValue
                    ? docs.Where(d => d.Date == date).SelectMany(d => d.ProductEarnings)
                        .Where(p => p.ProductId == productId).Sum(p => p.Orders)
                    : docs.Where(d => d.Date == date).Sum(d => d.Orders),
                earnings = productId.HasValue
                    ? docs.Where(d => d.Date == date).SelectMany(d => d.ProductEarnings)
                        .Where(p => p.ProductId == productId).Sum(p => p.Earnings)
                    : docs.Where(d => d.Date == date).Sum(d => d.Earnings),
                attributedOrders = conversions.Count(c => c.ConvertedAtUtc.ToString("yyyy-MM-dd") == date)
            })
            .OrderBy(d => d.date)
            .ToList();

        return Ok(trend);
    }

    /// <summary>
    /// GET /api/affiliate/analytics/top-products?range=30
    /// AF-24: Top earning products from daily analytics projections.
    /// Aggregates per-product earnings across all days and stores.
    /// </summary>
    [HttpGet("top-products")]
    public async Task<IActionResult> GetTopProducts(
        [FromQuery] int range = 30,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var affiliateId = await ResolveAffiliateAccountId(ct);
        if (affiliateId == null) return NotFound(new { message = "Affiliate account not found" });

        range = Math.Clamp(range, 1, 365);
        limit = Math.Clamp(limit, 1, 50);
        var window = AffiliateConversionAnalyticsQuery.Window(range, DateTime.UtcNow);
        var sinceDate = window.Since.ToString("yyyy-MM-dd");
        var untilDate = window.Until.ToString("yyyy-MM-dd");

        var filter = Builders<AffiliateAnalyticsDailyDocument>.Filter.And(
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Eq(d => d.AffiliateAccountId, affiliateId.Value),
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Gte(d => d.Date, sinceDate),
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Lt(d => d.Date, untilDate)
        );

        var docs = await _mongoDbContext.AffiliateAnalyticsDaily
            .Find(filter)
            .ToListAsync(ct);

        var topProducts = docs
            .SelectMany(d => d.ProductEarnings)
            .GroupBy(pe => pe.ProductId)
            .Select(g => new
            {
                productId = g.Key,
                name = g.First().ProductName,
                earnings = g.Sum(pe => pe.Earnings),
                orders = g.Sum(pe => pe.Orders)
            })
            .OrderByDescending(p => p.earnings)
            .Take(limit)
            .ToList();

        return Ok(topProducts);
    }

    /// <summary>
    /// GET /api/affiliate/analytics/top-stores?range=30
    /// AF-24: Top earning stores from daily analytics projections.
    /// Aggregates earnings per TenantId, resolves store names from MongoDB.
    /// </summary>
    [HttpGet("top-stores")]
    public async Task<IActionResult> GetTopStores(
        [FromQuery] int range = 30,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var affiliateId = await ResolveAffiliateAccountId(ct);
        if (affiliateId == null) return NotFound(new { message = "Affiliate account not found" });

        range = Math.Clamp(range, 1, 365);
        limit = Math.Clamp(limit, 1, 50);
        var window = AffiliateConversionAnalyticsQuery.Window(range, DateTime.UtcNow);
        var sinceDate = window.Since.ToString("yyyy-MM-dd");
        var untilDate = window.Until.ToString("yyyy-MM-dd");

        var filter = Builders<AffiliateAnalyticsDailyDocument>.Filter.And(
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Eq(d => d.AffiliateAccountId, affiliateId.Value),
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Gte(d => d.Date, sinceDate),
            Builders<AffiliateAnalyticsDailyDocument>.Filter.Lt(d => d.Date, untilDate)
        );

        var docs = await _mongoDbContext.AffiliateAnalyticsDaily
            .Find(filter)
            .ToListAsync(ct);

        var storeAggregates = docs
            .GroupBy(d => d.TenantId)
            .Select(g => new
            {
                tenantId = g.Key,
                earnings = g.Sum(d => d.Earnings),
                orders = g.Sum(d => d.Orders),
                clicks = g.Sum(d => d.Clicks)
            })
            .OrderByDescending(s => s.earnings)
            .Take(limit)
            .ToList();

        var tenantIds = storeAggregates.Select(s => s.tenantId).ToList();
        var storeNameMap = await _dbContext.StoreConfigs
            .AsNoTracking()
            .Where(config => tenantIds.Contains(config.TenantId))
            .Select(config => new { config.TenantId, config.StoreName })
            .ToDictionaryAsync(
                config => config.TenantId,
                config => string.IsNullOrWhiteSpace(config.StoreName) ? "Unknown Store" : config.StoreName,
                ct);

        var result = storeAggregates.Select(s => new
        {
            tenantId = s.tenantId,
            storeName = storeNameMap.GetValueOrDefault(s.tenantId, "Store"),
            earnings = s.earnings,
            orders = s.orders,
            clicks = s.clicks
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// GET /api/affiliate/analytics/clicks?page=1&pageSize=20
    /// AF-8: Paginated click history (kept for backward compatibility).
    /// </summary>
    [HttpGet("clicks")]
    public async Task<IActionResult> GetClicks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var affiliateId = await ResolveAffiliateAccountId(ct);
        if (affiliateId == null) return NotFound(new { message = "Affiliate account not found" });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var filter = Builders<AffiliateClickDocument>.Filter.Eq(d => d.AffiliateAccountId, affiliateId.Value);
        var sort = Builders<AffiliateClickDocument>.Sort.Descending(d => d.ClickedAtUtc);

        var total = await _mongoDbContext.AffiliateClicks.CountDocumentsAsync(filter, cancellationToken: ct);
        var clicks = await _mongoDbContext.AffiliateClicks
            .Find(filter)
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return Ok(new
        {
            page,
            pageSize,
            total,
            items = clicks.Select(c => new
            {
                clickId = c.Id,
                productId = c.ProductId,
                clickedAtUtc = c.ClickedAtUtc,
                converted = c.OrderId.HasValue,
                orderId = c.OrderId,
                convertedAtUtc = c.ConvertedAtUtc
            }).ToList()
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// AF-19B: Resolve AffiliateAccountId from JWT (no tenant context needed).
    /// </summary>
    private async Task<Guid?> ResolveAffiliateAccountId(CancellationToken ct)
    {
        var affiliateAccountClaim = User.FindFirst("affiliate_account_id")?.Value;
        if (!string.IsNullOrEmpty(affiliateAccountClaim) && Guid.TryParse(affiliateAccountClaim, out var affiliateId))
            return affiliateId;

        return await Task.FromResult<Guid?>(null);
    }
}
