using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.API.Configuration;
using PointOfSale.Infrastructure.Redis.Services;
using StackExchange.Redis;

namespace PointOfSale.API.Services.Platform;

/// <summary>
/// Redis-backed rate limiting service for public endpoints.
/// Phase 8.3: Tenant Registration Hardening
/// 
/// Provides sliding window rate limiting based on IP + endpoint.
/// Admission fails closed when its authoritative limiter is unavailable.
/// </summary>
public interface IRateLimitService
{
    /// <summary>
    /// Checks if a request should be allowed based on rate limits.
    /// </summary>
    /// <param name="clientIp">Client IP address</param>
    /// <param name="endpoint">Endpoint identifier (e.g., "register", "subdomain-check")</param>
    /// <param name="limit">Maximum requests allowed</param>
    /// <param name="windowSeconds">Time window in seconds</param>
    /// <returns>True if allowed, false if rate limited</returns>
    Task<RateLimitResult> CheckRateLimitAsync(string clientIp, string endpoint, int limit, int windowSeconds);
    
    /// <summary>
    /// Records a rate limit hit for tracking/logging.
    /// </summary>
    Task RecordRateLimitHitAsync(string clientIp, string endpoint);
    
    /// <summary>
    /// Checks if an IP is currently in abuse-triggered stricter mode.
    /// </summary>
    Task<bool> IsInAbuseThrottleAsync(string clientIp);
    
    /// <summary>
    /// Marks an IP as abusive (triggers stricter limits).
    /// </summary>
    Task MarkAsAbusiveAsync(string clientIp, TimeSpan duration);
}

public class RateLimitResult
{
    public bool Allowed { get; set; }
    public int CurrentCount { get; set; }
    public int Limit { get; set; }
    public int RetryAfterSeconds { get; set; }
    public string? Message { get; set; }
}

public class RateLimitService : IRateLimitService
{
    private readonly RedisConnectionFactory _redis;
    private readonly PlatformSecuritySettings _settings;
    private readonly ILogger<RateLimitService> _logger;
    
    private const string KeyPrefix = "ratelimit";
    private const string AbuseKeyPrefix = "abuse";

    // Admission and counting are one atomic operation. Rejected attempts do not
    // enlarge the set, so its cardinality remains bounded by the configured limit.
    private const string WindowScript = """
        local key = KEYS[1]
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        if ARGV[5] == '1' and redis.call('EXISTS', KEYS[2]) == 1 then
            limit = math.max(1, math.floor(limit * tonumber(ARGV[6])))
        end
        redis.call('ZREMRANGEBYSCORE', key, '-inf', now - window)
        local count = redis.call('ZCARD', key)
        if count >= limit then
            local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
            return {0, count, limit, math.max(1, math.ceil(tonumber(oldest[2]) + window - now))}
        end
        redis.call('ZADD', key, now, ARGV[4])
        redis.call('PEXPIRE', key, window * 1000 + 10000)
        return {1, count + 1, limit, 0}
        """;
    
    public RateLimitService(
        RedisConnectionFactory redis,
        IOptions<PlatformSecuritySettings> settings,
        ILogger<RateLimitService> logger)
    {
        _redis = redis;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<RateLimitResult> CheckRateLimitAsync(
        string clientIp, 
        string endpoint, 
        int limit, 
        int windowSeconds)
    {
        // If rate limiting is disabled, always allow
        if (!_settings.RateLimitEnabled)
        {
            return new RateLimitResult { Allowed = true, CurrentCount = 0, Limit = limit };
        }
        
        if (limit <= 0 || windowSeconds <= 0 ||
            !double.IsFinite(_settings.AbuseRateLimitMultiplier) ||
            _settings.AbuseRateLimitMultiplier <= 0 || _settings.AbuseRateLimitMultiplier > 1)
            return Unavailable(limit);

        var db = _redis.GetDatabase();
        
        // Public admission must not lose its abuse boundary during an outage.
        if (db == null)
        {
            _logger.LogWarning(
                "Rate limiting unavailable. Rejecting request. IP: {IP}, Endpoint: {Endpoint}",
                MaskIp(clientIp), endpoint);
            return Unavailable(limit);
        }
        
        var key = GetRateLimitKey(clientIp, endpoint);
        // Preserve the shipped seconds-based score format across the rollout.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        try
        {
            var reply = (RedisResult[])(await db.ScriptEvaluateAsync(WindowScript,
                new RedisKey[] { key, $"{AbuseKeyPrefix}:{HashIp(clientIp)}" },
                new RedisValue[] { now, windowSeconds, limit, Guid.NewGuid().ToString("N"),
                    _settings.AbuseDetectionEnabled ? "1" : "0", _settings.AbuseRateLimitMultiplier }))!;
            var allowed = (int)reply[0] == 1;
            var currentCount = (int)reply[1];
            var effectiveLimit = (int)reply[2];

            if (!allowed)
            {
                _logger.LogWarning(
                    "Rate limit triggered | IP: {IP} | Endpoint: {Endpoint} | Count: {Count}/{Limit} | Abusive: {Abusive}",
                    MaskIp(clientIp), endpoint, currentCount, effectiveLimit, effectiveLimit < limit);
                
                return new RateLimitResult
                {
                    Allowed = false,
                    CurrentCount = currentCount,
                    Limit = effectiveLimit,
                    RetryAfterSeconds = (int)reply[3],
                    Message = "Too many attempts. Please try again later."
                };
            }
            
            return new RateLimitResult
            {
                Allowed = true,
                CurrentCount = currentCount,
                Limit = effectiveLimit
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Rate limit check failed. Rejecting request. IP: {IP}, Endpoint: {Endpoint}",
                MaskIp(clientIp), endpoint);
            
            return Unavailable(limit);
        }
    }

    private static RateLimitResult Unavailable(int limit) => new()
    {
        Allowed = false,
        Limit = limit,
        RetryAfterSeconds = 60,
        Message = "Registration services are temporarily unavailable. Please try again later."
    };

    public async Task RecordRateLimitHitAsync(string clientIp, string endpoint)
    {
        var db = _redis.GetDatabase();
        if (db == null) return;
        
        try
        {
            var hitKey = $"{KeyPrefix}:hits:{endpoint}:{GetDayKey()}";
            await db.StringIncrementAsync(hitKey);
            await db.KeyExpireAsync(hitKey, TimeSpan.FromDays(1));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record rate limit hit");
        }
    }

    public async Task<bool> IsInAbuseThrottleAsync(string clientIp)
    {
        if (!_settings.AbuseDetectionEnabled)
        {
            return false;
        }
        
        var db = _redis.GetDatabase();
        if (db == null) return false;
        
        try
        {
            var abuseKey = $"{AbuseKeyPrefix}:{HashIp(clientIp)}";
            return await db.KeyExistsAsync(abuseKey);
        }
        catch
        {
            return false;
        }
    }

    public async Task MarkAsAbusiveAsync(string clientIp, TimeSpan duration)
    {
        var db = _redis.GetDatabase();
        if (db == null) return;
        
        try
        {
            var abuseKey = $"{AbuseKeyPrefix}:{HashIp(clientIp)}";
            await db.StringSetAsync(abuseKey, "1", duration);
            
            _logger.LogWarning(
                "IP marked as abusive | IP: {IP} | Duration: {Duration}",
                MaskIp(clientIp), duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark IP as abusive");
        }
    }

    private static string GetRateLimitKey(string clientIp, string endpoint)
    {
        // Hash IP for privacy
        return $"{KeyPrefix}:{endpoint}:{HashIp(clientIp)}";
    }

    private static string GetDayKey()
    {
        return DateTime.UtcNow.ToString("yyyyMMdd");
    }

    private static string HashIp(string ip)
    {
        // Use SHA256 for privacy-preserving IP storage
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(ip);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash)[..16]; // First 16 chars
    }

    private static string MaskIp(string ip)
    {
        // Mask last octet for logging
        var parts = ip.Split('.');
        if (parts.Length == 4)
        {
            return $"{parts[0]}.{parts[1]}.{parts[2]}.***";
        }
        return "***";
    }
}
