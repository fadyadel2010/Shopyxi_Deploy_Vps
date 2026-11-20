using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using PointOfSale.API.DTOs.Analytics;
using PointOfSale.Core.Analytics;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Analytics;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents;

namespace PointOfSale.API.Services.Analytics;

public class StorefrontLiveMonitoringService : IStorefrontLiveMonitoringService
{
    private static readonly string[] LiveEventTypes =
    [
        nameof(AnalyticsEventType.PageView),
        nameof(AnalyticsEventType.AddToCart),
        nameof(AnalyticsEventType.BeginCheckout),
        nameof(AnalyticsEventType.PurchaseCompleted)
    ];

    private static readonly string[] RecentActivityEventTypes =
    [
        nameof(AnalyticsEventType.AddToCart),
        nameof(AnalyticsEventType.BeginCheckout),
        nameof(AnalyticsEventType.PurchaseCompleted)
    ];

    private readonly ApplicationDbContext _dbContext;
    private readonly IStorefrontAnalyticsAuthorityReader _authorityReader;
    private readonly IMongoCollection<AnalyticsEventDocument> _analyticsEvents;

    public StorefrontLiveMonitoringService(
        ApplicationDbContext dbContext,
        IStorefrontAnalyticsAuthorityReader authorityReader,
        IMongoDatabase mongoDatabase)
    {
        _dbContext = dbContext;
        _authorityReader = authorityReader;
        _analyticsEvents = mongoDatabase.GetCollection<AnalyticsEventDocument>("analytics_events");
    }

    public async Task<StorefrontLiveMonitoringResponse> GetLiveMonitoringAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        // BSON timestamps have millisecond precision. Use that same exact
        // half-open boundary for SQL activity and the returned window metadata.
        var now = DateTime.UtcNow;
        var windowEndUtc = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var windowStartUtc = windowEndUtc.AddMinutes(-5);

        var liveSummaryTask = GetLiveSummaryAsync(tenantId, windowStartUtc, windowEndUtc, cancellationToken);
        var recentOrdersTask = GetRecentOrdersAsync(tenantId, windowStartUtc, windowEndUtc, cancellationToken);

        await Task.WhenAll(liveSummaryTask, recentOrdersTask);

        var liveSummary = await liveSummaryTask;
        var recentOrders = await recentOrdersTask;

        return new StorefrontLiveMonitoringResponse
        {
            LiveVisitors = liveSummary.LiveVisitors,
            ActiveCarts = liveSummary.ActiveCarts,
            CheckoutInProgress = liveSummary.CheckoutInProgress,
            RecentOrders = recentOrders.Count,
            RecentActivity = BuildRecentActivity(liveSummary.RecentEvents, recentOrders.Items),
            GeneratedAtUtc = windowEndUtc,
            WindowStartUtc = windowStartUtc,
            WindowEndUtc = windowEndUtc
        };
    }

    private async Task<StorefrontLiveSummary> GetLiveSummaryAsync(
        Guid tenantId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken cancellationToken)
    {
        var match = new BsonDocument("$match", new BsonDocument
        {
            { "TenantId", tenantId.ToString() },
            {
                "OccurredAtUtc",
                new BsonDocument
                {
                    { "$gte", windowStartUtc },
                    { "$lt", windowEndUtc }
                }
            },
            { "EventType", new BsonDocument("$in", new BsonArray(LiveEventTypes)) },
            {
                "SessionId",
                new BsonDocument
                {
                    { "$type", "string" },
                    { "$ne", string.Empty }
                }
            }
        });

        var groupedSessions = new BsonDocument("$group", new BsonDocument
        {
            { "_id", "$SessionId" },
            {
                "pageViewCount",
                new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$EventType", nameof(AnalyticsEventType.PageView) }),
                        1,
                        0
                    }))
            },
            {
                "addToCartCount",
                new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$EventType", nameof(AnalyticsEventType.AddToCart) }),
                        1,
                        0
                    }))
            },
            {
                "beginCheckoutCount",
                new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$EventType", nameof(AnalyticsEventType.BeginCheckout) }),
                        1,
                        0
                    }))
            },
            {
                "purchaseCompletedCount",
                new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$EventType", nameof(AnalyticsEventType.PurchaseCompleted) }),
                        1,
                        0
                    }))
            }
        });

        var summarize = new BsonDocument("$group", new BsonDocument
        {
            { "_id", BsonNull.Value },
            {
                "liveVisitors",
                new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$gt", new BsonArray { "$pageViewCount", 0 }),
                        1,
                        0
                    }))
            },
            {
                "activeCarts",
                new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$gt", new BsonArray { "$addToCartCount", 0 }),
                        1,
                        0
                    }))
            },
            {
                "checkoutInProgress",
                new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$and", new BsonArray
                        {
                            new BsonDocument("$gt", new BsonArray { "$beginCheckoutCount", 0 }),
                            new BsonDocument("$eq", new BsonArray { "$purchaseCompletedCount", 0 })
                        }),
                        1,
                        0
                    }))
            }
        });

        var recentEventsFacet = new BsonDocument("$facet", new BsonDocument
        {
            {
                "summary",
                new BsonArray
                {
                    groupedSessions,
                    summarize
                }
            },
            {
                "recentEvents",
                new BsonArray
                {
                    new BsonDocument("$match", new BsonDocument
                    {
                        { "EventType", new BsonDocument("$in", new BsonArray(RecentActivityEventTypes)) }
                    }),
                    new BsonDocument("$sort", new BsonDocument("OccurredAtUtc", -1)),
                    new BsonDocument("$limit", 10),
                    new BsonDocument("$project", new BsonDocument
                    {
                        { "_id", 0 },
                        { "eventType", "$EventType" },
                        { "productName", "$ProductName" },
                        { "orderId", "$OrderId" },
                        { "occurredAtUtc", "$OccurredAtUtc" }
                    })
                }
            }
        });

        using var cursor = await _analyticsEvents.AggregateAsync<BsonDocument>(
            new[] { match, recentEventsFacet }, cancellationToken: cancellationToken);
        var result = await cursor.SingleOrDefaultAsync(cancellationToken);

        var summary = new StorefrontLiveSummary();
        if (result == null)
        {
            return summary;
        }

        if (result.TryGetValue("summary", out var summaryValue) && summaryValue.AsBsonArray.Count > 0)
        {
            var doc = summaryValue.AsBsonArray[0].AsBsonDocument;
            summary.LiveVisitors = doc.GetValue("liveVisitors", new BsonInt64(0)).ToInt64();
            summary.ActiveCarts = doc.GetValue("activeCarts", new BsonInt64(0)).ToInt64();
            summary.CheckoutInProgress = doc.GetValue("checkoutInProgress", new BsonInt64(0)).ToInt64();
        }

        if (result.TryGetValue("recentEvents", out var recentEventsValue))
        {
            summary.RecentEvents = recentEventsValue.AsBsonArray
                .Select(x => x.AsBsonDocument)
                .Select(doc => new LiveRecentEvent
                {
                    EventType = doc.GetValue("eventType", new BsonString(string.Empty)).AsString,
                    ProductName = doc.TryGetValue("productName", out var productName) && !productName.IsBsonNull
                        ? productName.AsString
                        : null,
                    OrderId = doc.TryGetValue("orderId", out var orderId) && !orderId.IsBsonNull
                        ? Guid.TryParse(orderId.AsString, out var parsedOrderId) ? parsedOrderId : null
                        : null,
                    OccurredAtUtc = doc.GetValue("occurredAtUtc").ToUniversalTime()
                })
                .ToList();
        }

        return summary;
    }

    private async Task<RecentOrderWindow> GetRecentOrdersAsync(
        Guid tenantId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken cancellationToken)
    {
        var activity = await _authorityReader.GetRecognizedOrderActivityAsync(
            tenantId,
            windowStartUtc,
            windowEndUtc,
            cancellationToken);
        var orderIds = activity.Keys.ToArray();
        if (orderIds.Length == 0)
        {
            return new(0, []);
        }

        var orders = await _dbContext.Orders.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o =>
                o.TenantId == tenantId &&
                orderIds.Contains(o.Id))
            .Select(o => new RecentOrderInfo
            {
                OrderId = o.Id,
                OrderNumber = o.OrderNumber,
            })
            .ToListAsync(cancellationToken);
        foreach (var order in orders) order.CreatedAtUtc = activity[order.OrderId];
        return new(orders.Count, orders.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.OrderId).Take(10).ToList());
    }

    private static List<StorefrontLiveActivityDto> BuildRecentActivity(
        IReadOnlyList<LiveRecentEvent> recentEvents,
        IReadOnlyList<RecentOrderInfo> recentOrders)
    {
        var orderLookup = recentOrders.ToDictionary(o => o.OrderId, o => o.OrderNumber);

        var activities = recentEvents
            .Where(evt => evt.EventType != nameof(AnalyticsEventType.PurchaseCompleted))
            .Select(evt => new StorefrontLiveActivityDto
            {
                Type = evt.EventType == nameof(AnalyticsEventType.PurchaseCompleted)
                    ? "OrderCompleted"
                    : evt.EventType,
                Product = evt.ProductName,
                OrderNumber = evt.OrderId.HasValue && orderLookup.TryGetValue(evt.OrderId.Value, out var orderNumber)
                    ? orderNumber
                    : null,
                OccurredAtUtc = evt.OccurredAtUtc
            })
            .Concat(recentOrders.Select(order => new StorefrontLiveActivityDto
            {
                Type = "OrderCompleted", OrderNumber = order.OrderNumber, OccurredAtUtc = order.CreatedAtUtc
            }))
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(10)
            .ToList();

        return activities;
    }

    private sealed class StorefrontLiveSummary
    {
        public long LiveVisitors { get; set; }
        public long ActiveCarts { get; set; }
        public long CheckoutInProgress { get; set; }
        public List<LiveRecentEvent> RecentEvents { get; set; } = new();
    }

    private sealed class LiveRecentEvent
    {
        public string EventType { get; set; } = string.Empty;
        public string? ProductName { get; set; }
        public Guid? OrderId { get; set; }
        public DateTime OccurredAtUtc { get; set; }
    }

    private sealed class RecentOrderInfo
    {
        public Guid OrderId { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
    private sealed record RecentOrderWindow(int Count, List<RecentOrderInfo> Items);
}
