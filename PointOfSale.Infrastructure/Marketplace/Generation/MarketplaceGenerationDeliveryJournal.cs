using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal sealed class MarketplaceGenerationDeliveryJournal(NpgsqlDataSource source) : IGenerationDeliveryJournal
{
    public async Task<GenerationOperation> PrepareAsync(GenerationLease lease, ExternalWriteTicket ticket,
        MarketplacePhysicalAdmission.Expected target, CancellationToken ct)
    {
        MarketplacePhysicalAdmission.Validate(target);
        if (lease.JournalId != target.Journal || ticket.Epoch > lease.Epoch)
            throw new InvalidDataException("Delivery owner/target lineage rejected.");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await using var read = new NpgsqlCommand("""
            SELECT i.payload,EXISTS(SELECT 1 FROM marketplace_generation.dispatch_claims c WHERE c.operation_id=i.operation_id),
                EXISTS(SELECT 1 FROM marketplace_generation.recovery_operations r WHERE r.operation_id=i.operation_id)
            FROM marketplace_generation.write_intents i
            JOIN marketplace_generation.provider_bindings p ON p.generation_id=i.generation_id
            JOIN marketplace_generation.builds b ON b.generation_id=i.generation_id
            JOIN marketplace_generation.write_barriers w ON w.generation_id=i.generation_id
            WHERE i.operation_id=@operation AND i.completion_token=@token AND i.generation_id=@generation
                AND i.run_id=@run AND i.service_id=@service AND i.source_revision=@revision AND i.physical_uuid=@uuid
                AND i.fingerprint=@fingerprint AND i.digest=@digest AND i.issued_epoch=@epoch
                AND i.state IN ('outstanding','acknowledged','superseded','not-sent') AND p.journal_id=@journal AND p.cluster_uuid=@cluster
                AND p.physical_uuid=i.physical_uuid AND p.fingerprint=i.fingerprint
                AND b.physical_name=@name AND b.physical_uuid=i.physical_uuid AND b.fingerprint=i.fingerprint
                AND w.state IN ('open','draining') FOR UPDATE OF i,w
            """, db, tx);
        MarketplaceGenerationDeliveryProtocol.TicketParameters(read, ticket);
        read.Parameters.AddWithValue("journal", target.Journal); read.Parameters.AddWithValue("cluster", target.ClusterUuid);
        read.Parameters.AddWithValue("name", target.PhysicalName);
        string? payload; bool claimed, recoverable;
        await using (var rows = await read.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Exact durable delivery/provider binding required.");
            payload = rows.IsDBNull(0) ? null : rows.GetString(0); claimed = rows.GetBoolean(1); recoverable = rows.GetBoolean(2);
        }
        var operation = new GenerationOperation(target, ticket, payload); operation.Validate();
        if (claimed != recoverable)
            throw new InvalidOperationException("LEGACY_UNKNOWN_DISPATCH_REQUIRES_INDEPENDENT_DRAIN; never relabel an unfenced request.");
        if (!claimed)
        {
            if (ticket.Epoch != lease.Epoch) throw new InvalidOperationException("A stale ticket cannot start a new dispatch.");
            await using var claim = new NpgsqlCommand("INSERT INTO marketplace_generation.dispatch_claims(operation_id) VALUES(@operation)", db, tx);
            claim.Parameters.AddWithValue("operation", ticket.OperationId); await claim.ExecuteNonQueryAsync(ct);
            await MarketplaceGenerationAttemptOutcomes.AdmitAsync(db, tx, ticket.OperationId, ticket.OperationId,
                MarketplaceVersionedDocument.BuildUpdate(target.Journal, ticket, payload), ticket.Epoch, ct);
        }
        // The operation identity and original attempt are committed atomically;
        // no crash window leaves a recoverable claim without immutable payload.
        await MarketplaceGenerationOperationStore.AdmitAsync(db, tx, operation, ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await tx.CommitAsync(ct);
        return operation;
    }

    public async Task AcknowledgeAsync(GenerationOperation operation, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationOperationStore.RequireDurableTransactionAsync(db, tx, ct);
        var row = await MarketplaceGenerationOperationStore.ReadLockedAsync(db, tx, operation.Id, ct);
        MarketplaceGenerationOperationRecovery.RequireSame(operation, row.Operation);
        if (row.State != GenerationOperationState.Acknowledged) throw new InvalidOperationException("Unresolved operation cannot acknowledge delivery.");
        await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(db, operation.Ticket, ExternalDefiniteOutcome.Acknowledged, ct);
        await MarketplaceGenerationAttemptOutcomes.CompleteAsync(db, operation.Id, true, ct);
        await tx.CommitAsync(ct); // Both existing acknowledgements, or neither.
    }

    public async Task<bool> IsAcknowledgedAsync(GenerationOperation operation, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var query = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM marketplace_generation.write_intents i
                JOIN marketplace_generation.delivery_attempt_outcomes a ON a.attempt_id=i.operation_id
                WHERE i.operation_id=@operation AND i.completion_token=@token AND i.state='acknowledged' AND a.outcome='acknowledged')
            """, db);
        query.Parameters.AddWithValue("operation", operation.Id); query.Parameters.AddWithValue("token", operation.Ticket.CompletionToken);
        return await query.ExecuteScalarAsync(ct) is true;
    }

    public async Task ResolveSupersededAsync(GenerationOperation operation, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationOperationStore.RequireDurableTransactionAsync(db, tx, ct);
        var row = await MarketplaceGenerationOperationStore.ReadLockedAsync(db, tx, operation.Id, ct);
        MarketplaceGenerationOperationRecovery.RequireSame(operation, row.Operation);
        if (row.State != GenerationOperationState.SupersededAfterRefresh) throw new InvalidOperationException("Durable refreshed supersession required.");
        await using var resolve = new NpgsqlCommand("""
            UPDATE marketplace_generation.write_intents SET state='superseded' WHERE operation_id=@operation AND state='outstanding';
            INSERT INTO marketplace_generation.delivery_attempt_outcomes(attempt_id,outcome) VALUES(@operation,'superseded')
                ON CONFLICT(attempt_id) DO NOTHING;
            SELECT i.state='superseded' AND a.outcome='superseded' FROM marketplace_generation.write_intents i
                JOIN marketplace_generation.delivery_attempt_outcomes a ON a.attempt_id=i.operation_id WHERE i.operation_id=@operation
            """, db, tx);
        resolve.Parameters.AddWithValue("operation", operation.Id);
        if (await resolve.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Superseded outcome conflict.");
        await tx.CommitAsync(ct); // Not an acknowledgement of old historical visibility.
    }

    public async Task ResolveRejectedAsync(GenerationOperation operation, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationOperationStore.RequireDurableTransactionAsync(db, tx, ct);
        var row = await MarketplaceGenerationOperationStore.ReadLockedAsync(db, tx, operation.Id, ct);
        MarketplaceGenerationOperationRecovery.RequireSame(operation, row.Operation);
        if (row.State != GenerationOperationState.ConfirmedRejected) throw new InvalidOperationException("Definite rejection required.");
        await using var proof = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM marketplace_generation.gateway_attempts WHERE operation_id=@operation)", db, tx);
        proof.Parameters.AddWithValue("operation", operation.Id);
        if (await proof.ExecuteScalarAsync(ct) is not false)
            throw new InvalidOperationException("A previously dispatched request cannot be declared not-sent.");
        // Only newly prepared recoverable deliveries can reach here. Preparation
        // refuses legacy claimed/unknown histories; ALL gateway sends require a
        // committed attempt. An empty ledger is therefore a not-submitted proof,
        // never a timeout/GET-absence inference.
        await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(db, operation.Ticket, ExternalDefiniteOutcome.DefinitelyNotSent, ct);
        await MarketplaceGenerationAttemptOutcomes.CompleteAsync(db, operation.Id, false, ct);
        await tx.CommitAsync(ct);
    }
}
