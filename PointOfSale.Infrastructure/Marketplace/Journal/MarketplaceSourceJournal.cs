using System.Data;
using System.Text.Json;
using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Journal;

/// <summary>
/// Candidate-only PostgreSQL source journal contract. Not registered with application DI,
/// not a generation coordinator, and never installed automatically by startup/EnsureCreated.
/// </summary>
internal static class MarketplaceSourceJournal
{
    internal static readonly IReadOnlyList<string> SourceTables = Array.AsReadOnly(new[]
    {
        "Services", "MarketplaceProfiles", "ServiceRequests", "ServiceReviews",
        "MarketingReviewEvidences", "ProviderTrustAggregates", "Tenants", "UserGovernanceProfiles"
    });

    internal static string SchemaSql
    {
        get
        {
            using var stream = typeof(MarketplaceSourceJournal).Assembly.GetManifestResourceStream(
                "PointOfSale.Infrastructure.Marketplace.Journal.MarketplaceSourceJournal.sql")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    internal static async Task VerifyCaptureInstalledAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        // Exact implementation identity matters: trigger names and enabled flags alone do
        // not detect a replaced SECURITY DEFINER function. This is tamper detection, not
        // protection from a privileged owner capable of rewriting the identity table too.
        await using (var fingerprint = new NpgsqlCommand("""
            SELECT count(*)=3 AND bool_and(i.definition=pg_catalog.pg_get_functiondef(p.oid))
            FROM marketplace_journal.capture_identity i
            JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(i.signature)
            """, connection))
        {
            if (await fingerprint.ExecuteScalarAsync(ct) is not true)
                throw new InvalidDataException("Marketplace capture implementation identity rejected.");
        }
        const string sql = """
            SELECT count(*) FROM pg_catalog.pg_trigger t
            JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
            JOIN pg_catalog.pg_namespace fn ON fn.oid=p.pronamespace
            WHERE n.nspname='public' AND c.relname=ANY(@tables) AND c.relkind='r'
              AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)
              -- An earlier AFTER-row mutator can recursively change the same row before its outer
              -- preimage is captured. Reject unreviewed trigger interactions rather than invent order.
              AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_trigger other WHERE other.tgrelid=c.oid
                AND NOT other.tgisinternal AND other.tgname<>'marketplace_capture'
                AND (other.tgtype & 1)=1 AND (other.tgtype & 2)=0 AND (other.tgtype & 64)=0)
              AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_constraint fk WHERE fk.contype='f'
                AND fk.conrelid=c.oid AND fk.confrelid=c.oid)
              AND t.tgenabled='A' AND NOT t.tgisinternal AND fn.nspname='marketplace_journal'
              AND ((t.tgname='marketplace_gate' AND p.proname='enter_gate' AND t.tgtype=30)
                OR (t.tgname='marketplace_capture' AND p.proname='capture_row' AND t.tgtype=29)
                OR (t.tgname='marketplace_no_truncate' AND p.proname='reject_truncate' AND t.tgtype=34))
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tables", SourceTables.ToArray());
        if ((long)(await command.ExecuteScalarAsync(ct))! != SourceTables.Count * 3)
            throw new InvalidDataException("Marketplace source capture incomplete or topology unsupported.");
    }

    // Only an explicitly named disposable database can receive the unapproved schema module.
    internal static async Task InstallDisposableAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        if (!connection.Database.StartsWith("app17h_journal_", StringComparison.Ordinal) ||
            connection.State != ConnectionState.Open || connection.Host is not "app17c-pg")
            throw new InvalidOperationException("Guarded disposable PostgreSQL database required.");
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand(SchemaSql, connection, tx);
        command.Parameters.AddWithValue("journal_id", Guid.NewGuid());
        await command.ExecuteNonQueryAsync(ct);
        await VerifyCaptureInstalledAsync(connection, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Must be the first source read in a REPEATABLE READ transaction. The visible gate and all
    /// source reads share one MVCC snapshot. Never use MAX(ordinal), time, or allocation order.
    /// Keep this transaction alive for the complete snapshot; on connection loss restart it.
    /// </summary>
    internal static async Task<JournalBoundary> ReadSnapshotBoundaryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct = default)
    {
        if (transaction.IsolationLevel != IsolationLevel.RepeatableRead)
            throw new InvalidOperationException("Repeatable-read snapshot required.");
        await VerifyCaptureInstalledAsync(connection, ct);
        await using var command = new NpgsqlCommand(
            "SELECT journal_id,revision FROM marketplace_journal.gate WHERE singleton", connection, transaction);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new InvalidDataException("Journal gate missing.");
        return new(rows.GetGuid(0), rows.GetInt64(1));
    }

    // Internal global-source API, NOT tenant-facing. Tenant consumers must process both old/new
    // ownership, not WHERE new TenantId = scope (which would silently miss outgoing tombstones).
    internal static async Task<IReadOnlyList<SourceChange>> ReadPageAsync(NpgsqlConnection connection,
        Guid journalId, JournalPosition after, long throughRevision, int limit, CancellationToken ct = default)
    {
        if (journalId == Guid.Empty || after.Revision < 0 || after.Ordinal < 0 ||
            throughRevision < after.Revision || limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await VerifyCaptureInstalledAsync(connection, ct);
        await using (var identity = new NpgsqlCommand(
            "SELECT journal_id,revision FROM marketplace_journal.gate WHERE singleton", connection))
        await using (var gate = await identity.ExecuteReaderAsync(ct))
        {
            if (!await gate.ReadAsync(ct) || gate.GetGuid(0) != journalId || throughRevision > gate.GetInt64(1))
                throw new InvalidDataException("Journal identity or committed upper bound rejected.");
        }
        const string sql = """
            SELECT g.journal_id,c.revision,c.ordinal,c.source_table,c.source_id,c.old_source_id,
                   c.operation,c.old_row::text,c.new_row::text
            FROM marketplace_journal.changes c CROSS JOIN marketplace_journal.gate g
            WHERE g.singleton AND g.journal_id=@journal AND (c.revision,c.ordinal)>(@revision,@ordinal)
                AND c.revision<=@through ORDER BY c.revision,c.ordinal LIMIT @limit
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("journal", journalId);
        command.Parameters.AddWithValue("revision", after.Revision);
        command.Parameters.AddWithValue("ordinal", after.Ordinal);
        command.Parameters.AddWithValue("through", throughRevision);
        command.Parameters.AddWithValue("limit", limit);
        var result = new List<SourceChange>();
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            var change = new SourceChange(rows.GetGuid(0), new(rows.GetInt64(1), rows.GetInt64(2)),
                rows.GetString(3), rows.GetGuid(4), rows.IsDBNull(5) ? null : rows.GetGuid(5), rows.GetString(6),
                rows.IsDBNull(7) ? null : rows.GetString(7), rows.IsDBNull(8) ? null : rows.GetString(8));
            change.Validate(journalId);
            result.Add(change);
        }
        return result;
    }

    // Consumer-owned transaction must atomically persist its local effects and this checkpoint.
    // External-index acknowledgement alone cannot justify advancing it.
    internal static async Task SaveCheckpointAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string consumer, Guid journalId, JournalPosition expected, JournalPosition next, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(consumer) || consumer.Length > 120 || journalId == Guid.Empty ||
            expected.Revision < 0 || expected.Ordinal < 0 || next.Revision < 0 || next.Ordinal < 0 || next.CompareTo(expected) < 0)
            throw new ArgumentException("Invalid checkpoint transition.");
        await using (var valid = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM marketplace_journal.gate g WHERE g.journal_id=@journal
              AND @revision<=g.revision AND (@ordinal=9223372036854775807::bigint OR EXISTS(
                SELECT 1 FROM marketplace_journal.changes c WHERE c.revision=@revision AND c.ordinal=@ordinal)))
            """, connection, tx))
        {
            valid.Parameters.AddWithValue("journal",journalId);
            valid.Parameters.AddWithValue("revision",next.Revision);
            valid.Parameters.AddWithValue("ordinal",next.Ordinal);
            if (!(bool)(await valid.ExecuteScalarAsync(ct))!)
                throw new InvalidOperationException("Checkpoint is not a committed journal position.");
        }
        const string sql = """
            INSERT INTO marketplace_journal.checkpoints(consumer,journal_id,revision,ordinal)
            SELECT @consumer,@journal,@next_revision,@next_ordinal
            WHERE EXISTS (SELECT 1 FROM marketplace_journal.gate WHERE journal_id=@journal)
              AND (@revision,@ordinal)=(0::bigint,0::bigint)
            ON CONFLICT(consumer) DO NOTHING;
            UPDATE marketplace_journal.checkpoints SET revision=@next_revision,ordinal=@next_ordinal
            WHERE consumer=@consumer AND journal_id=@journal AND (revision,ordinal)=(@revision,@ordinal)
            """;
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("consumer", consumer); cmd.Parameters.AddWithValue("journal", journalId);
        cmd.Parameters.AddWithValue("revision", expected.Revision); cmd.Parameters.AddWithValue("ordinal", expected.Ordinal);
        cmd.Parameters.AddWithValue("next_revision", next.Revision); cmd.Parameters.AddWithValue("next_ordinal", next.Ordinal);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Checkpoint compare-and-swap rejected.");
    }
}

internal readonly record struct JournalBoundary(Guid JournalId, long Revision);
internal readonly record struct JournalPosition(long Revision, long Ordinal) : IComparable<JournalPosition>
{
    public int CompareTo(JournalPosition other) => Revision == other.Revision
        ? Ordinal.CompareTo(other.Ordinal) : Revision.CompareTo(other.Revision);
}

internal sealed record SourceChange(Guid JournalId, JournalPosition Position, string Table, Guid SourceId,
    Guid? OldSourceId, string Operation, string? OldRow, string? NewRow)
{
    internal void Validate(Guid expectedJournal)
    {
        if (JournalId == Guid.Empty || JournalId != expectedJournal || Position.Revision <= 0 || Position.Ordinal <= 0 ||
            !MarketplaceSourceJournal.SourceTables.Contains(Table) || SourceId == Guid.Empty ||
            Operation is not ("INSERT" or "UPDATE" or "DELETE") ||
            (Operation == "INSERT" ? OldRow != null || NewRow == null :
                Operation == "DELETE" ? OldRow == null || NewRow != null : OldRow == null || NewRow == null))
            throw new InvalidDataException("Invalid source journal record.");
        if (OldRow != null && Identity(OldRow) != OldSourceId || NewRow != null && Identity(NewRow) != SourceId ||
            Operation == "DELETE" && OldSourceId != SourceId || Operation == "INSERT" && OldSourceId != null)
            throw new InvalidDataException("Source journal identity mismatch.");
        foreach (var row in new[] { OldRow, NewRow }.Where(r => r != null))
        {
            using var json = JsonDocument.Parse(row!);
            var required = Table switch
            {
                "Services" => new[] { "TenantId", "ProfileId" },
                "ServiceRequests" => new[] { "TenantId", "ServiceId", "ProviderProfileId" },
                "ServiceReviews" => new[] { "TenantId", "ServiceId", "ServiceRequestId" },
                "MarketingReviewEvidences" => new[] { "TenantId", "ServiceReviewId" },
                "ProviderTrustAggregates" => new[] { "TenantId", "ProviderProfileId" },
                "UserGovernanceProfiles" => new[] { "SubjectId" },
                "Tenants" => Array.Empty<string>(),
                _ => new[] { "TenantId" }
            };
            foreach (var field in required)
                if (!json.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String ||
                    !value.TryGetGuid(out var id) || id == Guid.Empty)
                    throw new InvalidDataException($"Source journal relationship missing: {Table}.{field}.");
        }
    }

    private static Guid Identity(string row)
    {
        try
        {
            using var json = JsonDocument.Parse(row);
            return json.RootElement.GetProperty("Id").GetGuid();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Source journal identity missing or malformed.", error);
        }
    }
}
