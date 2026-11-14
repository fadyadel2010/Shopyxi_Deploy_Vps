using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;

namespace PointOfSale.Core.Entities;

/// <summary>
/// Persisted convergence authority for canonical tenant onboarding. Global
/// wildcard DNS and TLS are intentionally absent from this per-tenant model.
/// </summary>
public sealed class TenantProvisioningOperation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid StoreConfigId { get; set; }
    public string IdempotencyKeyHash { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string CanonicalHostname { get; set; } = string.Empty;
    public TenantProvisioningState State { get; set; } = TenantProvisioningState.Provisioning;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? LastErrorCategory { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime? AccountsSeededAtUtc { get; set; }
    public DateTime? StorefrontProjectionQueuedAtUtc { get; set; }
    public DateTime? VerificationDispatchedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
