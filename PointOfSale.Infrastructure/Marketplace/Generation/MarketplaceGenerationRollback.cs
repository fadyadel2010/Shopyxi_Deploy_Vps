using System.Security.Cryptography;
using System.Text;
using Npgsql;
using PointOfSale.Infrastructure.Marketplace.Journal;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>Retained-target catch-up; activation still uses the shared durable pointer CAS.</summary>
internal sealed class MarketplaceGenerationRollback(MarketplaceGenerationRunDelivery delivery, MarketplaceGenerationTransition transition)
{
    internal async Task ExecuteAsync(NpgsqlConnection db, GenerationLease lease, Guid operation,
        long expectedVersion, Guid expectedActive, MarketplacePhysicalAdmission.Expected previous,
        MarketplaceGenerationTransport transport, CancellationToken ct = default, NpgsqlDataSource? recoverySource = null,
        IGenerationAtomicWriteBoundary? recoveryBoundary = null)
    {
        var run = await CatchUpAsync(db, lease, operation, previous, transport, ct, recoverySource, recoveryBoundary);
        if (!await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(db, lease, expectedActive, ct))
            throw new InvalidOperationException("Active-generation writers have not drained.");
        await transition.ExecuteAsync(db, lease, Identity(operation, "pointer"), run, true,
            expectedVersion, expectedActive, previous, transport, ct);
    }

    internal async Task<Guid> CatchUpAsync(NpgsqlConnection db, GenerationLease lease, Guid operation,
        MarketplacePhysicalAdmission.Expected previous, MarketplaceGenerationTransport transport, CancellationToken ct = default,
        NpgsqlDataSource? recoverySource = null, IGenerationAtomicWriteBoundary? recoveryBoundary = null)
    {
        if (operation == Guid.Empty || previous.Journal != lease.JournalId) throw new ArgumentException("Exact rollback identity required.");
        await MarketplaceGenerationDeliveryProtocol.BindProviderAsync(db, lease, previous, transport, ct);
        var required = await BeginAsync(db, lease, operation, previous, ct);
        if (required.Run is Guid completed) return completed;
        var position = await CheckpointAsync(db, previous.Generation, ct);
        while (position.Revision < required.Revision || position.Ordinal != long.MaxValue)
        {
            lease = await MarketplaceGenerationRegistry.RenewAsync(db, lease, TimeSpan.FromMinutes(2), ct);
            position = await MarketplaceGenerationRegistry.ReplayPageAsync(db, lease, previous.Generation, position, required.Revision, 128, ct);
        }
        if (position.Revision != required.Revision) throw new InvalidOperationException("Rollback checkpoint advanced beyond its durable obligation.");
        var frozen = await MarketplaceGenerationInputExport.FreezeAsync(db, lease, previous.Generation,
            Identity(operation, "export"), position, ct);
        var run = await MarketplaceFrozenOutput.CreateAsync(db, lease, frozen.ExportId, Identity(operation, "output"), ct);
        while (!run.Exhausted)
        {
            lease = await MarketplaceGenerationRegistry.RenewAsync(db, lease, TimeSpan.FromMinutes(2), ct);
            await MarketplaceFrozenOutput.CommitNextPageAsync(db, lease, run.RunId, Identity(operation, "page:" + run.Cursor.NextPage), run.Cursor, 128, ct);
            run = await MarketplaceFrozenOutput.ReadAsync(db, run.RunId, ct);
        }
        await MarketplaceFrozenOutput.SealAsync(db, lease, run.RunId, Identity(operation, "seal"), ct);
        if (recoverySource is null || recoveryBoundary is null) await delivery.DeliverAsync(db, lease, run.RunId, previous, transport, ct);
        else await delivery.DeliverRecoverablyAsync(recoverySource, db, lease, run.RunId, previous, transport, recoveryBoundary, ct);
        if (!await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(db, lease, previous.Generation, ct))
            throw new InvalidOperationException("Rollback delivery attempts remain unresolved.");
        await using var finishTransaction = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, finishTransaction, lease, true, ct);
        await using var finish = new NpgsqlCommand("""
            UPDATE marketplace_generation.catchup_intents SET state='completed',run_id=@run
            WHERE operation_id=@operation AND state='running' AND required_revision=@revision
            """, db, finishTransaction);
        finish.Parameters.AddWithValue("run", run.RunId); finish.Parameters.AddWithValue("operation", operation);
        finish.Parameters.AddWithValue("revision", required.Revision);
        if (await finish.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Catch-up completion CAS rejected.");
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, finishTransaction, lease, false, ct);
        await finishTransaction.CommitAsync(ct);
        return run.RunId;
    }

    private sealed record Obligation(long Revision, Guid? Run);
    private static async Task<Obligation> BeginAsync(NpgsqlConnection db, GenerationLease lease, Guid operation,
        MarketplacePhysicalAdmission.Expected physical, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await MarketplaceGenerationLifecycleStore.LockPointerAsync(db, tx, ct);
        await using (var prior = new NpgsqlCommand("SELECT generation_id,journal_id,required_revision,run_id FROM marketplace_generation.catchup_intents WHERE operation_id=@operation", db, tx))
        {
            prior.Parameters.AddWithValue("operation", operation);
            await using var rows = await prior.ExecuteReaderAsync(ct);
            if (await rows.ReadAsync(ct))
            {
                if (rows.GetGuid(0) != physical.Generation || rows.GetGuid(1) != lease.JournalId)
                    throw new InvalidOperationException("Rollback operation identity cannot be rebound.");
                var result = new Obligation(rows.GetInt64(2), rows.IsDBNull(3) ? null : rows.GetGuid(3));
                await rows.DisposeAsync(); await tx.CommitAsync(ct); return result;
            }
        }
        await using var watermark = new NpgsqlCommand("SELECT marketplace_generation.lock_source_revision(@journal)", db, tx);
        watermark.Parameters.AddWithValue("journal", lease.JournalId);
        var revision = await watermark.ExecuteScalarAsync(ct) is long current ? current : throw new InvalidDataException("Source lineage missing.");
        var checkpoint = await CheckpointAsync(db, physical.Generation, ct);
        if (checkpoint.Ordinal != long.MaxValue || checkpoint.Revision > revision) throw new InvalidDataException("Complete retained source boundary required.");
        if (checkpoint.Revision == revision)
        {
            await using var observed = new NpgsqlCommand("SELECT run_id FROM marketplace_generation.refresh_observations WHERE generation_id=@generation AND source_revision=@revision ORDER BY observed_at DESC LIMIT 1", db, tx);
            observed.Parameters.AddWithValue("generation", physical.Generation); observed.Parameters.AddWithValue("revision", revision);
            var run = await observed.ExecuteScalarAsync(ct) is Guid id ? id : throw new InvalidOperationException("Retained refresh acknowledgement missing.");
            await tx.CommitAsync(ct); return new(revision, run);
        }
        await using var admit = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.catchup_intents(operation_id,generation_id,journal_id,from_revision,required_revision,issued_epoch)
            SELECT @operation,b.generation_id,b.journal_id,b.checkpoint_revision,@revision,@epoch
            FROM marketplace_generation.builds b
            JOIN marketplace_generation.lifecycle l ON l.generation_id=b.generation_id
            JOIN marketplace_generation.write_barriers w ON w.generation_id=b.generation_id
            CROSS JOIN marketplace_generation.active_pointer p
            WHERE b.generation_id=@generation AND b.journal_id=@journal AND b.physical_uuid=@uuid AND b.fingerprint=@fingerprint
              AND l.state='retained' AND p.singleton AND p.previous_generation=b.generation_id
              AND p.active_generation IS DISTINCT FROM b.generation_id AND w.state='drained' AND w.last_epoch<@epoch
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.reader_pins r WHERE r.generation_id=b.generation_id
                AND r.released_at IS NULL AND r.expires_at>clock_timestamp())
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.reader_operations o JOIN marketplace_generation.reader_pins r ON r.pin_id=o.pin_id
                WHERE r.generation_id=b.generation_id AND o.completed_at IS NULL AND (o.deadline IS NULL OR o.deadline>clock_timestamp()))
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.write_intents i WHERE i.generation_id=b.generation_id AND i.state='outstanding')
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.delivery_attempts d JOIN marketplace_generation.write_intents i ON i.operation_id=d.operation_id
                WHERE i.generation_id=b.generation_id AND NOT EXISTS(SELECT 1 FROM marketplace_generation.delivery_attempt_outcomes a WHERE a.attempt_id=d.attempt_id));
            UPDATE marketplace_generation.write_barriers SET state='open',last_epoch=@epoch
              WHERE generation_id=@generation AND state='drained' AND EXISTS(
                SELECT 1 FROM marketplace_generation.catchup_intents WHERE operation_id=@operation)
            """, db, tx);
        admit.Parameters.AddWithValue("operation", operation); admit.Parameters.AddWithValue("revision", revision);
        admit.Parameters.AddWithValue("epoch", lease.Epoch); admit.Parameters.AddWithValue("generation", physical.Generation);
        admit.Parameters.AddWithValue("journal", lease.JournalId); admit.Parameters.AddWithValue("uuid", physical.PhysicalUuid);
        admit.Parameters.AddWithValue("fingerprint", physical.Fingerprint);
        if (await admit.ExecuteNonQueryAsync(ct) != 2) throw new InvalidOperationException("Retained generation requires a newer owner epoch and fully drained readers/writers.");
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await tx.CommitAsync(ct); return new(revision, null);
    }

    private static async Task<JournalPosition> CheckpointAsync(NpgsqlConnection db, Guid generation, CancellationToken ct)
    {
        await using var read = new NpgsqlCommand("SELECT checkpoint_revision,checkpoint_ordinal FROM marketplace_generation.builds WHERE generation_id=@generation", db);
        read.Parameters.AddWithValue("generation", generation);
        await using var rows = await read.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Retained build missing.");
        return new(rows.GetInt64(0), rows.GetInt64(1));
    }
    private static Guid Identity(Guid operation, string boundary) => new(SHA256.HashData(Encoding.UTF8.GetBytes(operation.ToString("D") + ":" + boundary)).AsSpan(0, 16));
}
