using Microsoft.EntityFrameworkCore;
using Npgsql;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

public interface IMarketplaceGenerationMutationRouter
{
    /// <summary>True means durably routed; the caller must not issue a legacy mutation.</summary>
    Task<bool> TryRouteAsync(Guid operation, string kind, Guid source, CancellationToken ct);
}

/// <summary>
/// Events are wake-up signals, never versioned search payloads. The transaction-bound
/// source journal remains authoritative, including mutations with no domain event.
/// An active pointer disables legacy scripts/deletes regardless of activation options.
/// </summary>
internal sealed class MarketplaceGenerationMutationRouter(ApplicationDbContext context) : IMarketplaceGenerationMutationRouter
{
    public async Task<bool> TryRouteAsync(Guid operation, string kind, Guid source, CancellationToken ct)
    {
        if (operation == Guid.Empty || source == Guid.Empty || kind is not ("service" or "provider" or "repair"))
            throw new ArgumentException("Exact event/source identity required.");
        if (!context.Database.IsNpgsql()) throw new InvalidOperationException("Durable PostgreSQL generation admission required.");
        await using var db = new NpgsqlConnection(context.Database.GetConnectionString());
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // The pointer lock serializes routing with cutover. A signal carries no
        // permission to write, and cannot become an external acknowledgement.
        await using var read = new NpgsqlCommand("SELECT marketplace_generation.lock_reader_pointer()", db, tx);
        if (await read.ExecuteScalarAsync(ct) is not long) throw new InvalidOperationException("Generation schema unavailable.");
        read.CommandText = "SELECT active_generation IS NOT NULL FROM marketplace_generation.active_pointer WHERE singleton";
        if (await read.ExecuteScalarAsync(ct) is not bool active) throw new InvalidOperationException("Generation pointer unavailable.");
        if (!active) { await tx.CommitAsync(ct); return false; }
        await using var signal = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.delivery_signals(operation_id,kind,source_id,journal_id,source_revision)
            SELECT @operation,@kind,@source,journal_id,revision FROM marketplace_journal.gate WHERE singleton
            ON CONFLICT(operation_id) DO NOTHING;
            SELECT kind=@kind AND source_id=@source FROM marketplace_generation.delivery_signals WHERE operation_id=@operation
            """, db, tx);
        signal.Parameters.AddWithValue("operation", operation); signal.Parameters.AddWithValue("kind", kind);
        signal.Parameters.AddWithValue("source", source);
        if (await signal.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Delivery signal identity cannot be rebound.");
        await tx.CommitAsync(ct);
        return true;
    }
}
