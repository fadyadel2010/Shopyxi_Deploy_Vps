namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal interface IGenerationDeliveryJournal
{
    Task<GenerationOperation> PrepareAsync(GenerationLease lease, ExternalWriteTicket ticket, MarketplacePhysicalAdmission.Expected target, CancellationToken ct);
    Task AcknowledgeAsync(GenerationOperation operation, CancellationToken ct);
    Task<bool> IsAcknowledgedAsync(GenerationOperation operation, CancellationToken ct);
    Task ResolveSupersededAsync(GenerationOperation operation, CancellationToken ct);
    Task ResolveRejectedAsync(GenerationOperation operation, CancellationToken ct);
}

/// <summary>
/// Sealed delivery -> durable claim/operation -> gateway -> refresh -> final SQL
/// acknowledgement. Reuses the ORIGINAL operation/ticket on restart. The legacy
/// attempt and intent are never acknowledged by a success response or SQL state
/// alone. Old unfenced dispatches cannot be imported as recoverable operations.
/// </summary>
internal sealed class MarketplaceGenerationRecoverableDelivery(
    IGenerationDeliveryJournal journal, IGenerationOperationStore store, IGenerationAtomicWriteBoundary boundary)
{
    internal async Task<GenerationOperationSnapshot> DeliverAsync(GenerationLease lease, ExternalWriteTicket ticket,
        MarketplacePhysicalAdmission.Expected target, CancellationToken ct = default)
    {
        var operation = await journal.PrepareAsync(lease, ticket, target, ct);
        operation.Validate();
        if (operation.Target != target || operation.Ticket != ticket)
            throw new InvalidDataException("Delivery journal cannot rebind immutable request lineage.");
        return await RecoverOperationAsync(operation, ct);
    }

    // Supervised restart reconciliation does not require reacquiring/advancing a
    // lease first: that would deadlock against its own unknown raw request. Only
    // the gateway can admit writes; old/expired authority still cannot submit.
    internal async Task<GenerationOperationSnapshot> RecoverAsync(Guid operation, CancellationToken ct = default)
    {
        var row = await store.ReadAsync(operation, ct);
        row.Operation.Validate();
        return await RecoverOperationAsync(row.Operation, ct);
    }

    // Supervised ownership recovery is lookup-only. Unlike explicit current-owner
    // delivery, this path must never retransmit an unknown operation.
    internal async Task<GenerationOperationSnapshot> ReconcileAsync(Guid operation, CancellationToken ct = default)
    {
        var row = await store.ReadAsync(operation, ct);
        row.Operation.Validate();
        return await RecoverOperationAsync(row.Operation, ct, allowSubmission: false);
    }

    private async Task<GenerationOperationSnapshot> RecoverOperationAsync(GenerationOperation operation, CancellationToken ct, bool allowSubmission = true)
    {
        if (await journal.IsAcknowledgedAsync(operation, ct))
        {
            // Final acknowledgement already committed atomically. A later source
            // revision must not turn an idempotent replay into a fresh attempt to
            // certify old content, or reopen its immutable historical outcome.
            var terminal = await store.ReadAsync(operation.Id, ct);
            MarketplaceGenerationOperationRecovery.RequireSame(operation, terminal.Operation);
            if (terminal.State != GenerationOperationState.Acknowledged)
                throw new InvalidDataException("Delivery/recovery terminal state conflict.");
            return terminal;
        }
        var recovery = new MarketplaceGenerationOperationRecovery(store, boundary);
        var result = allowSubmission
            ? await recovery.DeliverAsync(operation, ct)
            : await recovery.ReconcileAsync(operation.Id, ct);
        if (result.State == GenerationOperationState.ConfirmedRejected)
        {
            await journal.ResolveRejectedAsync(operation, ct);
            return result;
        }
        if (result.State == GenerationOperationState.SupersededAfterRefresh)
        {
            // A terminal state is durable, but a restart must re-establish its
            // successor proof before discharging the existing delivery intent.
            var superseded = await boundary.RefreshAndVerifyAsync(operation, ct);
            MarketplaceGenerationOperationRecovery.RequireSame(operation, superseded.Operation);
            if (superseded.State != GenerationOperationState.SupersededAfterRefresh)
                throw new InvalidDataException("Refreshed successor proof required.");
            MarketplaceGenerationOperationRecovery.RequireSuccessor(operation, superseded.Successor);
            await journal.ResolveSupersededAsync(operation, ct);
            return result;
        }
        if (result.State != GenerationOperationState.Acknowledged) return result;
        // Covers a crash/transaction failure between recovery acknowledgement and
        // the existing delivery intent/attempt acknowledgement. Restart rechecks
        // visibility even when recovery's own durable state was already terminal.
        var visible = await boundary.RefreshAndVerifyAsync(operation, ct);
        MarketplaceGenerationOperationRecovery.RequireSame(operation, visible.Operation);
        if (visible.State == GenerationOperationState.SupersededAfterRefresh)
        {
            MarketplaceGenerationOperationRecovery.RequireSuccessor(operation, visible.Successor);
            result = await store.AdvanceAsync(result, GenerationOperationState.SupersededAfterRefresh, ct);
            await journal.ResolveSupersededAsync(operation, ct);
            return result;
        }
        if (visible.State != GenerationOperationState.VisibleAfterRefresh)
            throw new InvalidDataException("Final delivery acknowledgement requires refreshed visibility.");
        await journal.AcknowledgeAsync(operation, ct);
        return result;
    }
}
