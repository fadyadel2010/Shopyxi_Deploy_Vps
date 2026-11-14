using System.Text.Json;
using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>Append-only operation identities/events; no remote client or deployment grants.</summary>
internal sealed class MarketplaceGenerationOperationStore(NpgsqlDataSource source) : IGenerationOperationStore
{
    public async Task<GenerationOperationSnapshot> AdmitAsync(GenerationOperation operation, CancellationToken ct)
    {
        operation.Validate();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var row = await AdmitAsync(db, tx, operation, ct);
        await tx.CommitAsync(ct);
        return row;
    }

    internal static async Task<GenerationOperationSnapshot> AdmitAsync(NpgsqlConnection db, NpgsqlTransaction tx, GenerationOperation operation, CancellationToken ct)
    {
        operation.Validate();
        await RequireDurableTransactionAsync(db, tx, ct);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.recovery_operations(operation_id,generation_id,service_id,source_revision,identity_json,request_digest)
            VALUES(@id,@generation,@service,@revision,@identity,@digest) ON CONFLICT(operation_id) DO NOTHING
            """, db, tx))
        {
            insert.Parameters.AddWithValue("id", operation.Id); insert.Parameters.AddWithValue("generation", operation.Target.Generation);
            insert.Parameters.AddWithValue("service", operation.Ticket.ServiceId); insert.Parameters.AddWithValue("revision", operation.Ticket.SourceRevision);
            insert.Parameters.AddWithValue("identity", JsonSerializer.Serialize(operation)); insert.Parameters.AddWithValue("digest", operation.RequestDigest);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var row = await ReadLockedAsync(db, tx, operation.Id, ct);
        MarketplaceGenerationOperationRecovery.RequireSame(operation, row.Operation);
        return row;
    }

    public async Task<GenerationOperationSnapshot> ReadAsync(Guid operation, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var row = await ReadLockedAsync(db, tx, operation, ct);
        await tx.CommitAsync(ct);
        return row;
    }

    internal async Task<GenerationOperation?> FindRevisionAsync(Guid generation, Guid service, long revision, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var query = new NpgsqlCommand("SELECT operation_id FROM marketplace_generation.recovery_operations WHERE generation_id=@generation AND service_id=@service AND source_revision=@revision", db, tx);
        query.Parameters.AddWithValue("generation", generation); query.Parameters.AddWithValue("service", service); query.Parameters.AddWithValue("revision", revision);
        var result = await query.ExecuteScalarAsync(ct) is Guid id ? (await ReadLockedAsync(db, tx, id, ct)).Operation : null;
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<GenerationOperationSnapshot> AdvanceAsync(GenerationOperationSnapshot expected, GenerationOperationState next, CancellationToken ct)
    {
        MarketplaceGenerationOperationRecovery.RequireTransition(expected.State, next);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await RequireDurableTransactionAsync(db, tx, ct);
        var row = await ReadLockedAsync(db, tx, expected.Operation.Id, ct);
        MarketplaceGenerationOperationRecovery.RequireSame(expected.Operation, row.Operation);
        if (row.Version != expected.Version || row.State != expected.State)
            throw new InvalidOperationException("Operation changed concurrently; reload durable state.");
        var version = checked(row.Version + 1);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.recovery_events(operation_id,version,state) VALUES(@id,@version,@state)
            """, db, tx);
        insert.Parameters.AddWithValue("id", row.Operation.Id); insert.Parameters.AddWithValue("version", version);
        insert.Parameters.AddWithValue("state", next.ToString()); await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return row with { Version = version, State = next };
    }

    internal static async Task<GenerationOperationSnapshot> ReadLockedAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        // The immutable identity row serializes ALL state appends, including across
        // processes. This is persistence concurrency, NOT a provider-global fence.
        await using var query = new NpgsqlCommand("""
            SELECT identity_json,request_digest FROM marketplace_generation.recovery_operations WHERE operation_id=@id FOR UPDATE
            """, db, tx);
        query.Parameters.AddWithValue("id", id);
        GenerationOperation operation;
        await using (var rows = await query.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Durable operation does not exist.");
            operation = JsonSerializer.Deserialize<GenerationOperation>(rows.GetString(0)) ?? throw new InvalidDataException("Missing immutable identity.");
            operation.Validate();
            if (operation.Id != id || operation.RequestDigest != rows.GetString(1)) throw new InvalidDataException("Corrupt operation identity/digest.");
        }
        await using var last = new NpgsqlCommand("""
            SELECT version,state FROM marketplace_generation.recovery_events WHERE operation_id=@id ORDER BY version DESC LIMIT 1
            """, db, tx);
        last.Parameters.AddWithValue("id", id);
        await using var events = await last.ExecuteReaderAsync(ct);
        return await events.ReadAsync(ct)
            ? new(operation, events.GetInt64(0), Enum.Parse<GenerationOperationState>(events.GetString(1), false))
            : new(operation, 0, GenerationOperationState.Pending);
    }

    internal static async Task RequireDurableTransactionAsync(NpgsqlConnection db, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var durability = new NpgsqlCommand("""
            SET LOCAL synchronous_commit='on';
            SELECT current_setting('fsync')='on' AND current_setting('full_page_writes')='on' AND NOT pg_is_in_recovery()
            """, db, tx);
        if (await durability.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException("GENERATION_DURABLE_PRIMARY_REQUIRED");
    }
}
