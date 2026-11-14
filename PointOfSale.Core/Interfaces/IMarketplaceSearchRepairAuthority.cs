namespace PointOfSale.Core.Interfaces;

/// <summary>Bounded administrative recovery of the existing service search index.</summary>
public interface IMarketplaceSearchRepairAuthority
{
    Task<MarketplaceSearchRepairResult> RepairAsync(
        MarketplaceSearchRepairActor actor, CancellationToken cancellationToken = default);
}

// Identity is input, never proof of permission. Implementations must admit current SQL authority.
public sealed record MarketplaceSearchRepairActor(Guid UserId, Guid? TenantId, int? SuperAdminTokenVersion);

public sealed record MarketplaceSearchRepairResult(
    bool Success, string Code, int IndexedServices = 0, int DeletedServices = 0, int Errors = 0);
