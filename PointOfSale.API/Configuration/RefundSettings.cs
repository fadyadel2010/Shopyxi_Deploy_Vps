namespace PointOfSale.API.Configuration;

public class RefundSettings
{
    // Shopixy contractual minima. Configuration may grant longer rights, never shorten them.
    public int RefundWindowDays { get; set; } = 14;
    public int DefectClaimWindowDays { get; set; } = 30;
    public bool AutoExecuteWhenReady { get; set; } = false;
    public int MaxReasonLength { get; set; } = 1000;
    public int MaxEvidenceFiles { get; set; } = 5;
    public long MaxImageBytes { get; set; } = 10 * 1024 * 1024;
    public long MaxVideoBytes { get; set; } = 50 * 1024 * 1024;
    public int UploadUrlExpiryMinutes { get; set; } = 15;
    public int EvidenceAccessUrlExpiryMinutes { get; set; } = 10;
    public int StagedUploadExpiryHours { get; set; } = 24;
    public int EvidenceCleanupBatchSize { get; set; } = 100;
}
