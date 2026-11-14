using System.Data;
using Npgsql;
using NpgsqlTypes;
using PointOfSale.Infrastructure.Marketplace.Journal;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Durable build ownership and source-input mirror, not yet an application coordinator.
/// Deliberately not registered in DI. No API here can activate/retire a generation or
/// authorize a search mutation. SQL-session loss does not cancel an in-flight HTTP write.
/// </summary>
internal static class MarketplaceGenerationRegistry
{
    internal static string SchemaSql
    {
        get
        {
            using var stream=typeof(MarketplaceGenerationRegistry).Assembly.GetManifestResourceStream(
                "PointOfSale.Infrastructure.Marketplace.Generation.MarketplaceGenerationRegistry.sql")!;
            using var reader=new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    internal static async Task InstallDisposableAsync(NpgsqlConnection connection,CancellationToken ct=default)
    {
        if(connection.State!=ConnectionState.Open || connection.Host!="app17c-pg" ||
            !connection.Database.StartsWith("app17h_journal_",StringComparison.Ordinal))
            throw new InvalidOperationException("Guarded disposable PostgreSQL database required.");
        await MarketplaceSourceJournal.VerifyCaptureInstalledAsync(connection,ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        await using var command=new NpgsqlCommand(SchemaSql+MarketplaceFrozenInputContract.BuildSchemaSql,connection,tx);
        await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    internal static async Task<GenerationLease?> TryAcquireAsync(NpgsqlConnection db,Guid owner,TimeSpan duration,CancellationToken ct=default)
    {
        ValidateLeaseRequest(owner,duration);
        await using var tx=await db.BeginTransactionAsync(ct);
        // Even an unchanged tuple lock can outlive a clock-dependent WHERE
        // predicate. Serialize ownership first; evaluate expiry afterwards in
        // a new statement, never from a pre-wait qualification.
        await using(var ownership=new NpgsqlCommand("SELECT epoch FROM marketplace_generation.control WHERE singleton FOR UPDATE",db,tx))
            await ownership.ExecuteScalarAsync(ct);
        await using var command=new NpgsqlCommand("""
            UPDATE marketplace_generation.control c SET epoch=c.epoch+1,owner_id=@owner,
                lease_until=clock_timestamp()+@duration
            WHERE c.singleton AND (c.owner_id IS NULL OR c.lease_until<=clock_timestamp())
                AND EXISTS(SELECT 1 FROM marketplace_journal.gate g WHERE g.singleton AND g.journal_id=c.journal_id)
            RETURNING c.journal_id,c.epoch,c.lease_until
            """,db,tx);
        command.Parameters.AddWithValue("owner",owner);command.Parameters.AddWithValue("duration",duration);
        GenerationLease? lease;
        await using(var rows=await command.ExecuteReaderAsync(ct))
            lease=await rows.ReadAsync(ct) ? new(rows.GetGuid(0),owner,rows.GetInt64(1),rows.GetDateTime(2)) : null;
        await tx.CommitAsync(ct);return lease;
    }

    internal static async Task<GenerationLease> RenewAsync(NpgsqlConnection db,GenerationLease lease,TimeSpan duration,CancellationToken ct=default)
    {
        ValidateLeaseRequest(lease.Owner,duration);
        await using var tx=await db.BeginTransactionAsync(ct);
        await RequireLeaseAsync(db,tx,lease,true,ct);
        await using var command=new NpgsqlCommand("""
            UPDATE marketplace_generation.control c SET lease_until=clock_timestamp()+@duration
            WHERE c.singleton AND c.journal_id=@journal AND c.owner_id=@owner AND c.epoch=@epoch
                AND c.lease_until>clock_timestamp()
                AND EXISTS(SELECT 1 FROM marketplace_journal.gate g WHERE g.singleton AND g.journal_id=c.journal_id)
            RETURNING c.lease_until
            """,db,tx);
        LeaseParameters(command,lease);command.Parameters.AddWithValue("duration",duration);
        var renewed=await command.ExecuteScalarAsync(ct) is DateTime expires
            ? lease with { ExpiresAtUtc=expires } : throw new InvalidOperationException("Generation lease renewal rejected.");
        await RequireLeaseAsync(db,tx,renewed,false,ct);
        await tx.CommitAsync(ct);return renewed;
    }

    internal static async Task ReleaseAsync(NpgsqlConnection db,GenerationLease lease,CancellationToken ct=default)
    {
        await using var tx=await db.BeginTransactionAsync(ct);
        await RequireLeaseAsync(db,tx,lease,true,ct);
        await using var command=new NpgsqlCommand("""
            UPDATE marketplace_generation.control SET owner_id=NULL,lease_until=NULL
            WHERE singleton AND journal_id=@journal AND owner_id=@owner AND epoch=@epoch AND lease_until>clock_timestamp()
            """,db,tx);
        LeaseParameters(command,lease);
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Generation lease release rejected.");
        await tx.CommitAsync(ct);
    }

    internal static async Task<GenerationBuild> CreateBuildAsync(NpgsqlConnection db,GenerationLease lease,Guid operation,CancellationToken ct=default)
    {
        if(operation==Guid.Empty)throw new ArgumentException("Operation identity required.",nameof(operation));
        await using var tx=await db.BeginTransactionAsync(ct);
        await RequireLeaseAsync(db,tx,lease,true,ct);
        var id=Guid.NewGuid();
        await using(var command=new NpgsqlCommand("""
            INSERT INTO marketplace_generation.builds(generation_id,operation_id,journal_id,physical_name,created_epoch,last_epoch)
            VALUES(@id,@operation,@journal,@name,@epoch,@epoch) ON CONFLICT(operation_id) DO NOTHING
            """,db,tx))
        {
            command.Parameters.AddWithValue("id",id);command.Parameters.AddWithValue("operation",operation);
            command.Parameters.AddWithValue("journal",lease.JournalId);command.Parameters.AddWithValue("epoch",lease.Epoch);
            command.Parameters.AddWithValue("name","marketplace_services_g_"+id.ToString("N"));
            await command.ExecuteNonQueryAsync(ct);
        }
        await using var read=new NpgsqlCommand("""
            SELECT generation_id,physical_name,journal_id FROM marketplace_generation.builds WHERE operation_id=@operation
            """,db,tx);
        read.Parameters.AddWithValue("operation",operation);
        GenerationBuild build;
        await using(var rows=await read.ExecuteReaderAsync(ct))
        {
            if(!await rows.ReadAsync(ct) || rows.GetGuid(2)!=lease.JournalId)throw new InvalidDataException("Build lineage rejected.");
            build=new(rows.GetGuid(0),operation,rows.GetString(1));
        }
        await RequireLeaseAsync(db,tx,lease,false,ct);
        await tx.CommitAsync(ct);return build;
    }

    // Persist an independently observed server identity. This does NOT verify the server
    // or make its mapping/UUID safe; admission of external writers remains unimplemented.
    internal static async Task BindPhysicalIdentityAsync(NpgsqlConnection db,GenerationLease lease,Guid generation,
        string uuid,string fingerprint,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(uuid) || uuid.Length>200 || uuid=="_na_" || fingerprint.Length!=64 ||
            fingerprint.Any(c=>!char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))throw new ArgumentException("Physical identity required.");
        await using var tx=await db.BeginTransactionAsync(ct);
        await RequireLeaseAsync(db,tx,lease,true,ct);
        await using var command=new NpgsqlCommand("""
            UPDATE marketplace_generation.builds SET physical_uuid=@uuid,fingerprint=@fingerprint,last_epoch=@epoch
            WHERE generation_id=@generation AND journal_id=@journal
              AND (physical_uuid IS NULL OR (physical_uuid=@uuid AND fingerprint=@fingerprint))
            """,db,tx);
        command.Parameters.AddWithValue("uuid",uuid);command.Parameters.AddWithValue("fingerprint",fingerprint);
        command.Parameters.AddWithValue("generation",generation);command.Parameters.AddWithValue("journal",lease.JournalId);
        command.Parameters.AddWithValue("epoch",lease.Epoch);
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Immutable physical identity binding rejected.");
        await RequireLeaseAsync(db,tx,lease,false,ct);await tx.CommitAsync(ct);
    }

    internal static async Task<JournalPosition> SnapshotAsync(NpgsqlConnection db,GenerationLease lease,Guid generation,CancellationToken ct=default)
    {
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        // Boundary and all eight source copies use the same MVCC snapshot. A lost
        // connection rolls back the entire copy; no old snapshot cursor can be resumed.
        var sourceSchema=await MarketplaceFrozenInputContract.ReadSourceFingerprintAsync(db,tx,ct);
        var boundary=await MarketplaceSourceJournal.ReadSnapshotBoundaryAsync(db,tx,ct);
        if(boundary.JournalId!=lease.JournalId)throw new InvalidDataException("Source journal changed.");
        await RequireLeaseAsync(db,tx,lease,true,ct);
        await using(var transition=new NpgsqlCommand("""
            UPDATE marketplace_generation.builds SET state='snapshot-ready',snapshot_revision=@revision,
                checkpoint_revision=@revision,checkpoint_ordinal=9223372036854775807,last_epoch=@epoch,source_schema_fingerprint=@schema
            WHERE generation_id=@generation AND journal_id=@journal AND state='new' AND physical_uuid IS NOT NULL
            """,db,tx))
        {
            transition.Parameters.AddWithValue("revision",boundary.Revision);transition.Parameters.AddWithValue("epoch",lease.Epoch);
            transition.Parameters.AddWithValue("generation",generation);transition.Parameters.AddWithValue("journal",lease.JournalId);
            transition.Parameters.AddWithValue("schema",sourceSchema);
            if(await transition.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Snapshot transition rejected; restart an incomplete snapshot, never resume its cursor.");
        }
        foreach(var table in MarketplaceSourceJournal.SourceTables)
        {
            await using var copy=new NpgsqlCommand($"""
                INSERT INTO marketplace_generation.inputs(generation_id,source_table,source_id,revision,ordinal,deleted,body)
                SELECT @generation,@table,s."Id",@revision,9223372036854775807,false,to_jsonb(s) FROM public."{table}" s
                """,db,tx);
            copy.Parameters.AddWithValue("generation",generation);copy.Parameters.AddWithValue("table",table);
            copy.Parameters.AddWithValue("revision",boundary.Revision);await copy.ExecuteNonQueryAsync(ct);
        }
        await RequireLeaseAsync(db,tx,lease,false,ct);await tx.CommitAsync(ct);
        return new(boundary.Revision,long.MaxValue);
    }

    internal static async Task<JournalPosition> ReplayPageAsync(NpgsqlConnection db,GenerationLease lease,Guid generation,
        JournalPosition expected,long through,int limit,CancellationToken ct=default)
    {
        await using var tx=await db.BeginTransactionAsync(ct);
        await RequireLeaseAsync(db,tx,lease,true,ct);
        var sourceSchema=await MarketplaceFrozenInputContract.ReadSourceFingerprintAsync(db,tx,ct);
        await using(var current=new NpgsqlCommand("""
            SELECT checkpoint_revision,checkpoint_ordinal,source_schema_fingerprint FROM marketplace_generation.builds
            WHERE generation_id=@generation AND journal_id=@journal AND state IN ('snapshot-ready','replaying') FOR UPDATE
            """,db,tx))
        {
            current.Parameters.AddWithValue("generation",generation);current.Parameters.AddWithValue("journal",lease.JournalId);
            await using var reader=await current.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct) || new JournalPosition(reader.GetInt64(0),reader.GetInt64(1))!=expected)
                throw new InvalidOperationException("Generation checkpoint compare-and-swap rejected.");
            if(reader.IsDBNull(2) || reader.GetString(2)!=sourceSchema)
                throw new InvalidDataException("Generation source schema changed or unknown; restart the build, never mix row formats.");
        }
        var changes=await MarketplaceSourceJournal.ReadPageAsync(db,lease.JournalId,expected,through,limit,ct);
        foreach(var change in changes)
        {
            if(change.OldSourceId is Guid old && old!=change.SourceId)
                await ApplyInputAsync(db,tx,generation,change,old,true,change.OldRow!,ct);
            await ApplyInputAsync(db,tx,generation,change,change.SourceId,change.NewRow==null,change.NewRow??change.OldRow!,ct);
            await using var work=new NpgsqlCommand("""
                INSERT INTO marketplace_generation.invalidations(generation_id,revision,ordinal,source_table,old_row,new_row)
                VALUES(@generation,@revision,@ordinal,@table,@old,@new)
                """,db,tx);
            work.Parameters.AddWithValue("generation",generation);work.Parameters.AddWithValue("revision",change.Position.Revision);
            work.Parameters.AddWithValue("ordinal",change.Position.Ordinal);work.Parameters.AddWithValue("table",change.Table);
            work.Parameters.AddWithValue("old",NpgsqlDbType.Jsonb,(object?)change.OldRow??DBNull.Value);
            work.Parameters.AddWithValue("new",NpgsqlDbType.Jsonb,(object?)change.NewRow??DBNull.Value);
            await work.ExecuteNonQueryAsync(ct);
        }
        var next=changes.Count<limit ? new JournalPosition(through,long.MaxValue) : changes[^1].Position;
        await using(var checkpoint=new NpgsqlCommand("""
            UPDATE marketplace_generation.builds SET state='replaying',checkpoint_revision=@revision,checkpoint_ordinal=@ordinal,last_epoch=@epoch
            WHERE generation_id=@generation AND journal_id=@journal
            """,db,tx))
        {
            checkpoint.Parameters.AddWithValue("generation",generation);checkpoint.Parameters.AddWithValue("journal",lease.JournalId);
            checkpoint.Parameters.AddWithValue("revision",next.Revision);checkpoint.Parameters.AddWithValue("ordinal",next.Ordinal);
            checkpoint.Parameters.AddWithValue("epoch",lease.Epoch);
            if(await checkpoint.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Build checkpoint missing.");
        }
        await RequireLeaseAsync(db,tx,lease,false,ct);await tx.CommitAsync(ct);return next;
    }

    private static async Task ApplyInputAsync(NpgsqlConnection db,NpgsqlTransaction tx,Guid generation,SourceChange change,
        Guid sourceId,bool deleted,string body,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("""
            INSERT INTO marketplace_generation.inputs AS target(generation_id,source_table,source_id,revision,ordinal,deleted,body)
            VALUES(@generation,@table,@source,@revision,@ordinal,@deleted,@body)
            ON CONFLICT(generation_id,source_table,source_id) DO UPDATE
            SET revision=excluded.revision,ordinal=excluded.ordinal,deleted=excluded.deleted,body=excluded.body
            WHERE (target.revision,target.ordinal)<(excluded.revision,excluded.ordinal)
            """,db,tx);
        command.Parameters.AddWithValue("generation",generation);command.Parameters.AddWithValue("table",change.Table);
        command.Parameters.AddWithValue("source",sourceId);command.Parameters.AddWithValue("revision",change.Position.Revision);
        command.Parameters.AddWithValue("ordinal",change.Position.Ordinal);command.Parameters.AddWithValue("deleted",deleted);
        command.Parameters.AddWithValue("body",NpgsqlDbType.Jsonb,body);await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task RequireLeaseAsync(NpgsqlConnection db,NpgsqlTransaction tx,GenerationLease lease,bool lockRow,CancellationToken ct)
    {
        if(lockRow)
        {
            await using var ownership=new NpgsqlCommand("""
                SELECT c.epoch FROM marketplace_generation.control c WHERE c.singleton AND c.journal_id=@journal
                    AND c.owner_id=@owner AND c.epoch=@epoch
                    AND EXISTS(SELECT 1 FROM marketplace_journal.gate g WHERE g.singleton AND g.journal_id=c.journal_id)
                FOR UPDATE OF c
                """,db,tx);
            LeaseParameters(ownership,lease);
            if(await ownership.ExecuteScalarAsync(ct) is not long owned || owned!=lease.Epoch)
                throw new InvalidOperationException("Generation ownership/epoch/database-clock lease rejected.");
        }
        // This independent statement evaluates the actual clock after any
        // ownership wait. Do not combine it with the locking SELECT above.
        var sql="""
            SELECT c.epoch FROM marketplace_generation.control c WHERE c.singleton AND c.journal_id=@journal
                AND c.owner_id=@owner AND c.epoch=@epoch AND c.lease_until>clock_timestamp()
                AND EXISTS(SELECT 1 FROM marketplace_journal.gate g WHERE g.singleton AND g.journal_id=c.journal_id)
            """;
        await using var command=new NpgsqlCommand(sql,db,tx);LeaseParameters(command,lease);
        if(await command.ExecuteScalarAsync(ct) is not long epoch || epoch!=lease.Epoch)
            throw new InvalidOperationException("Generation ownership/epoch/database-clock lease rejected.");
    }

    private static void LeaseParameters(NpgsqlCommand command,GenerationLease lease)
    {
        if(lease.JournalId==Guid.Empty || lease.Owner==Guid.Empty || lease.Epoch<=0)throw new ArgumentException("Invalid generation lease.");
        command.Parameters.AddWithValue("journal",lease.JournalId);command.Parameters.AddWithValue("owner",lease.Owner);
        command.Parameters.AddWithValue("epoch",lease.Epoch);
    }

    private static void ValidateLeaseRequest(Guid owner,TimeSpan duration)
    {
        if(owner==Guid.Empty || duration<TimeSpan.FromSeconds(1) || duration>TimeSpan.FromMinutes(2))
            throw new ArgumentException("Owner and bounded lease duration required.");
    }
}

internal sealed record GenerationLease(Guid JournalId,Guid Owner,long Epoch,DateTime ExpiresAtUtc);
internal sealed record GenerationBuild(Guid GenerationId,Guid OperationId,string PhysicalName);
