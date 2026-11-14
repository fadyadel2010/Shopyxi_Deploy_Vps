using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal sealed record GenerationReaderPin(Guid PinId, string Secret, Guid Journal, Guid Generation,
    long PointerVersion, Guid Tenant, Guid Reader, string QueryHash, DateTime ExpiresAtUtc, string PhysicalName);

/// <summary>
/// Opaque durable continuation identity. The query hash includes canonical filters,
/// ordering and cursor format version. Tenant and authenticated reader are supplied
/// by trusted request context, never accepted from cursor claims alone.
/// </summary>
internal static class MarketplaceGenerationReaderPins
{
    internal static async Task<GenerationReaderPin> AcquireAsync(NpgsqlConnection db, Guid tenant, Guid reader,
        string queryHash, TimeSpan lifetime, CancellationToken ct = default)
    {
        ValidateScope(tenant, reader, queryHash);
        if (lifetime < TimeSpan.FromSeconds(1) || lifetime > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        var id = Guid.NewGuid();
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockReaderPointerAsync(db, tx, ct);
        await using var acquire = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.reader_pins(pin_id,secret_hash,generation_id,journal_id,pointer_version,
                tenant_id,reader_id,query_hash,expires_at)
            SELECT @pin,@secret,p.active_generation,p.journal_id,p.version,@tenant,@reader,@query,clock_timestamp()+@lifetime
            FROM marketplace_generation.active_pointer p
            JOIN marketplace_generation.lifecycle l ON l.generation_id=p.active_generation AND l.state='active'
            WHERE p.singleton
            RETURNING journal_id,generation_id,pointer_version,expires_at
            """, db, tx);
        acquire.Parameters.AddWithValue("pin", id); acquire.Parameters.AddWithValue("secret", Hash(secret));
        acquire.Parameters.AddWithValue("tenant", tenant); acquire.Parameters.AddWithValue("reader", reader);
        acquire.Parameters.AddWithValue("query", queryHash); acquire.Parameters.AddWithValue("lifetime", lifetime);
        GenerationReaderPin pin;
        await using (var rows = await acquire.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Verified active generation pointer required; no legacy alias fallback.");
            var generation = rows.GetGuid(1);
            pin = new(id, secret, rows.GetGuid(0), generation, rows.GetInt64(2), tenant, reader, queryHash,
                rows.GetDateTime(3), "marketplace_services_g_" + generation.ToString("N"));
        }
        await tx.CommitAsync(ct);
        return pin;
    }

    internal static async Task<Guid> AdmitAsync(NpgsqlConnection db, GenerationReaderPin pin, Guid tenant,
        Guid reader, string queryHash, CancellationToken ct = default)
    {
        Validate(pin, tenant, reader, queryHash);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockReaderPointerAsync(db, tx, ct);
        await using var admit = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.reader_operations(operation_id,pin_id)
            SELECT @operation,r.pin_id FROM marketplace_generation.reader_pins r
            JOIN marketplace_generation.lifecycle l ON l.generation_id=r.generation_id
            WHERE r.pin_id=@pin AND r.secret_hash=@secret AND r.generation_id=@generation AND r.journal_id=@journal
              AND r.pointer_version=@version AND r.tenant_id=@tenant AND r.reader_id=@reader AND r.query_hash=@query
              AND r.released_at IS NULL AND r.expires_at>clock_timestamp() AND l.state IN ('active','retained')
            RETURNING operation_id
            """, db, tx);
        var operation = Guid.NewGuid();
        PinParameters(admit, pin); admit.Parameters.AddWithValue("operation", operation);
        if (await admit.ExecuteScalarAsync(ct) is not Guid accepted || accepted != operation)
            throw new InvalidOperationException("Reader pin expired, released, foreign or generation retiring.");
        await tx.CommitAsync(ct);
        return operation;
    }

    internal static async Task CompleteAsync(NpgsqlConnection db, GenerationReaderPin pin, Guid operation,
        CancellationToken ct = default)
    {
        Validate(pin, pin.Tenant, pin.Reader, pin.QueryHash);
        await using var command = new NpgsqlCommand("""
            UPDATE marketplace_generation.reader_operations o SET completed_at=COALESCE(o.completed_at,clock_timestamp())
            FROM marketplace_generation.reader_pins r WHERE o.operation_id=@operation AND o.pin_id=r.pin_id
              AND r.pin_id=@pin AND r.secret_hash=@secret AND r.generation_id=@generation AND r.journal_id=@journal
              AND r.pointer_version=@version AND r.tenant_id=@tenant AND r.reader_id=@reader AND r.query_hash=@query
            """, db);
        PinParameters(command, pin); command.Parameters.AddWithValue("operation", operation);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Exact admitted reader required.");
    }

    internal static async Task ReleaseAsync(NpgsqlConnection db, GenerationReaderPin pin, CancellationToken ct = default)
    {
        Validate(pin, pin.Tenant, pin.Reader, pin.QueryHash);
        await using var command = new NpgsqlCommand("""
            UPDATE marketplace_generation.reader_pins SET released_at=COALESCE(released_at,clock_timestamp())
            WHERE pin_id=@pin AND secret_hash=@secret AND generation_id=@generation AND journal_id=@journal
              AND pointer_version=@version AND tenant_id=@tenant AND reader_id=@reader AND query_hash=@query
            """, db);
        PinParameters(command, pin);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Exact reader pin required.");
        // Releasing an abandoned pin does NOT complete outstanding reader operations.
    }

    internal static void Validate(GenerationReaderPin pin, Guid tenant, Guid reader, string queryHash)
    {
        ValidateScope(tenant, reader, queryHash);
        if (pin.PinId == Guid.Empty || pin.Journal == Guid.Empty || pin.Generation == Guid.Empty || pin.PointerVersion <= 0 ||
            !Digest(pin.Secret) || pin.Tenant != tenant || pin.Reader != reader || pin.QueryHash != queryHash ||
            pin.PhysicalName != "marketplace_services_g_" + pin.Generation.ToString("N"))
            throw new InvalidOperationException("Invalid or foreign generation reader pin.");
    }
    private static void ValidateScope(Guid tenant, Guid reader, string queryHash)
    {
        if (tenant == Guid.Empty || reader == Guid.Empty || !Digest(queryHash))
            throw new ArgumentException("Trusted tenant, reader and canonical query digest required.");
    }
    private static bool Digest(string? value) => value is { Length: 64 } &&
        value.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void PinParameters(NpgsqlCommand command, GenerationReaderPin pin)
    {
        command.Parameters.AddWithValue("pin", pin.PinId); command.Parameters.AddWithValue("secret", Hash(pin.Secret));
        command.Parameters.AddWithValue("generation", pin.Generation); command.Parameters.AddWithValue("journal", pin.Journal);
        command.Parameters.AddWithValue("version", pin.PointerVersion); command.Parameters.AddWithValue("tenant", pin.Tenant);
        command.Parameters.AddWithValue("reader", pin.Reader); command.Parameters.AddWithValue("query", pin.QueryHash);
    }

    private static async Task LockReaderPointerAsync(NpgsqlConnection db, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT marketplace_generation.lock_reader_pointer()", db, tx);
        if (await command.ExecuteScalarAsync(ct) is not long) throw new InvalidDataException("Generation pointer missing.");
    }
}
