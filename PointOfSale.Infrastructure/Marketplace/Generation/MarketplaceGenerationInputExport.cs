using System.Data;
using Npgsql;
using PointOfSale.Infrastructure.Marketplace.Journal;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Frozen, reconnect-pageable inputs at one COMPLETE replay boundary. No live public
/// source reads, semantic projection, external write, fan-out acknowledgement or DI.
/// Owner/superuser tampering and production permissions remain independent obligations.
/// </summary>
internal static class MarketplaceGenerationInputExport
{
    internal static string SchemaSql
    {
        get
        {
            using var stream=typeof(MarketplaceGenerationInputExport).Assembly.GetManifestResourceStream(
                "PointOfSale.Infrastructure.Marketplace.Generation.MarketplaceGenerationInputExport.sql")!;
            using var reader=new StreamReader(stream);return reader.ReadToEnd();
        }
    }

    internal static async Task InstallDisposableAsync(NpgsqlConnection db,CancellationToken ct=default)
    {
        if(db.State!=ConnectionState.Open || db.Host!="app17c-pg" ||
            !db.Database.StartsWith("app17h_journal_",StringComparison.Ordinal))
            throw new InvalidOperationException("Guarded disposable PostgreSQL database required.");
        await MarketplaceSourceJournal.VerifyCaptureInstalledAsync(db,ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var command=new NpgsqlCommand(SchemaSql+MarketplaceFrozenInputContract.ExportSchemaSql,db,tx);
        await command.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
    }

    internal static async Task<FrozenGenerationInput> FreezeAsync(NpgsqlConnection db,GenerationLease lease,
        Guid generation,Guid operation,JournalPosition expected,CancellationToken ct=default)
    {
        if(generation==Guid.Empty || operation==Guid.Empty || expected.Revision<0 || expected.Ordinal!=long.MaxValue)
            throw new ArgumentException("A generation, operation and complete revision boundary are required.");
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await MarketplaceSourceJournal.VerifyCaptureInstalledAsync(db,ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,true,ct);
        FrozenGenerationInput? existing;
        await using(var read=new NpgsqlCommand("SELECT "+Columns+" FROM marketplace_generation.exports WHERE operation_id=@operation",db,tx))
        {
            read.Parameters.AddWithValue("operation",operation);
            await using var rows=await read.ExecuteReaderAsync(ct);
            existing=await rows.ReadAsync(ct) ? Header(rows) : null;
        }
        // A retry always returns its original frozen snapshot, even if replay advanced.
        if(existing is not null)
        {
            if(existing.GenerationId!=generation || existing.JournalId!=lease.JournalId || existing.Revision!=expected.Revision)
                throw new InvalidOperationException("Frozen input operation identity is already bound elsewhere.");
            await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,false,ct);
            await tx.CommitAsync(ct);return existing;
        }

        string name,uuid,fingerprint,sourceSchema;
        await using(var build=new NpgsqlCommand("""
            SELECT b.physical_name,b.physical_uuid,b.fingerprint,b.source_schema_fingerprint FROM marketplace_generation.builds b
            WHERE b.generation_id=@generation AND b.journal_id=@journal
              AND b.state IN ('snapshot-ready','replaying') AND b.checkpoint_revision=@revision
              AND b.checkpoint_ordinal=9223372036854775807 AND b.physical_uuid IS NOT NULL
              AND EXISTS(SELECT 1 FROM marketplace_journal.gate g
                  WHERE g.singleton AND g.journal_id=b.journal_id AND g.revision>=b.checkpoint_revision)
            FOR UPDATE OF b
            """,db,tx))
        {
            build.Parameters.AddWithValue("generation",generation);build.Parameters.AddWithValue("journal",lease.JournalId);
            build.Parameters.AddWithValue("revision",expected.Revision);
            await using var rows=await build.ExecuteReaderAsync(ct);
            if(!await rows.ReadAsync(ct))throw new InvalidOperationException("Frozen input requires the exact complete current build checkpoint.");
            name=rows.GetString(0);uuid=rows.GetString(1);fingerprint=rows.GetString(2);
            if(rows.IsDBNull(3))throw new InvalidDataException("Unknown build source schema cannot be frozen; restart this legacy build.");
            sourceSchema=rows.GetString(3);
        }
        // Fail closed on unsupported sources/versions. Table-name admission is not a
        // semantic completeness proof; production capture/writer inventory remains open.
        await using(var check=new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM marketplace_generation.inputs WHERE generation_id=@generation
                AND (NOT(source_table=ANY(@tables)) OR revision>@revision OR source_id='00000000-0000-0000-0000-000000000000'::uuid))
              OR EXISTS(SELECT 1 FROM marketplace_generation.invalidations WHERE generation_id=@generation
                AND (NOT(source_table=ANY(@tables)) OR revision>@revision))
            """,db,tx))
        {
            check.Parameters.AddWithValue("generation",generation);check.Parameters.AddWithValue("tables",MarketplaceSourceJournal.SourceTables.ToArray());
            check.Parameters.AddWithValue("revision",expected.Revision);
            if(await check.ExecuteScalarAsync(ct) is not false)throw new InvalidDataException("Frozen input source/version admission rejected.");
        }
        var id=Guid.NewGuid();long inputs,invalidations;
        await using(var copy=new NpgsqlCommand("""
            INSERT INTO marketplace_generation.export_inputs(export_id,source_table,source_id,revision,ordinal,deleted,body)
            SELECT @export,source_table,source_id,revision,ordinal,deleted,body FROM marketplace_generation.inputs
            WHERE generation_id=@generation
            """,db,tx))
        {
            copy.Parameters.AddWithValue("export",id);copy.Parameters.AddWithValue("generation",generation);
            inputs=await copy.ExecuteNonQueryAsync(ct);
        }
        await using(var copy=new NpgsqlCommand("""
            INSERT INTO marketplace_generation.export_invalidations(export_id,revision,ordinal,source_table,old_row,new_row)
            SELECT @export,revision,ordinal,source_table,old_row,new_row FROM marketplace_generation.invalidations
            WHERE generation_id=@generation
            """,db,tx))
        {
            copy.Parameters.AddWithValue("export",id);copy.Parameters.AddWithValue("generation",generation);
            invalidations=await copy.ExecuteNonQueryAsync(ct);
        }
        await using(var seal=new NpgsqlCommand("""
            INSERT INTO marketplace_generation.exports(export_id,operation_id,generation_id,journal_id,revision,
                physical_name,physical_uuid,fingerprint,created_epoch,capture_tables,input_count,invalidation_count,input_format_version,source_schema_fingerprint)
            VALUES(@export,@operation,@generation,@journal,@revision,@name,@uuid,@fingerprint,@epoch,@tables,@inputs,@invalidations,@format,@schema)
            """,db,tx))
        {
            seal.Parameters.AddWithValue("export",id);seal.Parameters.AddWithValue("operation",operation);
            seal.Parameters.AddWithValue("generation",generation);seal.Parameters.AddWithValue("journal",lease.JournalId);
            seal.Parameters.AddWithValue("revision",expected.Revision);seal.Parameters.AddWithValue("name",name);
            seal.Parameters.AddWithValue("uuid",uuid);seal.Parameters.AddWithValue("fingerprint",fingerprint);
            seal.Parameters.AddWithValue("epoch",lease.Epoch);seal.Parameters.AddWithValue("tables",MarketplaceSourceJournal.SourceTables.ToArray());
            seal.Parameters.AddWithValue("inputs",inputs);seal.Parameters.AddWithValue("invalidations",invalidations);
            seal.Parameters.AddWithValue("format",MarketplaceFrozenInputContract.FormatVersion);seal.Parameters.AddWithValue("schema",sourceSchema);
            await seal.ExecuteNonQueryAsync(ct);
        }
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,false,ct);
        await tx.CommitAsync(ct);
        return new(id,operation,generation,lease.JournalId,expected.Revision,name,uuid,fingerprint,lease.Epoch,inputs,invalidations,MarketplaceFrozenInputContract.FormatVersion,sourceSchema);
    }

    internal static async Task<FrozenInputPage> ReadPageAsync(NpgsqlConnection db,Guid export,
        FrozenInputCursor? after,int limit,CancellationToken ct=default)
    {
        if(export==Guid.Empty || limit<1 || limit>1000 || after is not null &&
            (after.ExportId!=export || !MarketplaceSourceJournal.SourceTables.Contains(after.Table) || after.SourceId==Guid.Empty))
            throw new ArgumentException("An export-bound input cursor and bounded page are required.");
        var header=await ReadHeaderAsync(db,export,ct);
        await using var page=new NpgsqlCommand("""
            SELECT source_table,source_id,revision,ordinal,deleted,body::text
            FROM marketplace_generation.export_inputs
            WHERE export_id=@export AND (@first OR (source_table,source_id)>(@table COLLATE "C",@source))
            ORDER BY source_table,source_id LIMIT @limit
            """,db);
        page.Parameters.AddWithValue("export",export);page.Parameters.AddWithValue("first",after is null);
        page.Parameters.AddWithValue("table",after?.Table??"");page.Parameters.AddWithValue("source",after?.SourceId??Guid.Empty);
        page.Parameters.AddWithValue("limit",limit);
        var result=new List<FrozenInputRow>();
        await using(var rows=await page.ExecuteReaderAsync(ct))
        {
            while(await rows.ReadAsync(ct))result.Add(new(rows.GetString(0),rows.GetGuid(1),
                new(rows.GetInt64(2),rows.GetInt64(3)),rows.GetBoolean(4),rows.GetString(5)));
        }
        return new(header,result,result.Count==limit ? new(export,result[^1].Table,result[^1].SourceId) : null);
    }

    internal static async Task<FrozenGenerationInput> ReadHeaderAsync(NpgsqlConnection db,Guid export,CancellationToken ct=default)
    {
        if(export==Guid.Empty)throw new ArgumentException("Frozen export identity required.");
        await using(var read=new NpgsqlCommand("SELECT "+Columns+" FROM marketplace_generation.exports WHERE export_id=@export",db))
        {
            read.Parameters.AddWithValue("export",export);await using var rows=await read.ExecuteReaderAsync(ct);
            if(!await rows.ReadAsync(ct))throw new InvalidOperationException("Frozen input export not found.");
            return Header(rows);
        }
    }

    private const string Columns="export_id,operation_id,generation_id,journal_id,revision,physical_name,physical_uuid,fingerprint,created_epoch,input_count,invalidation_count,input_format_version,source_schema_fingerprint";
    private static FrozenGenerationInput Header(NpgsqlDataReader rows)=>new(rows.GetGuid(0),rows.GetGuid(1),rows.GetGuid(2),
        rows.GetGuid(3),rows.GetInt64(4),rows.GetString(5),rows.GetString(6),rows.GetString(7),rows.GetInt64(8),rows.GetInt64(9),rows.GetInt64(10),rows.GetInt32(11),rows.IsDBNull(12) ? null : rows.GetString(12));
}

internal sealed record FrozenGenerationInput(Guid ExportId,Guid OperationId,Guid GenerationId,Guid JournalId,long Revision,
    string PhysicalName,string PhysicalUuid,string Fingerprint,long CreatedEpoch,long InputCount,long InvalidationCount,int InputFormatVersion,string? SourceSchemaFingerprint);
internal sealed record FrozenInputCursor(Guid ExportId,string Table,Guid SourceId);
internal sealed record FrozenInputRow(string Table,Guid SourceId,JournalPosition Position,bool Deleted,string Body);
internal sealed record FrozenInputPage(FrozenGenerationInput Export,IReadOnlyList<FrozenInputRow> Rows,FrozenInputCursor? Next);
