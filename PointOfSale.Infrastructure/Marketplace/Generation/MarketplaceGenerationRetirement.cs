using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Durable retirement admission and exact physical cleanup. Neither a missing
/// HTTP response nor a timeout can mark deletion complete.
/// Retention defaults to infinity until a separately authorized policy releases it.
/// </summary>
internal static class MarketplaceGenerationRetirement
{
    internal static async Task ExecuteAsync(NpgsqlConnection db, GenerationLease lease, Guid operation,
        MarketplacePhysicalAdmission.Expected physical, MarketplaceGenerationTransport transport,
        CancellationToken ct = default, Action<MarketplaceGenerationPhysicalRetirement.Boundary>? injectFailure = null)
    {
        await PrepareAsync(db, lease, operation, physical, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await MarketplaceGenerationLifecycleStore.LockPointerAsync(db, tx, ct);
        await using (var admission = new NpgsqlCommand("""
            SELECT r.state FROM marketplace_generation.retirement_intents r
            JOIN marketplace_generation.lifecycle l ON l.generation_id=r.generation_id
            CROSS JOIN marketplace_generation.active_pointer a
            WHERE r.operation_id=@operation AND r.generation_id=@generation AND r.physical_uuid=@uuid
              AND r.cluster_uuid=@cluster AND r.fingerprint=@fingerprint AND a.singleton
              AND a.active_generation IS DISTINCT FROM r.generation_id AND a.previous_generation IS DISTINCT FROM r.generation_id
              AND ((r.state='completed' AND l.state='retired') OR (r.state IN ('intent','deleted') AND l.state='retiring'))
            FOR UPDATE OF r,l
            """, db, tx))
        {
            Parameters(admission, operation, physical);
            var state = await admission.ExecuteScalarAsync(ct) as string
                ?? throw new InvalidOperationException("Exact durable retirement authorization required.");
            if (state == "completed") { await tx.CommitAsync(ct); return; }
        }
        // Keep pointer/control locks through the provider operation. A crash leaves
        // 'retiring'; a reconnect inspects the same UUID or proves exact absence.
        await MarketplaceGenerationPhysicalRetirement.DeleteAsync(physical, transport, ct, injectFailure,
            new GenerationRetirementPermit(operation, physical));
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await using (var finish = new NpgsqlCommand("""
            UPDATE marketplace_generation.retirement_intents SET state='deleted' WHERE operation_id=@operation AND state='intent';
            UPDATE marketplace_generation.lifecycle SET state='retired',retired_at=clock_timestamp()
              WHERE generation_id=@generation AND state='retiring';
            UPDATE marketplace_generation.retirement_intents SET state='completed' WHERE operation_id=@operation AND state='deleted'
            """, db, tx))
        {
            Parameters(finish, operation, physical);
            await finish.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    internal static async Task PrepareAsync(NpgsqlConnection db, GenerationLease lease, Guid operation,
        MarketplacePhysicalAdmission.Expected physical, CancellationToken ct = default)
    {
        MarketplacePhysicalAdmission.Validate(physical);
        if (operation == Guid.Empty || physical.Journal != lease.JournalId) throw new ArgumentException("Exact retirement lineage required.");
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await MarketplaceGenerationLifecycleStore.LockPointerAsync(db, tx, ct);
        await using (var existing = new NpgsqlCommand("""
            SELECT generation_id=@generation AND physical_uuid=@uuid AND cluster_uuid=@cluster AND fingerprint=@fingerprint
            FROM marketplace_generation.retirement_intents WHERE operation_id=@operation
            """, db, tx))
        {
            Parameters(existing, operation, physical);
            if (await existing.ExecuteScalarAsync(ct) is bool matches)
            {
                if (!matches) throw new InvalidOperationException("Retirement identity cannot be rebound.");
                await tx.CommitAsync(ct); return;
            }
        }
        await using (var admit = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.retirement_intents(operation_id,generation_id,physical_uuid,cluster_uuid,fingerprint)
            SELECT @operation,l.generation_id,p.physical_uuid,p.cluster_uuid,p.fingerprint
            FROM marketplace_generation.lifecycle l
            JOIN marketplace_generation.builds b ON b.generation_id=l.generation_id
            JOIN marketplace_generation.provider_bindings p ON p.generation_id=l.generation_id
            JOIN marketplace_generation.write_barriers w ON w.generation_id=l.generation_id
            CROSS JOIN marketplace_generation.active_pointer a
            WHERE l.generation_id=@generation AND l.state IN ('retained','verified') AND l.retain_until<=clock_timestamp()
              AND a.singleton AND a.active_generation IS DISTINCT FROM l.generation_id AND a.previous_generation IS DISTINCT FROM l.generation_id
              AND p.journal_id=@journal AND p.cluster_uuid=@cluster AND p.physical_uuid=@uuid AND p.fingerprint=@fingerprint
              AND b.physical_name=@name AND b.physical_uuid=p.physical_uuid AND b.fingerprint=p.fingerprint
              AND w.state='drained' AND b.checkpoint_ordinal=9223372036854775807
              AND EXISTS(SELECT 1 FROM marketplace_generation.refresh_observations r
                    WHERE r.generation_id=l.generation_id AND r.source_revision=b.checkpoint_revision
                      AND r.physical_uuid=p.physical_uuid AND r.cluster_uuid=p.cluster_uuid AND r.fingerprint=p.fingerprint)
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.reader_pins r WHERE r.generation_id=l.generation_id
                    AND r.released_at IS NULL AND r.expires_at>clock_timestamp())
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.reader_operations o
                    JOIN marketplace_generation.reader_pins r ON r.pin_id=o.pin_id
                    WHERE r.generation_id=l.generation_id AND o.completed_at IS NULL
                      AND (o.deadline IS NULL OR o.deadline>clock_timestamp()))
               AND NOT EXISTS(SELECT 1 FROM marketplace_generation.delivery_attempts d
                    JOIN marketplace_generation.write_intents i ON i.operation_id=d.operation_id
                    WHERE i.generation_id=l.generation_id AND NOT EXISTS(
                        SELECT 1 FROM marketplace_generation.delivery_attempt_outcomes a WHERE a.attempt_id=d.attempt_id))
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.write_intents i WHERE i.generation_id=l.generation_id AND i.state='outstanding')
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.output_runs r
                    JOIN marketplace_generation.exports e ON e.export_id=r.export_id
                    WHERE e.generation_id=l.generation_id AND NOT EXISTS(
                        SELECT 1 FROM marketplace_generation.refresh_observations observed WHERE observed.run_id=r.run_id))
               AND NOT EXISTS(SELECT 1 FROM marketplace_generation.pointer_intents i
                     WHERE i.target_generation=l.generation_id AND i.state IN ('intent','committed'))
               AND NOT EXISTS(SELECT 1 FROM marketplace_generation.catchup_intents c
                     WHERE c.generation_id=l.generation_id AND c.state='running')
            """, db, tx))
        {
            Parameters(admit, operation, physical);
            if (await admit.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Retirement excluded by retention, pointer, reader, delivery, replay or immutable physical binding.");
        }
        await using (var transition = new NpgsqlCommand("UPDATE marketplace_generation.lifecycle SET state='retiring' WHERE generation_id=@generation AND state IN ('retained','verified')", db, tx))
        {
            transition.Parameters.AddWithValue("generation", physical.Generation);
            if (await transition.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Concurrent retirement rejected.");
        }
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await tx.CommitAsync(ct);
    }

    private static void Parameters(NpgsqlCommand command, Guid operation, MarketplacePhysicalAdmission.Expected physical)
    {
        command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("generation", physical.Generation);
        command.Parameters.AddWithValue("journal", physical.Journal); command.Parameters.AddWithValue("cluster", physical.ClusterUuid);
        command.Parameters.AddWithValue("uuid", physical.PhysicalUuid); command.Parameters.AddWithValue("fingerprint", physical.Fingerprint);
        command.Parameters.AddWithValue("name", physical.PhysicalName);
    }
}
