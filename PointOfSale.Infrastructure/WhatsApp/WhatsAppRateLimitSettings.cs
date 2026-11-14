namespace PointOfSale.Infrastructure.WhatsApp;

public class WhatsAppRateLimitSettings
{
    /// <summary>
    /// Maximum messages per tenant per day (all accounts combined)
    /// </summary>
    public int TenantDailyLimit { get; set; } = 1000;
    
    /// <summary>
    /// Maximum messages per account per hour (anti-ban)
    /// </summary>
    public int AccountHourlyLimit { get; set; } = 100;
    
    /// <summary>
    /// Cooldown period between messages to the same recipient (seconds)
    /// </summary>
    public int RecipientCooldownSeconds { get; set; } = 60;
    
    /// <summary>
    /// Minimum delay between messages (milliseconds)
    /// </summary>
    public int MinDelayMs { get; set; } = 2000;
    
    /// <summary>
    /// Maximum delay between messages (milliseconds)
    /// </summary>
    public int MaxDelayMs { get; set; } = 8000;
    
    /// <summary>
    /// More restrictive cap for OTP sends against the aggregate account-hour
    /// counter. All account traffic counts; this is not an independent OTP bucket.
    /// </summary>
    public int OtpAccountHourlyLimit { get; set; } = 50;
}
