using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal sealed class MarketplaceGenerationRetention
{
    /// <summary>
    /// Explicitly releases infinite rollback retention only after the generation is
    /// no longer active, previous, or the target of an unfinished pointer operation.
    /// Does not delete anything or waive any reader/writer/delivery exclusion.
    /// </summary>
    internal async Task ReleaseAsync(NpgsqlConnection db, GenerationLease lease, Guid operation, Guid generation,
        TimeSpan minimumRetention, CancellationToken ct = default)
    {
        if (operation == Guid.Empty || generation == Guid.Empty || minimumRetention < TimeSpan.FromDays(1) || minimumRetention > TimeSpan.FromDays(365))
            throw new ArgumentException("Explicit identity and bounded retention policy required.");
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await MarketplaceGenerationLifecycleStore.LockPointerAsync(db, tx, ct);
        await using (var prior = new NpgsqlCommand("SELECT generation_id=@generation AND journal_id=@journal AND minimum_retention=@duration FROM marketplace_generation.retention_releases WHERE operation_id=@operation", db, tx))
        {
            Parameters(prior, lease, operation, generation, minimumRetention);
            if (await prior.ExecuteScalarAsync(ct) is bool matches)
            {
                if (!matches) throw new InvalidOperationException("Retention operation cannot be rebound.");
                await tx.CommitAsync(ct); return;
            }
        }
        await using var release = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.retention_releases(operation_id,generation_id,journal_id,minimum_retention,retain_until,issued_epoch)
            SELECT @operation,l.generation_id,b.journal_id,@duration,clock_timestamp()+@duration,@epoch
            FROM marketplace_generation.lifecycle l JOIN marketplace_generation.builds b ON b.generation_id=l.generation_id
            CROSS JOIN marketplace_generation.active_pointer p
            WHERE l.generation_id=@generation AND b.journal_id=@journal AND l.state IN ('retained','verified')
              AND p.singleton AND p.active_generation IS DISTINCT FROM l.generation_id AND p.previous_generation IS DISTINCT FROM l.generation_id
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.pointer_intents i WHERE i.target_generation=l.generation_id AND i.state IN ('intent','committed'))
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.catchup_intents c WHERE c.generation_id=l.generation_id AND c.state='running');
            UPDATE marketplace_generation.lifecycle l SET retain_until=CASE WHEN l.retain_until='infinity' THEN r.retain_until ELSE greatest(l.retain_until,r.retain_until) END
            FROM marketplace_generation.retention_releases r WHERE r.operation_id=@operation AND l.generation_id=r.generation_id
            """, db, tx);
        Parameters(release, lease, operation, generation, minimumRetention); release.Parameters.AddWithValue("epoch", lease.Epoch);
        if (await release.ExecuteNonQueryAsync(ct) != 2) throw new InvalidOperationException("Active/rollback generations retain their protection.");
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await tx.CommitAsync(ct);
    }

    private static void Parameters(NpgsqlCommand command, GenerationLease lease, Guid operation, Guid generation, TimeSpan duration)
    {
        command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("journal", lease.JournalId); command.Parameters.AddWithValue("duration", duration);
    }
}
