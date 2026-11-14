namespace PointOfSale.Core.Events;

/// <summary>
/// Requests convergence for an already-committed canonical tenant. This event
/// never requests tenant-specific DNS records or certificates.
/// </summary>
public sealed class TenantProvisioningRequestedEvent : IntegrationEvent, IAggregateScopedIntegrationEvent
{
    public Guid ProvisioningOperationId { get; init; }
    public Guid OwnerUserId { get; init; }
    public Guid StoreConfigId { get; init; }
    public string CanonicalHostname { get; init; } = string.Empty;

    public Guid GetAuthoritativeAggregateId() => ProvisioningOperationId;
}

public sealed class TenantProvisioningReadyEvent : IntegrationEvent, IAggregateScopedIntegrationEvent
{
    public Guid ProvisioningOperationId { get; init; }
    public string CanonicalHostname { get; init; } = string.Empty;

    public Guid GetAuthoritativeAggregateId() => ProvisioningOperationId;
}
