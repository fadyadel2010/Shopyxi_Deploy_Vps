namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal sealed record GenerationDispatchPermit(Guid Attempt, GenerationOperation Operation);

/// <summary>Shared durable admission; session loss must never erase an attempt.</summary>
internal interface IGenerationGatewayJournal
{
    Task<GenerationDispatchPermit?> AdmitAsync(GenerationOperation operation, CancellationToken ct);
    Task<bool> HasOutstandingAsync(GenerationOperation operation, CancellationToken ct);
    Task SettleAsync(GenerationOperation operation, CancellationToken ct, GenerationOperation? successor = null);
}

internal interface IGenerationGatewayProvider
{
    Task WriteAsync(GenerationDispatchPermit permit, CancellationToken ct);
    // Exact physical target, complete content/envelope and digest; false means
    // absent, not an assertion that an earlier request cannot still arrive.
    Task<bool> VerifyAsync(GenerationOperation operation, bool refresh, CancellationToken ct);
    Task<GenerationOperation?> FindVisibleSuccessorAsync(GenerationOperation operation, CancellationToken ct) => Task.FromResult<GenerationOperation?>(null);
}

/// <summary>
/// Conservative admission/drain gateway. Requires EXCLUSIVE update credentials,
/// no delete/lifecycle privileges and retained immutable physical targets. Epoch
/// advancement is refused by SQL while any admitted request can still mutate.
/// A successful exact observation makes all identical delayed requests no-ops:
/// the reviewed script never regresses revision/epoch or removes tombstones.
/// Missing/foreign/newer content is never converted into successful delivery.
/// No process lock, lease expiry or HTTP timeout discharges a durable attempt.
/// </summary>
internal sealed class MarketplaceGenerationGateway(IGenerationGatewayJournal journal, IGenerationGatewayProvider provider)
    : IGenerationAtomicWriteBoundary
{
    public async Task<GenerationBoundaryReceipt?> LookupAsync(GenerationOperation operation, CancellationToken ct)
    {
        operation.Validate();
        var superseded = await SupersededAsync(operation, ct);
        if (superseded is not null) return superseded;
        if (!await provider.VerifyAsync(operation, false, ct)) return null;
        await journal.SettleAsync(operation, ct);
        return new(operation, GenerationOperationState.Applied);
    }

    public async Task<GenerationBoundaryReceipt> SubmitAsync(GenerationOperation operation, CancellationToken ct)
    {
        operation.Validate();
        var permit = await journal.AdmitAsync(operation, ct); // Commit BEFORE dispatch; locks common epoch authority.
        if (permit is null)
        {
            // Rejection of THIS admission says nothing about an earlier delayed
            // request. Never terminalize an unknown history on ownership loss.
            return new(operation, await journal.HasOutstandingAsync(operation, ct)
                ? GenerationOperationState.OutcomeUnknown : GenerationOperationState.ConfirmedRejected);
        }
        MarketplaceGenerationOperationRecovery.RequireSame(operation, permit.Operation);
        if (permit.Attempt == Guid.Empty) throw new InvalidDataException("Durable dispatch identity required.");
        await provider.WriteAsync(permit, ct);
        var superseded = await SupersededAsync(operation, ct);
        if (superseded is not null) return superseded;
        // HTTP success/noop is not proof of this operation (a newer revision can
        // produce noop). Prove the exact content before discharging ANY attempt.
        if (!await provider.VerifyAsync(operation, false, ct))
            return new(operation, GenerationOperationState.OutcomeUnknown);
        await journal.SettleAsync(operation, ct);
        return new(operation, GenerationOperationState.Applied);
    }

    public async Task<GenerationBoundaryReceipt> RefreshAndVerifyAsync(GenerationOperation operation, CancellationToken ct)
    {
        operation.Validate();
        var superseded = await SupersededAsync(operation, ct);
        if (superseded is not null) return superseded;
        if (!await provider.VerifyAsync(operation, true, ct))
            throw new InvalidDataException("Exact refreshed operation visibility required; unresolved delivery cannot be acknowledged.");
        await journal.SettleAsync(operation, ct);
        return new(operation, GenerationOperationState.VisibleAfterRefresh);
    }

    private async Task<GenerationBoundaryReceipt?> SupersededAsync(GenerationOperation operation, CancellationToken ct)
    {
        var successor = await provider.FindVisibleSuccessorAsync(operation, ct);
        if (successor is null) return null;
        MarketplaceGenerationOperationRecovery.RequireSuccessor(operation, successor);
        await journal.SettleAsync(operation, ct, successor);
        return new(operation, GenerationOperationState.SupersededAfterRefresh, successor);
    }
}
