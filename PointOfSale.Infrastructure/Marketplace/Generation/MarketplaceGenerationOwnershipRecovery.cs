using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal enum GenerationOwnershipRecoveryState
{
    Acquired, OwnerStillActive, ReconciliationPageIncomplete, AwaitingAuthoritativeOutcome
}

internal sealed record GenerationOwnershipRecoveryResult(
    GenerationOwnershipRecoveryState State,
    GenerationLease? Lease,
    MarketplaceGenerationRunDelivery.RecoveryPage Reconciliation);

/// <summary>
/// Explicit, bounded recovery-before-takeover. Never registered as a worker.
/// Reconnect/lease expiry is not a delivery receipt. The unchanged SQL epoch
/// guard is the final authority, including for legacy unfenced attempts which
/// cannot be imported into the recoverable operation protocol.
/// </summary>
internal static class MarketplaceGenerationOwnershipRecovery
{
    internal static async Task<GenerationOwnershipRecoveryResult> ReconcileAndTryAcquireAsync(
        NpgsqlDataSource source,
        IGenerationAtomicWriteBoundary boundary,
        Guid owner,
        TimeSpan duration,
        Guid? after = null,
        int limit = 128,
        CancellationToken ct = default)
    {
        if (owner == Guid.Empty || duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(2))
            throw new ArgumentException("Owner and bounded lease duration required.");
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(boundary);

        // No lease is needed for authenticated provider lookup and settlement.
        // Reconciliation cannot issue writes, even under still-current ownership.
        var page = await new MarketplaceGenerationRunDelivery()
            .ReconcilePendingAsync(source, boundary, after, limit, ct);
        if (page.Operations.Count == limit)
            return new(GenerationOwnershipRecoveryState.ReconciliationPageIncomplete, null, page);

        await using var db = await source.OpenConnectionAsync(ct);
        try
        {
            var lease = await MarketplaceGenerationRegistry.TryAcquireAsync(db, owner, duration, ct);
            return new(lease is null ? GenerationOwnershipRecoveryState.OwnerStillActive : GenerationOwnershipRecoveryState.Acquired,
                lease, page);
        }
        catch (PostgresException error) when (
            error.SqlState == "55000" && error.MessageText == "GENERATION_GATEWAY_UNRESOLVED_REQUESTS")
        {
            // Do not suppress other database/authorization/integrity errors. This
            // precise result retains every unresolved attempt and the old epoch.
            return new(GenerationOwnershipRecoveryState.AwaitingAuthoritativeOutcome, null, page);
        }
    }
}
