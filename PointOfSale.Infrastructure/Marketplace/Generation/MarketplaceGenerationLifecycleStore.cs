using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Durable SQL coordination, deliberately not a remotely certified activation API.
/// All external orchestration remains behind the disabled application facade.
/// Uses the existing lease, immutable provider binding and refreshed output receipt.
/// </summary>
internal static class MarketplaceGenerationLifecycleStore
{
    internal enum CutoverBoundary { BeforeIntentCommit, AfterIntentCommit, AfterPointerChangeBeforeCommit, AfterPointerCommit, BeforeFinalAcknowledgement }

    internal static async Task PrepareAsync(NpgsqlConnection db, GenerationLease lease, Guid operation,
        Guid target, Guid run, bool rollback, long expectedVersion, Guid? expectedActive,
        MarketplacePhysicalAdmission.Expected physical, CancellationToken ct = default, Action<CutoverBoundary>? injectFailure = null)
    {
        if (operation == Guid.Empty || target == Guid.Empty || run == Guid.Empty || expectedVersion < 0 ||
            expectedVersion == long.MaxValue || target == expectedActive || target != physical.Generation || physical.Journal != lease.JournalId)
            throw new ArgumentException("Exact operation, pointer and target identities required.");
        MarketplacePhysicalAdmission.Validate(physical);
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await LockPointerAsync(db, tx, ct);
        // Durable retry identity must be checked before evaluating mutable eligibility.
        await using (var prior = new NpgsqlCommand("""
            SELECT kind=@kind AND journal_id=@journal AND expected_version=@version
              AND expected_active IS NOT DISTINCT FROM @active AND target_generation=@target AND run_id=@run
              AND EXISTS(SELECT 1 FROM marketplace_generation.provider_bindings p WHERE p.generation_id=target_generation
                AND p.journal_id=@journal AND p.cluster_uuid=@cluster AND p.physical_uuid=@uuid AND p.fingerprint=@fingerprint)
            FROM marketplace_generation.pointer_intents WHERE operation_id=@operation
            """, db, tx))
        {
            Parameters(prior, lease, operation, target, run, expectedVersion, expectedActive, rollback);
            prior.Parameters.AddWithValue("cluster", physical.ClusterUuid); prior.Parameters.AddWithValue("uuid", physical.PhysicalUuid);
            prior.Parameters.AddWithValue("fingerprint", physical.Fingerprint);
            if (await prior.ExecuteScalarAsync(ct) is bool matches)
            {
                if (!matches) throw new InvalidOperationException("Operation identity cannot be rebound.");
                await tx.CommitAsync(ct);
                return;
            }
        }
        long watermark;
        await using (var gate = new NpgsqlCommand("SELECT marketplace_generation.lock_source_revision(@journal)", db, tx))
        {
            gate.Parameters.AddWithValue("journal", lease.JournalId);
            watermark = await gate.ExecuteScalarAsync(ct) is long revision ? revision : throw new InvalidDataException("Source lineage missing.");
        }
        await RequireRefreshAsync(db, tx, lease.JournalId, target, run, watermark, physical, ct);
        await using (var prepare = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.lifecycle(generation_id,state)
              VALUES(@target,'verified') ON CONFLICT(generation_id) DO NOTHING;
            INSERT INTO marketplace_generation.pointer_intents(operation_id,kind,journal_id,expected_version,expected_active,
                target_generation,required_revision,run_id,issued_epoch)
            SELECT @operation,@kind,@journal,@version,@active,@target,@revision,@run,@epoch
            FROM marketplace_generation.active_pointer p
            JOIN marketplace_generation.lifecycle l ON l.generation_id=@target
            WHERE p.singleton AND p.journal_id=@journal AND p.version=@version
              AND p.active_generation IS NOT DISTINCT FROM @active
              AND (p.operation_id IS NULL OR EXISTS(SELECT 1 FROM marketplace_generation.pointer_intents prior
                   WHERE prior.operation_id=p.operation_id AND prior.state='completed'))
              AND ((@kind='cutover' AND l.state='verified') OR
                   (@kind='rollback' AND l.state='retained' AND p.previous_generation=@target))
              AND (p.active_generation IS NULL OR EXISTS(SELECT 1 FROM marketplace_generation.write_barriers b
                   WHERE b.generation_id=p.active_generation AND b.state='drained'))
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.write_intents i
                   WHERE i.generation_id IN (p.active_generation,@target) AND i.state='outstanding')
            """, db, tx))
        {
            Parameters(prepare, lease, operation, target, run, expectedVersion, expectedActive, rollback);
            prepare.Parameters.AddWithValue("revision", watermark);
            // The first statement can affect one row. Verify intent identity separately.
            await prepare.ExecuteNonQueryAsync(ct);
        }
        await using (var verify = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM marketplace_generation.pointer_intents WHERE operation_id=@operation)", db, tx))
        {
            verify.Parameters.AddWithValue("operation", operation);
            if (await verify.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Cutover/rollback eligibility or pointer CAS rejected.");
        }
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        injectFailure?.Invoke(CutoverBoundary.BeforeIntentCommit);
        await tx.CommitAsync(ct);
        injectFailure?.Invoke(CutoverBoundary.AfterIntentCommit);
    }

    internal static async Task CommitAsync(NpgsqlConnection db, GenerationLease lease, Guid operation,
        MarketplacePhysicalAdmission.Expected physical, CancellationToken ct = default, Action<CutoverBoundary>? injectFailure = null)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await LockPointerAsync(db, tx, ct);
        var intent = await ReadIntentAsync(db, tx, operation, ct);
        if (intent.Journal != lease.JournalId || physical.Generation != intent.Target || physical.Journal != intent.Journal)
            throw new InvalidDataException("Intent lineage mismatch.");
        if (intent.State is "completed" or "committed") { await tx.CommitAsync(ct); return; }
        if (intent.State != "intent") throw new InvalidOperationException("Intent no longer eligible.");
        // Revalidate the committed source boundary: an intent cannot hide writes
        // committed after readiness. Catch-up requires a fresh operation/receipt.
        await using (var gate = new NpgsqlCommand("SELECT marketplace_generation.lock_source_revision(@journal)", db, tx))
        {
            gate.Parameters.AddWithValue("journal", intent.Journal);
            if (await gate.ExecuteScalarAsync(ct) is not long revision || revision != intent.Revision)
                throw new InvalidOperationException("Source advanced after intent; catch-up and re-verification required.");
        }
        await RequireRefreshAsync(db, tx, intent.Journal, intent.Target, intent.Run, intent.Revision, physical, ct);
        await using (var pointer = new NpgsqlCommand("""
            UPDATE marketplace_generation.active_pointer p SET active_generation=@target,
                previous_generation=p.active_generation,version=p.version+1,operation_id=@operation
            WHERE p.singleton AND p.journal_id=@journal AND p.version=@version
              AND p.active_generation IS NOT DISTINCT FROM @active
              AND (@kind<>'rollback' OR p.previous_generation=@target)
              AND (p.active_generation IS NULL OR EXISTS(SELECT 1 FROM marketplace_generation.write_barriers b
                   WHERE b.generation_id=p.active_generation AND b.state='drained'))
              AND EXISTS(SELECT 1 FROM marketplace_generation.lifecycle l WHERE l.generation_id=@target
                   AND ((@kind='cutover' AND l.state='verified') OR (@kind='rollback' AND l.state='retained')))
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.write_intents i
                   WHERE i.generation_id IN (p.active_generation,@target) AND i.state='outstanding')
            """, db, tx))
        {
            IntentParameters(pointer, intent, operation);
            if (await pointer.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Authoritative pointer CAS rejected.");
        }
        injectFailure?.Invoke(CutoverBoundary.AfterPointerChangeBeforeCommit);
        await using (var states = new NpgsqlCommand("""
            UPDATE marketplace_generation.lifecycle SET state='retained' WHERE generation_id=@active AND state='active';
            UPDATE marketplace_generation.lifecycle SET state='active' WHERE generation_id=@target AND state IN ('verified','retained');
            UPDATE marketplace_generation.pointer_intents SET state='committed' WHERE operation_id=@operation AND state='intent'
            """, db, tx))
        {
            IntentParameters(states, intent, operation);
            await states.ExecuteNonQueryAsync(ct);
        }
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        // Pointer, lifecycle and operation outcome share one PostgreSQL commit.
        await tx.CommitAsync(ct);
        injectFailure?.Invoke(CutoverBoundary.AfterPointerCommit);
    }

    internal static async Task CompleteAsync(NpgsqlConnection db, Guid operation, CancellationToken ct = default, Action<CutoverBoundary>? injectFailure = null)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockPointerAsync(db, tx, ct);
        var intent = await ReadIntentAsync(db, tx, operation, ct);
        if (intent.State == "completed") { await tx.CommitAsync(ct); return; }
        injectFailure?.Invoke(CutoverBoundary.BeforeFinalAcknowledgement);
        await using var finish = new NpgsqlCommand("""
            UPDATE marketplace_generation.pointer_intents i SET state='completed'
            WHERE i.operation_id=@operation AND i.state='committed' AND EXISTS(
              SELECT 1 FROM marketplace_generation.active_pointer p WHERE p.singleton
                AND p.journal_id=i.journal_id AND p.operation_id=i.operation_id
                AND p.version=i.expected_version+1 AND p.active_generation=i.target_generation)
            """, db, tx);
        finish.Parameters.AddWithValue("operation", operation);
        if (await finish.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Pointer must reconcile exactly before final acknowledgement.");
        await tx.CommitAsync(ct);
    }

    internal static async Task<GenerationRecoveryAction> RecoverAsync(NpgsqlConnection db, Guid operation, CancellationToken ct = default)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockPointerAsync(db, tx, ct);
        var intent = await ReadIntentAsync(db, tx, operation, ct);
        await using var read = new NpgsqlCommand("SELECT version,active_generation,operation_id FROM marketplace_generation.active_pointer WHERE singleton AND journal_id=@journal", db, tx);
        read.Parameters.AddWithValue("journal", intent.Journal);
        GenerationRecoveryAction decision;
        await using (var rows = await read.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct)) throw new InvalidDataException("Authoritative pointer missing.");
            var outcome = intent.State switch { "intent" => GenerationPointerOutcome.Intent, "committed" => GenerationPointerOutcome.Committed,
                "completed" => GenerationPointerOutcome.Completed, _ => GenerationPointerOutcome.Superseded };
            decision = MarketplaceGenerationLifecycle.Recover(outcome, intent.Version, rows.GetInt64(0), intent.Target,
                rows.IsDBNull(1) ? null : rows.GetGuid(1), operation, rows.IsDBNull(2) ? null : rows.GetGuid(2));
        }
        if (decision == GenerationRecoveryAction.RejectForeignPointer)
        {
            // A losing intent is a durable terminal outcome, not a permanent
            // pending-retirement reference. Never supersede a committed outcome.
            await using var supersede = new NpgsqlCommand("UPDATE marketplace_generation.pointer_intents SET state='superseded' WHERE operation_id=@operation AND state='intent'", db, tx);
            supersede.Parameters.AddWithValue("operation", operation);
            await supersede.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return decision; // No hidden worker or automatic activation.
    }

    internal static async Task RequireRefreshAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid journal,
        Guid target, Guid run, long revision, MarketplacePhysicalAdmission.Expected physical, CancellationToken ct)
    {
        MarketplacePhysicalAdmission.Validate(physical);
        await using var query = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM marketplace_generation.refresh_observations r
              JOIN marketplace_generation.provider_bindings p ON p.generation_id=r.generation_id
              JOIN marketplace_generation.builds b ON b.generation_id=r.generation_id
              JOIN marketplace_generation.output_seals s ON s.run_id=r.run_id
              JOIN marketplace_generation.write_barriers w ON w.generation_id=b.generation_id
              WHERE r.run_id=@run AND r.generation_id=@target AND r.journal_id=@journal
                AND r.source_revision=@revision AND r.seal_digest=s.digest AND r.item_count=s.item_count
                AND r.cluster_uuid=@cluster AND r.physical_uuid=@uuid AND r.fingerprint=@fingerprint
                AND p.journal_id=r.journal_id AND p.cluster_uuid=r.cluster_uuid AND p.physical_uuid=r.physical_uuid
                AND p.fingerprint=r.fingerprint AND b.journal_id=r.journal_id AND b.physical_uuid=r.physical_uuid
                AND b.fingerprint=r.fingerprint AND b.physical_name=@name AND b.checkpoint_revision=r.source_revision
                AND b.checkpoint_ordinal=9223372036854775807 AND w.state='drained'
                AND NOT EXISTS(SELECT 1 FROM marketplace_generation.write_intents i
                    WHERE i.generation_id=@target AND i.state='outstanding'))
            """, db, tx);
        query.Parameters.AddWithValue("journal", journal); query.Parameters.AddWithValue("target", target);
        query.Parameters.AddWithValue("run", run); query.Parameters.AddWithValue("revision", revision);
        query.Parameters.AddWithValue("cluster", physical.ClusterUuid); query.Parameters.AddWithValue("uuid", physical.PhysicalUuid);
        query.Parameters.AddWithValue("fingerprint", physical.Fingerprint); query.Parameters.AddWithValue("name", physical.PhysicalName);
        if (await query.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException("Exact fully replayed, refreshed and permanently drained generation required.");
    }

    internal static async Task LockPointerAsync(NpgsqlConnection db, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT version FROM marketplace_generation.active_pointer WHERE singleton FOR UPDATE", db, tx);
        if (await command.ExecuteScalarAsync(ct) is not long) throw new InvalidDataException("Generation pointer missing.");
    }

    private sealed record Intent(Guid Journal, Guid Target, Guid Run, long Version, Guid? Active, long Revision, string Kind, string State);
    private static async Task<Intent> ReadIntentAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid operation, CancellationToken ct)
    {
        await using var read = new NpgsqlCommand("""
            SELECT journal_id,target_generation,run_id,expected_version,expected_active,required_revision,kind,state
            FROM marketplace_generation.pointer_intents WHERE operation_id=@operation FOR UPDATE
            """, db, tx);
        read.Parameters.AddWithValue("operation", operation);
        await using var rows = await read.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Durable pointer intent required.");
        return new(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetInt64(3), rows.IsDBNull(4) ? null : rows.GetGuid(4),
            rows.GetInt64(5), rows.GetString(6), rows.GetString(7));
    }

    private static void Parameters(NpgsqlCommand command, GenerationLease lease, Guid operation, Guid target, Guid run,
        long version, Guid? active, bool rollback)
    {
        command.Parameters.AddWithValue("journal", lease.JournalId); command.Parameters.AddWithValue("epoch", lease.Epoch);
        command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("run", run); command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("active", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)active ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", rollback ? "rollback" : "cutover");
    }
    private static void IntentParameters(NpgsqlCommand command, Intent intent, Guid operation)
    {
        command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("journal", intent.Journal);
        command.Parameters.AddWithValue("target", intent.Target); command.Parameters.AddWithValue("version", intent.Version);
        command.Parameters.AddWithValue("active", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)intent.Active ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", intent.Kind);
    }
}
