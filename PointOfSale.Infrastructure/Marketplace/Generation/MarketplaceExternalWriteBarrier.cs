using Npgsql;
using NpgsqlTypes;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Durable admission/drain substrate, deliberately unregistered. A ticket alone
/// does NOT authorize an HTTP request: independent physical verification, a common
/// transport with remote source-version/tombstone enforcement, and all-writer
/// integration are still required. Never infer external cancellation from SQL
/// session loss, lease expiry, an exception, timeout, or process restart.
/// </summary>
internal static class MarketplaceExternalWriteBarrier
{
    internal static string SchemaSql
    {
        get
        {
            using var stream = typeof(MarketplaceExternalWriteBarrier).Assembly.GetManifestResourceStream(
                "PointOfSale.Infrastructure.Marketplace.Generation.MarketplaceExternalWriteBarrier.sql")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    // A repeated operation never grants permission to issue another HTTP request.
    // A crash after commit but before send is conservatively outstanding, not safe
    // to reclaim. Tombstones are retained payload=NULL intents, never physical deletes.
    internal static async Task<ExternalWriteTicket> AdmitAsync(NpgsqlConnection db, GenerationLease lease,
        Guid run, Guid service, Guid operation, CancellationToken ct = default)
    {
        if (run == Guid.Empty || service == Guid.Empty || operation == Guid.Empty)
            throw new ArgumentException("Exact output, service and operation identities required.");
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        Guid generation; long revision; string uuid; string fingerprint; string digest; string? payload;
        await using (var read = new NpgsqlCommand("""
            SELECT e.generation_id,e.revision,e.physical_uuid,e.fingerprint,o.digest,o.payload
            FROM marketplace_generation.output_runs r
            JOIN marketplace_generation.output_seals s ON s.run_id=r.run_id
            JOIN marketplace_generation.exports e ON e.export_id=r.export_id
            JOIN marketplace_generation.builds b ON b.generation_id=e.generation_id
            JOIN marketplace_generation.output_rows o ON o.run_id=r.run_id AND o.service_id=@service
            WHERE r.run_id=@run AND b.journal_id=@journal AND b.physical_uuid=e.physical_uuid
              AND b.fingerprint=e.fingerprint AND r.composer_fingerprint=@composer
            """, db, tx))
        {
            read.Parameters.AddWithValue("run", run); read.Parameters.AddWithValue("service", service);
            read.Parameters.AddWithValue("journal", lease.JournalId);
            read.Parameters.AddWithValue("composer", MarketplaceFrozenOutput.ComposerFingerprint);
            await using var rows = await read.ExecuteReaderAsync(ct);
            if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Sealed output and immutable physical lineage required.");
            generation = rows.GetGuid(0); revision = rows.GetInt64(1); uuid = rows.GetString(2);
            fingerprint = rows.GetString(3); digest = rows.GetString(4); payload = rows.IsDBNull(5) ? null : rows.GetString(5);
        }
        await using (var barrier = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.write_barriers(generation_id,last_epoch)
            VALUES(@generation,@epoch) ON CONFLICT(generation_id) DO NOTHING;
            SELECT state FROM marketplace_generation.write_barriers WHERE generation_id=@generation FOR UPDATE
            """, db, tx))
        {
            barrier.Parameters.AddWithValue("generation", generation); barrier.Parameters.AddWithValue("epoch", lease.Epoch);
            if (await barrier.ExecuteScalarAsync(ct) is not string state || state != "open")
                throw new InvalidOperationException("Generation is draining or drained; admission is permanently closed.");
        }
        await using (var prior = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM marketplace_generation.write_intents WHERE operation_id=@operation),
              EXISTS(SELECT 1 FROM marketplace_generation.write_intents WHERE generation_id=@generation AND service_id=@service
                AND (state='outstanding' OR (state IN ('acknowledged','superseded') AND source_revision>=@revision)))
            """, db, tx))
        {
            prior.Parameters.AddWithValue("operation", operation); prior.Parameters.AddWithValue("generation", generation);
            prior.Parameters.AddWithValue("service", service); prior.Parameters.AddWithValue("revision", revision);
            await using var rows = await prior.ExecuteReaderAsync(ct); await rows.ReadAsync(ct);
            if (rows.GetBoolean(0) || rows.GetBoolean(1))
                throw new InvalidOperationException("Operation already admitted, unresolved service write, or non-monotone source revision.");
        }
        var token = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.write_intents(operation_id,completion_token,generation_id,run_id,service_id,
                source_revision,physical_uuid,fingerprint,payload,digest,issued_epoch)
            VALUES(@operation,@token,@generation,@run,@service,@revision,@uuid,@fingerprint,@payload,@digest,@epoch)
            """, db, tx))
        {
            insert.Parameters.AddWithValue("operation", operation); insert.Parameters.AddWithValue("token", token);
            insert.Parameters.AddWithValue("generation", generation); insert.Parameters.AddWithValue("run", run);
            insert.Parameters.AddWithValue("service", service); insert.Parameters.AddWithValue("revision", revision);
            insert.Parameters.AddWithValue("uuid", uuid); insert.Parameters.AddWithValue("fingerprint", fingerprint);
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Text, (object?)payload ?? DBNull.Value);
            insert.Parameters.AddWithValue("digest", digest); insert.Parameters.AddWithValue("epoch", lease.Epoch);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await tx.CommitAsync(ct);
        return new(operation, token, generation, run, service, revision, uuid, fingerprint, digest, lease.Epoch);
    }

    // Only the eventual transport may report a definite terminal outcome. This
    // intentionally accepts late responses after lease loss so drain can complete.
    // No "timeout", "unknown", "lease expired" or forced-success outcome exists.
    // Acknowledged is delivery only, NEVER refreshed visibility or readiness.
    internal static async Task RecordDefiniteOutcomeAsync(NpgsqlConnection db, ExternalWriteTicket ticket,
        ExternalDefiniteOutcome outcome, CancellationToken ct = default)
    {
        var state = outcome switch
        {
            ExternalDefiniteOutcome.Acknowledged => "acknowledged",
            ExternalDefiniteOutcome.DefinitelyNotSent => "not-sent",
            _ => throw new ArgumentException("Only a proven terminal transport outcome is admissible.")
        };
        await using var command = new NpgsqlCommand("""
            UPDATE marketplace_generation.write_intents SET state=@state
            WHERE operation_id=@operation AND completion_token=@token AND generation_id=@generation
              AND run_id=@run AND service_id=@service AND source_revision=@revision AND physical_uuid=@uuid
              AND fingerprint=@fingerprint AND digest=@digest AND issued_epoch=@epoch AND state='outstanding'
            """, db);
        command.Parameters.AddWithValue("state", state); command.Parameters.AddWithValue("operation", ticket.OperationId);
        command.Parameters.AddWithValue("token", ticket.CompletionToken); command.Parameters.AddWithValue("generation", ticket.GenerationId);
        command.Parameters.AddWithValue("run", ticket.RunId); command.Parameters.AddWithValue("service", ticket.ServiceId);
        command.Parameters.AddWithValue("revision", ticket.SourceRevision); command.Parameters.AddWithValue("uuid", ticket.PhysicalUuid);
        command.Parameters.AddWithValue("fingerprint", ticket.Fingerprint); command.Parameters.AddWithValue("digest", ticket.Digest);
        command.Parameters.AddWithValue("epoch", ticket.Epoch);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            // A reconnect may repeat persistence after a definite provider reply.
            // Accept only the same exact identity and terminal outcome, never a
            // not-sent/acknowledged conversion or a newly admitted request.
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM marketplace_generation.write_intents
                WHERE operation_id=@operation AND completion_token=@token AND generation_id=@generation
                  AND run_id=@run AND service_id=@service AND source_revision=@revision AND physical_uuid=@uuid
                  AND fingerprint=@fingerprint AND digest=@digest AND issued_epoch=@epoch AND state=@state)
                """;
            if (await command.ExecuteScalarAsync(ct) is not true)
                throw new InvalidOperationException("Exact outstanding ticket required; terminal outcomes are immutable.");
        }
    }

    internal static async Task<bool> CloseAndTryDrainAsync(NpgsqlConnection db, GenerationLease lease,
        Guid generation, CancellationToken ct = default)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await using (var close = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.write_barriers(generation_id,state,last_epoch)
              SELECT generation_id,'draining',@epoch FROM marketplace_generation.builds WHERE generation_id=@generation AND journal_id=@journal
              ON CONFLICT(generation_id) DO NOTHING;
            UPDATE marketplace_generation.write_barriers SET state='draining',last_epoch=@epoch
              WHERE generation_id=@generation AND state='open';
            SELECT state FROM marketplace_generation.write_barriers WHERE generation_id=@generation FOR UPDATE
            """, db, tx))
        {
            close.Parameters.AddWithValue("generation", generation); close.Parameters.AddWithValue("journal", lease.JournalId);
            close.Parameters.AddWithValue("epoch", lease.Epoch);
            if (await close.ExecuteScalarAsync(ct) is not string) throw new InvalidOperationException("Generation lineage required.");
        }
        await using var drain = new NpgsqlCommand("""
            UPDATE marketplace_generation.write_barriers SET state='drained',last_epoch=@epoch
              WHERE generation_id=@generation AND state='draining'
                 AND NOT EXISTS(SELECT 1 FROM marketplace_generation.write_intents WHERE generation_id=@generation AND state='outstanding')
                 AND NOT EXISTS(SELECT 1 FROM marketplace_generation.delivery_attempts d
                   JOIN marketplace_generation.write_intents i ON i.operation_id=d.operation_id
                   WHERE i.generation_id=@generation AND NOT EXISTS(
                     SELECT 1 FROM marketplace_generation.delivery_attempt_outcomes a WHERE a.attempt_id=d.attempt_id));
            SELECT state='drained' FROM marketplace_generation.write_barriers WHERE generation_id=@generation
            """, db, tx);
        drain.Parameters.AddWithValue("generation", generation); drain.Parameters.AddWithValue("epoch", lease.Epoch);
        var drained = (bool)(await drain.ExecuteScalarAsync(ct))!;
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await tx.CommitAsync(ct); return drained;
    }
}

internal sealed record ExternalWriteTicket(Guid OperationId, Guid CompletionToken, Guid GenerationId,
    Guid RunId, Guid ServiceId, long SourceRevision, string PhysicalUuid, string Fingerprint, string Digest, long Epoch);
internal enum ExternalDefiniteOutcome { Acknowledged = 1, DefinitelyNotSent = 2 }
