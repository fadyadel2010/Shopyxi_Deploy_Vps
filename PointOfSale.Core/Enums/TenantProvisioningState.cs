namespace PointOfSale.Core.Enums;

/// <summary>
/// Durable application-resource convergence state. Tenant lifecycle and
/// StoreConfig publication remain separate authorities.
/// </summary>
public enum TenantProvisioningState
{
    Provisioning = 0,
    Ready = 1,
    Failed = 2,
    Suspended = 3,
    Disabled = 4
}
