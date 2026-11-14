using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>Explicit recoverable transition shared by cutover and rollback.</summary>
internal sealed class MarketplaceGenerationTransition
{
    internal async Task ExecuteAsync(NpgsqlConnection db, GenerationLease lease, Guid operation, Guid run,
        bool rollback, long expectedVersion, Guid? expectedActive, MarketplacePhysicalAdmission.Expected physical,
        MarketplaceGenerationTransport transport, CancellationToken ct = default,
        Action<MarketplaceGenerationLifecycleStore.CutoverBoundary>? injectFailure = null)
    {
        // Re-inspect immediately before intent AND before CAS. SQL readiness is
        // necessary, but a previously observed UUID cannot certify a recreated index.
        await InspectAsync(physical, transport, ct);
        await MarketplaceGenerationLifecycleStore.PrepareAsync(db, lease, operation, physical.Generation, run,
            rollback, expectedVersion, expectedActive, physical, ct, injectFailure);
        await InspectAsync(physical, transport, ct);
        await MarketplaceGenerationLifecycleStore.CommitAsync(db, lease, operation, physical, ct, injectFailure);
        await MarketplaceGenerationLifecycleStore.CompleteAsync(db, operation, ct, injectFailure);
    }

    internal async Task ReconcileAsync(NpgsqlConnection db, GenerationLease lease, Guid operation,
        MarketplacePhysicalAdmission.Expected physical, MarketplaceGenerationTransport transport, CancellationToken ct = default)
    {
        switch (await MarketplaceGenerationLifecycleStore.RecoverAsync(db, operation, ct))
        {
            case GenerationRecoveryAction.AlreadyComplete: return;
            case GenerationRecoveryAction.CompleteCommitted:
                await MarketplaceGenerationLifecycleStore.CompleteAsync(db, operation, ct); return;
            case GenerationRecoveryAction.RetryCompareAndSwap:
                await InspectAsync(physical, transport, ct);
                await MarketplaceGenerationLifecycleStore.CommitAsync(db, lease, operation, physical, ct);
                await MarketplaceGenerationLifecycleStore.CompleteAsync(db, operation, ct); return;
            default: throw new InvalidOperationException("Transition lost authoritative pointer CAS; no automatic rebase.");
        }
    }

    private static async Task InspectAsync(MarketplacePhysicalAdmission.Expected physical, MarketplaceGenerationTransport transport, CancellationToken ct)
    {
        MarketplacePhysicalAdmission.Validate(physical);
        var cluster = await transport.SendAsync(HttpMethod.Get, "/", null, ct);
        _ = MarketplacePhysicalAdmission.InspectResponses(physical, cluster,
            await transport.SendAsync(HttpMethod.Get, "/" + physical.PhysicalName, null, ct));
    }
}
