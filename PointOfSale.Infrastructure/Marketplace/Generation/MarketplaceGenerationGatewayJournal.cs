using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>Credential owner only. Application/query principals receive no table grants.</summary>
internal sealed class MarketplaceGenerationGatewayJournal(NpgsqlDataSource source) : IGenerationGatewayJournal
{
    public async Task<bool> HasOutstandingAsync(GenerationOperation operation, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var query = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM marketplace_generation.gateway_attempts a
                WHERE a.operation_id=@operation AND a.request_digest=@digest AND NOT EXISTS(
                    SELECT 1 FROM marketplace_generation.gateway_settlements s
                    WHERE s.attempt_id=a.attempt_id AND s.request_digest=a.request_digest))
            """, db);
        query.Parameters.AddWithValue("operation", operation.Id); query.Parameters.AddWithValue("digest", operation.RequestDigest);
        return await query.ExecuteScalarAsync(ct) is true;
    }

    public async Task<GenerationDispatchPermit?> AdmitAsync(GenerationOperation operation, CancellationToken ct)
    {
        operation.Validate();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationOperationStore.RequireDurableTransactionAsync(db, tx, ct);
        // Lock first, then read the database clock in a NEW statement after any wait.
        await using var control = new NpgsqlCommand("SELECT epoch FROM marketplace_generation.control WHERE singleton FOR UPDATE", db, tx);
        if (await control.ExecuteScalarAsync(ct) is not long) throw new InvalidOperationException("Gateway epoch authority unavailable.");
        control.CommandText = "SELECT epoch=@epoch AND journal_id=@journal AND owner_id IS NOT NULL AND lease_until>clock_timestamp() FROM marketplace_generation.control WHERE singleton";
        control.Parameters.AddWithValue("epoch", operation.Ticket.Epoch);
        control.Parameters.AddWithValue("journal", operation.Target.Journal);
        if (await control.ExecuteScalarAsync(ct) is not true) { await tx.CommitAsync(ct); return null; }
        var durable = await MarketplaceGenerationOperationStore.ReadLockedAsync(db, tx, operation.Id, ct);
        MarketplaceGenerationOperationRecovery.RequireSame(operation, durable.Operation);
        if (durable.State is not (GenerationOperationState.Submitted or GenerationOperationState.OutcomeUnknown))
            throw new InvalidOperationException("Committed Submitted/OutcomeUnknown state required before gateway admission.");
        await using var barrier = new NpgsqlCommand("""
            SELECT w.state FROM marketplace_generation.write_barriers w
            JOIN marketplace_generation.write_intents i ON i.generation_id=w.generation_id
            JOIN marketplace_generation.provider_bindings p ON p.generation_id=i.generation_id
            JOIN marketplace_generation.builds b ON b.generation_id=i.generation_id
            WHERE i.operation_id=@operation AND i.completion_token=@token AND i.generation_id=@generation
                AND i.run_id=@run AND i.service_id=@service AND i.source_revision=@revision AND i.physical_uuid=@uuid
                AND i.fingerprint=@fingerprint AND i.digest=@digest AND i.issued_epoch=@epoch AND i.state='outstanding'
                AND p.journal_id=@journal AND p.cluster_uuid=@cluster AND p.physical_uuid=i.physical_uuid
                AND p.fingerprint=i.fingerprint AND b.physical_name=@name AND b.physical_uuid=i.physical_uuid
                AND b.fingerprint=i.fingerprint
            AND (w.state='open' OR (w.state='draining' AND EXISTS(
                SELECT 1 FROM marketplace_generation.gateway_attempts a WHERE a.operation_id=i.operation_id)))
            FOR UPDATE OF w
            """, db, tx);
        MarketplaceGenerationDeliveryProtocol.TicketParameters(barrier, operation.Ticket);
        barrier.Parameters.AddWithValue("journal", operation.Target.Journal); barrier.Parameters.AddWithValue("cluster", operation.Target.ClusterUuid);
        barrier.Parameters.AddWithValue("name", operation.Target.PhysicalName);
        if (await barrier.ExecuteScalarAsync(ct) is not string) { await tx.CommitAsync(ct); return null; }
        var attempt = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("INSERT INTO marketplace_generation.gateway_attempts(attempt_id,operation_id,epoch,request_digest) VALUES(@attempt,@operation,@epoch,@digest)", db, tx);
        insert.Parameters.AddWithValue("attempt", attempt); insert.Parameters.AddWithValue("operation", operation.Id);
        insert.Parameters.AddWithValue("epoch", operation.Ticket.Epoch); insert.Parameters.AddWithValue("digest", operation.RequestDigest);
        await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return new(attempt, operation);
    }

    public async Task SettleAsync(GenerationOperation operation, CancellationToken ct, GenerationOperation? successor = null)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationOperationStore.RequireDurableTransactionAsync(db, tx, ct);
        // Serialize settlement/admission/epoch advancement. The verified document
        // is retained; later reviewed writes can only strengthen its version fence.
        await using var control = new NpgsqlCommand("SELECT epoch FROM marketplace_generation.control WHERE singleton FOR UPDATE", db, tx);
        if (await control.ExecuteScalarAsync(ct) is not long) throw new InvalidOperationException("Gateway epoch authority unavailable.");
        await RequireIdentityAsync(db, tx, operation, ct);
        var witness = successor ?? operation;
        if (successor is not null) MarketplaceGenerationOperationRecovery.RequireSuccessor(operation, successor);
        await RequireIdentityAsync(db, tx, witness, ct);
        if (successor is not null)
        {
            await using var supersession = new NpgsqlCommand("""
                INSERT INTO marketplace_generation.gateway_supersessions(operation_id,witness_operation_id,witness_request_digest)
                VALUES(@operation,@witness,@digest) ON CONFLICT(operation_id,witness_operation_id) DO NOTHING
                """, db, tx);
            supersession.Parameters.AddWithValue("operation", operation.Id); supersession.Parameters.AddWithValue("witness", witness.Id);
            supersession.Parameters.AddWithValue("digest", witness.RequestDigest); await supersession.ExecuteNonQueryAsync(ct);
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.gateway_settlements(attempt_id,request_digest,witness_operation_id,witness_request_digest)
            SELECT attempt_id,request_digest,@witness,@witness_digest FROM marketplace_generation.gateway_attempts
            WHERE operation_id=@operation AND request_digest=@digest ON CONFLICT(attempt_id) DO NOTHING
            """, db, tx);
        insert.Parameters.AddWithValue("operation", operation.Id); insert.Parameters.AddWithValue("digest", operation.RequestDigest);
        insert.Parameters.AddWithValue("witness", witness.Id); insert.Parameters.AddWithValue("witness_digest", witness.RequestDigest);
        await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task RequireIdentityAsync(NpgsqlConnection db, NpgsqlTransaction tx, GenerationOperation operation, CancellationToken ct)
    {
        var row = await MarketplaceGenerationOperationStore.ReadLockedAsync(db, tx, operation.Id, ct);
        MarketplaceGenerationOperationRecovery.RequireSame(operation, row.Operation);
    }
}
