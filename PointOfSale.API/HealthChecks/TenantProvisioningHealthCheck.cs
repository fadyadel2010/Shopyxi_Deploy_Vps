using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.HealthChecks;

/// <summary>
/// Reports durable onboarding backlog without turning a merchant-specific
/// convergence failure into an API pod restart loop.
/// </summary>
public sealed class TenantProvisioningHealthCheck : IHealthCheck
{
    private readonly ApplicationDbContext _db;

    public TenantProvisioningHealthCheck(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var stuckCutoff = now.AddMinutes(-10);

        var dueFailures = await _db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(operation =>
                operation.State == TenantProvisioningState.Failed &&
                (!operation.NextAttemptAtUtc.HasValue || operation.NextAttemptAtUtc <= now),
                cancellationToken);

        var stuckOperations = await _db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(operation =>
                operation.State == TenantProvisioningState.Provisioning &&
                operation.UpdatedAtUtc <= stuckCutoff &&
                (!operation.LeaseExpiresAtUtc.HasValue || operation.LeaseExpiresAtUtc <= now),
                cancellationToken);

        var data = new Dictionary<string, object>
        {
            ["dueFailures"] = dueFailures,
            ["stuckOperations"] = stuckOperations
        };

        return dueFailures + stuckOperations > 0
            ? HealthCheckResult.Degraded(
                "Tenant provisioning has durable operations awaiting recovery.",
                data: data)
            : HealthCheckResult.Healthy(
                "Tenant provisioning convergence is current.",
                data);
    }
}
