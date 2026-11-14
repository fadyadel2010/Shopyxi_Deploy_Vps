using Microsoft.Extensions.Logging;
using PointOfSale.API.DTOs.Admin;
using PointOfSale.API.DTOs.Storefront;
using PointOfSale.Infrastructure.Redis.Services;
using StackExchange.Redis;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Redis.Configuration;

namespace PointOfSale.API.Services.AI;

/// <summary>
/// Implementation of Commerce AI insights using Redis counters.
/// Phase 9: Admin Insights
/// </summary>
public class CommerceAiInsightsService : ICommerceAiInsightsService
{
    private readonly RedisConnectionFactory _redisFactory;
    private readonly ILogger<CommerceAiInsightsService> _logger;
    private readonly string _instanceName;

    private const int InsightTtlDays = 30;

    public CommerceAiInsightsService(
        RedisConnectionFactory redisFactory,
        ILogger<CommerceAiInsightsService> logger,
        IOptions<RedisSettings> settings)
    {
        _redisFactory = redisFactory;
        _logger = logger;
        _instanceName = settings.Value.InstanceName;
    }

    public async Task TrackEventAsync(Guid tenantId, CommerceAiEventType eventType)
    {
        try
        {
            var db = _redisFactory.GetDatabase();
            if (db == null) return;

            var key = GetKey(tenantId, eventType);
            
            // Increment counter
            await db.StringIncrementAsync(key);
            
            // Ensure TTL is set (sliding expiry on write)
            await db.KeyExpireAsync(key, TimeSpan.FromDays(InsightTtlDays));
            
            _logger.LogDebug("Commerce AI Event tracked | TenantId: {TenantId} | Type: {Type}", tenantId, eventType);
        }
        catch (Exception ex)
        {
            // Fire-and-forget: do not throw
            _logger.LogWarning(ex, "Failed to track Commerce AI event | TenantId: {TenantId}", tenantId);
        }
    }

    public async Task<CommerceAiInsightsDto> GetInsightsAsync(Guid tenantId)
    {
        var insights = new CommerceAiInsightsDto();

        try
        {
            var db = _redisFactory.GetDatabase();
            if (db == null) throw new InvalidOperationException("Activity counters unavailable");

            var chatKey = GetKey(tenantId, CommerceAiEventType.ChatOpened);
            var draftKey = GetKey(tenantId, CommerceAiEventType.OrderDraftCreated);

            // Fetch in parallel
            var chatTask = db.StringGetAsync(chatKey);
            var draftTask = db.StringGetAsync(draftKey);

            await Task.WhenAll(chatTask, draftTask);

            if (chatTask.Result.HasValue && long.TryParse(chatTask.Result.ToString(), out var chatCount))
            {
                insights.ChatOpened = chatCount;
            }

            if (draftTask.Result.HasValue && long.TryParse(draftTask.Result.ToString(), out var draftCount))
            {
                insights.DraftCreated = draftCount;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve Commerce AI insights | TenantId: {TenantId}", tenantId);
            throw;
        }

        return insights;
    }

    private string GetKey(Guid tenantId, CommerceAiEventType type)
    {
        // Match the configured runtime namespace, without granting wider Redis ACLs.
        return $"{_instanceName}:tenant:{tenantId:N}:commerce-ai:{type.ToString().ToLowerInvariant()}:v1";
    }
}
