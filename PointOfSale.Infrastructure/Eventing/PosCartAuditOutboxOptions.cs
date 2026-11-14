namespace PointOfSale.Infrastructure.Eventing;

public sealed class PosCartAuditOutboxOptions
{
    public const string SectionName = "PosCartAuditOutbox";
    public bool Enabled { get; set; }
    public Guid[] TenantIds { get; set; } = [];
    public int BatchSize { get; set; } = 50;
    public int PollIntervalSeconds { get; set; } = 15;
}
