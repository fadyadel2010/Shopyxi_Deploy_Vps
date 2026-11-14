using System.Text.Json;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Exact-target security REVIEW INPUT, not a deployment or a credential issuer.
/// OpenSearch roles are additive: the gateway identity must have ONLY this role,
/// never an admin/default/application role, and its secret must not be available
/// to application/background/admin clients. Provisioning/retirement is offline
/// and cannot run concurrently with an admitted/active physical target.
/// </summary>
internal static class MarketplaceGenerationSecurityPlan
{
    internal static string GatewayRole(MarketplacePhysicalAdmission.Expected target)
    {
        MarketplacePhysicalAdmission.Validate(target);
        return JsonSerializer.Serialize(new
        {
            cluster_permissions = new[] { "cluster:monitor/main" },
            index_permissions = new[] { new
            {
                index_patterns = new[] { target.PhysicalName },
                allowed_actions = new[] { "indices:admin/get", "indices:admin/refresh*", "indices:data/read/mget*", "indices:data/write/update" }
            } },
            tenant_permissions = Array.Empty<object>()
        });
    }
}
