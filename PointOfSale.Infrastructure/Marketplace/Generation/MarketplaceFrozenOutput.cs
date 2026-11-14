using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using PointOfSale.Core.Entities;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// LOCAL durable composition only. Not registered in DI. No search client, output
/// delivery acknowledgement, generation readiness, pointer or lifecycle operation.
/// Database owner/role bypass and production-scale budgets remain uncertified.
/// </summary>
internal static class MarketplaceFrozenOutput
{
    private static readonly JsonSerializerOptions Codec=new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        Converters={new ProviderDecimalConverter()}
    };
    // Seal the double representation declared by the derivative mapping before
    // Java/Painless can round it. SQL amounts and Int64 revision/epoch stay exact.
    internal static string SerializeDocument(PointOfSale.Infrastructure.Elasticsearch.Documents.MarketplaceServiceDocument document) =>
        JsonSerializer.Serialize(document,Codec);

    private sealed class ProviderDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteNumberValue((double)value);
    }
    // Conservative code pin: unrelated infrastructure/core changes may force a new
    // run. Never silently resume pages under a different composer/codec/runtime.
    internal static string ComposerFingerprint {get;}=Hash("local-output-v2-provider-double|"+
        typeof(MarketplaceFrozenOutput).Module.ModuleVersionId+"|"+typeof(ProviderTrustAggregate).Module.ModuleVersionId+"|"+Environment.Version);
    internal static string SchemaSql
    {
        get
        {
            using var stream=typeof(MarketplaceFrozenOutput).Assembly.GetManifestResourceStream(
                "PointOfSale.Infrastructure.Marketplace.Generation.MarketplaceFrozenOutput.sql")!;
            using var reader=new StreamReader(stream);return reader.ReadToEnd();
        }
    }

    internal static async Task<FrozenOutputRun> CreateAsync(NpgsqlConnection db,GenerationLease lease,Guid export,Guid operation,CancellationToken ct=default)
    {
        Identity(export,operation);
        await using var tx=await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,true,ct);
        var header=await MarketplaceGenerationInputExport.ReadHeaderAsync(db,export,ct);
        await RequireLineageAsync(db,header,lease,ct);
        await MarketplaceFrozenInputContract.RequireProjectionAsync(db,header,ct);
        var existing=await RunAsync(db,"operation_id",operation,false,ct);
        if(existing is not null && (existing.ExportId!=export || existing.ComposerFingerprint!=ComposerFingerprint))
            throw new InvalidOperationException("Local output operation is bound to another export/composer.");
        if(existing is null)
        {
            var id=Guid.NewGuid();
            await using var insert=new NpgsqlCommand("""
                INSERT INTO marketplace_generation.output_runs(run_id,operation_id,export_id,composer_fingerprint,created_epoch,last_epoch)
                VALUES(@id,@operation,@export,@composer,@epoch,@epoch)
                """,db,tx);
            insert.Parameters.AddWithValue("id",id);insert.Parameters.AddWithValue("operation",operation);
            insert.Parameters.AddWithValue("export",export);insert.Parameters.AddWithValue("composer",ComposerFingerprint);insert.Parameters.AddWithValue("epoch",lease.Epoch);
            await insert.ExecuteNonQueryAsync(ct);existing=(await RunAsync(db,"run_id",id,false,ct))!;
        }
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,false,ct);
        await tx.CommitAsync(ct);return existing;
    }

    // Observed local progress, not authority for any external write/readiness decision.
    internal static async Task<FrozenOutputRun> ReadAsync(NpgsqlConnection db,Guid run,CancellationToken ct=default)=>
        await RunAsync(db,"run_id",run,false,ct) ?? throw new InvalidOperationException("Local output run not found.");

    internal static async Task<FrozenOutputPage> CommitNextPageAsync(NpgsqlConnection db,GenerationLease lease,Guid run,Guid operation,
        FrozenOutputCursor expected,int limit,CancellationToken ct=default)
    {
        Identity(run,operation);
        if(limit is <1 or >128 || expected.RunId!=run || expected.NextPage<0 || expected.AfterService==Guid.Empty)
            throw new ArgumentException("An exact run/export-bound cursor and bounded page are required.");
        await using var tx=await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,true,ct);
        var current=await ReadAsync(db,run,ct);var header=await RequireRunAsync(db,current,lease,ct);
        var retry=await PageAsync(db,operation,null,null,ct);
        if(retry is not null)
        {
            if(retry.RunId!=run || retry.ExportId!=expected.ExportId || retry.PageNumber!=expected.NextPage ||
                retry.ExpectedAfter!=expected.AfterService || retry.Limit!=limit)
                throw new InvalidOperationException("Local page operation identity is already bound elsewhere.");
            await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,false,ct);
            await tx.CommitAsync(ct);return retry;
        }
        if(current.Cursor!=expected || current.Exhausted || current.Sealed)
            throw new InvalidOperationException("Local output cursor compare-and-swap rejected.");
        var page=await MarketplaceFrozenServiceProjection.ReadPageAsync(db,header.ExportId,
            expected.AfterService is Guid after ? new(header.ExportId,after) : null,limit,ct);
        if(page.Export!=header)throw new InvalidDataException("Frozen projection changed its manifest.");
        var leafDigests=new List<(Guid Id,string Digest)>();
        foreach(var service in page.Services)
        {
            var payload=service.Document is null ? null : SerializeDocument(service.Document);
            var digest=RowDigest(service.ServiceId,payload);leafDigests.Add((service.ServiceId,digest));
            await using var row=new NpgsqlCommand("""
                INSERT INTO marketplace_generation.output_rows(run_id,page_number,service_id,payload,digest)
                VALUES(@run,@page,@service,@payload,@digest)
                """,db,tx);
            row.Parameters.AddWithValue("run",run);row.Parameters.AddWithValue("page",expected.NextPage);
            row.Parameters.AddWithValue("service",service.ServiceId);row.Parameters.AddWithValue("payload",NpgsqlDbType.Text,(object?)payload??DBNull.Value);
            row.Parameters.AddWithValue("digest",digest);await row.ExecuteNonQueryAsync(ct);
        }
        var last=page.Services.Count>0 ? page.Services[^1].ServiceId : expected.AfterService;
        var receipt=new FrozenOutputPage(run,header.ExportId,operation,expected.NextPage,expected.AfterService,last,limit,
            page.Services.Count,page.Next is null,PageDigest(leafDigests),lease.Epoch);
        await using(var record=new NpgsqlCommand("""
            INSERT INTO marketplace_generation.output_pages(run_id,page_number,operation_id,expected_after,after_service,page_limit,row_count,exhausted,digest,created_epoch)
            VALUES(@run,@page,@operation,@expected,@after,@limit,@count,@exhausted,@digest,@epoch)
            """,db,tx))
        {
            PageParameters(record,receipt);await record.ExecuteNonQueryAsync(ct);
        }
        await using(var advance=new NpgsqlCommand("""
            UPDATE marketplace_generation.output_runs SET next_page=next_page+1,after_service=@after,
                item_count=item_count+@count,exhausted=@exhausted,last_epoch=@epoch
            WHERE run_id=@run AND next_page=@page AND after_service IS NOT DISTINCT FROM @expected AND NOT exhausted
            """,db,tx))
        {
            PageParameters(advance,receipt);
            if(await advance.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Local output cursor compare-and-swap rejected.");
        }
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,false,ct);
        await tx.CommitAsync(ct);return receipt;
    }

    internal static async Task<FrozenOutputSeal> SealAsync(NpgsqlConnection db,GenerationLease lease,Guid run,Guid operation,CancellationToken ct=default)
    {
        Identity(run,operation);await using var tx=await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,true,ct);
        var current=await ReadAsync(db,run,ct);var header=await RequireRunAsync(db,current,lease,ct);
        var digest=await VerifyCompleteAsync(db,current,header,ct);
        var seal=await SealRecordAsync(db,run,ct);
        if(seal is not null && (seal.OperationId!=operation || seal.Digest!=digest || seal.PageCount!=current.Cursor.NextPage || seal.ItemCount!=current.ItemCount))
            throw new InvalidOperationException("Local output seal identity/content rejected.");
        if(seal is null)
        {
            seal=new(run,operation,current.Cursor.NextPage,current.ItemCount,digest,lease.Epoch);
            await using var insert=new NpgsqlCommand("""
                INSERT INTO marketplace_generation.output_seals(run_id,operation_id,page_count,item_count,digest,created_epoch)
                VALUES(@run,@operation,@pages,@items,@digest,@epoch)
                """,db,tx);
            insert.Parameters.AddWithValue("run",run);insert.Parameters.AddWithValue("operation",operation);
            insert.Parameters.AddWithValue("pages",seal.PageCount);insert.Parameters.AddWithValue("items",seal.ItemCount);
            insert.Parameters.AddWithValue("digest",seal.Digest);insert.Parameters.AddWithValue("epoch",lease.Epoch);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,false,ct);await tx.CommitAsync(ct);return seal;
    }

    internal static async Task<FrozenOutputLocalAck> AcknowledgeLocalAsync(NpgsqlConnection db,GenerationLease lease,Guid run,Guid operation,CancellationToken ct=default)
    {
        Identity(run,operation);await using var tx=await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,true,ct);
        var current=await ReadAsync(db,run,ct);var header=await RequireRunAsync(db,current,lease,ct);
        var seal=await SealRecordAsync(db,run,ct) ?? throw new InvalidOperationException("A sealed complete local output is required.");
        var digest=await VerifyCompleteAsync(db,current,header,ct);
        if(seal.Digest!=digest || seal.PageCount!=current.Cursor.NextPage || seal.ItemCount!=current.ItemCount)
            throw new InvalidDataException("Local seal content verification rejected.");
        FrozenOutputLocalAck? ack=null;
        await using(var read=new NpgsqlCommand("SELECT operation_id,seal_digest,created_epoch FROM marketplace_generation.output_local_acks WHERE run_id=@run",db,tx))
        {
            read.Parameters.AddWithValue("run",run);await using var rows=await read.ExecuteReaderAsync(ct);
            if(await rows.ReadAsync(ct))ack=new(run,rows.GetGuid(0),rows.GetString(1),rows.GetInt64(2));
        }
        if(ack is not null && (ack.OperationId!=operation || ack.SealDigest!=digest))throw new InvalidOperationException("Local acknowledgement operation identity rejected.");
        if(ack is null)
        {
            await using var insert=new NpgsqlCommand("""
                INSERT INTO marketplace_generation.output_local_acks(run_id,operation_id,seal_digest,created_epoch)
                VALUES(@run,@operation,@digest,@epoch)
                """,db,tx);
            insert.Parameters.AddWithValue("run",run);insert.Parameters.AddWithValue("operation",operation);
            insert.Parameters.AddWithValue("digest",digest);insert.Parameters.AddWithValue("epoch",lease.Epoch);
            await insert.ExecuteNonQueryAsync(ct);ack=new(run,operation,digest,lease.Epoch);
        }
        // No raw invalidation/journal deletion, source/output checkpoint, build-state
        // transition or external acknowledgement occurs in this LOCAL transaction.
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db,tx,lease,false,ct);await tx.CommitAsync(ct);return ack;
    }

    private static async Task<string> VerifyCompleteAsync(NpgsqlConnection db,FrozenOutputRun run,FrozenGenerationInput header,CancellationToken ct)
    {
        if(!run.Exhausted || run.Cursor.NextPage<=0)throw new InvalidOperationException("Local output full pass is not proven exhausted.");
        await MarketplaceFrozenInputContract.RequireProjectionAsync(db,header,ct);
        await using(var counts=new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM marketplace_generation.export_inputs WHERE export_id=@export AND source_table='Services'),
                count(*),count(*) FILTER(WHERE i.source_id IS NULL)
            FROM marketplace_generation.output_rows o LEFT JOIN marketplace_generation.export_inputs i
                ON i.export_id=@export AND i.source_table='Services' AND i.source_id=o.service_id WHERE o.run_id=@run
            """,db))
        {
            counts.Parameters.AddWithValue("export",run.ExportId);counts.Parameters.AddWithValue("run",run.RunId);
            await using var rows=await counts.ExecuteReaderAsync(ct);await rows.ReadAsync(ct);
            if(rows.GetInt64(0)!=run.ItemCount || rows.GetInt64(1)!=run.ItemCount || rows.GetInt64(2)!=0)
                throw new InvalidDataException("Local output identity/count coverage incomplete.");
        }
        using var root=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(root,"local-output-v1");Append(root,run.RunId.ToString("D"));Append(root,run.ExportId.ToString("D"));Append(root,run.ComposerFingerprint);
        Append(root,run.Cursor.NextPage.ToString(CultureInfo.InvariantCulture));Append(root,run.ItemCount.ToString(CultureInfo.InvariantCulture));
        Guid? after=null;long total=0;
        for(long number=0;number<run.Cursor.NextPage;number++)
        {
            var page=await PageAsync(db,null,run.RunId,number,ct) ?? throw new InvalidDataException("Local output page receipt missing.");
            if(page.ExpectedAfter!=after || page.Exhausted!=(number==run.Cursor.NextPage-1) || page.Exhausted!=(page.RowCount<page.Limit))
                throw new InvalidDataException("Local output page chain/exhaustion rejected.");
            var digests=new List<(Guid Id,string Digest)>();
            await using(var read=new NpgsqlCommand("""
                SELECT service_id,payload,digest,(@first OR service_id>@after) FROM marketplace_generation.output_rows
                WHERE run_id=@run AND page_number=@page ORDER BY service_id
                """,db))
            {
                read.Parameters.AddWithValue("first",after is null);read.Parameters.AddWithValue("after",after??Guid.Empty);
                read.Parameters.AddWithValue("run",run.RunId);read.Parameters.AddWithValue("page",number);
                await using var rows=await read.ExecuteReaderAsync(ct);
                while(await rows.ReadAsync(ct))
                {
                    var id=rows.GetGuid(0);var payload=rows.IsDBNull(1) ? null : rows.GetString(1);var digest=rows.GetString(2);
                    if(!rows.GetBoolean(3) || RowDigest(id,payload)!=digest)throw new InvalidDataException("Local output row identity/order/digest rejected.");
                    if(payload is not null)
                    {
                        using var body=JsonDocument.Parse(payload);
                        if(body.RootElement.GetProperty("serviceId").GetGuid()!=id)throw new InvalidDataException("Local document identity mismatch.");
                    }
                    digests.Add((id,digest));
                }
            }
            var last=digests.Count>0 ? digests[^1].Id : after;
            if(digests.Count!=page.RowCount || last!=page.AfterService || PageDigest(digests)!=page.Digest)
                throw new InvalidDataException("Local output page content/count rejected.");
            Append(root,number.ToString(CultureInfo.InvariantCulture));Append(root,page.Digest);
            after=last;total+=digests.Count;
        }
        if(total!=run.ItemCount || after!=run.Cursor.AfterService)throw new InvalidDataException("Local output run cursor/count mismatch.");
        await using(var count=new NpgsqlCommand("SELECT count(*) FROM marketplace_generation.output_pages WHERE run_id=@run",db))
        {
            count.Parameters.AddWithValue("run",run.RunId);
            if((long)(await count.ExecuteScalarAsync(ct))! !=run.Cursor.NextPage)throw new InvalidDataException("Unexpected local output page receipt.");
        }
        return Convert.ToHexStringLower(root.GetHashAndReset());
    }

    private static async Task<FrozenGenerationInput> RequireRunAsync(NpgsqlConnection db,FrozenOutputRun run,GenerationLease lease,CancellationToken ct)
    {
        if(run.ComposerFingerprint!=ComposerFingerprint)throw new InvalidDataException("Local output composer/code/runtime changed; start a new run.");
        var header=await MarketplaceGenerationInputExport.ReadHeaderAsync(db,run.ExportId,ct);
        await RequireLineageAsync(db,header,lease,ct);return header;
    }
    private static async Task RequireLineageAsync(NpgsqlConnection db,FrozenGenerationInput header,GenerationLease lease,CancellationToken ct)
    {
        if(header.JournalId!=lease.JournalId || header.InputFormatVersion!=MarketplaceFrozenInputContract.FormatVersion ||
            header.SourceSchemaFingerprint!=MarketplaceFrozenInputContract.SupportedFingerprint)throw new InvalidDataException("Local output frozen lineage/contract rejected.");
        await using var query=new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM marketplace_generation.builds WHERE generation_id=@generation AND journal_id=@journal
                AND physical_name=@name AND physical_uuid=@uuid AND fingerprint=@fingerprint AND source_schema_fingerprint=@schema
                AND state IN ('snapshot-ready','replaying'))
            """,db);
        query.Parameters.AddWithValue("generation",header.GenerationId);query.Parameters.AddWithValue("journal",header.JournalId);
        query.Parameters.AddWithValue("name",header.PhysicalName);query.Parameters.AddWithValue("uuid",header.PhysicalUuid);
        query.Parameters.AddWithValue("fingerprint",header.Fingerprint);query.Parameters.AddWithValue("schema",header.SourceSchemaFingerprint!);
        if(await query.ExecuteScalarAsync(ct) is not true)throw new InvalidDataException("Local output physical/source lineage changed.");
    }
    private static async Task<FrozenOutputRun?> RunAsync(NpgsqlConnection db,string key,Guid value,bool lockRow,CancellationToken ct)
    {
        // key is private, selected only by the fixed call sites above.
        if(key is not ("run_id" or "operation_id"))throw new ArgumentException(nameof(key));
        await using var query=new NpgsqlCommand("""
            SELECT r.run_id,r.operation_id,r.export_id,r.composer_fingerprint,r.next_page,r.after_service,r.exhausted,r.item_count,r.created_epoch,
                EXISTS(SELECT 1 FROM marketplace_generation.output_seals s WHERE s.run_id=r.run_id)
            FROM marketplace_generation.output_runs r WHERE r.
            """+key+"=@value"+(lockRow ? " FOR UPDATE OF r" : ""),db);
        query.Parameters.AddWithValue("value",value);await using var rows=await query.ExecuteReaderAsync(ct);
        if(!await rows.ReadAsync(ct))return null;
        var run=rows.GetGuid(0);var export=rows.GetGuid(2);
        return new(run,rows.GetGuid(1),export,rows.GetString(3),new(run,export,rows.GetInt64(4),rows.IsDBNull(5) ? null : rows.GetGuid(5)),
            rows.GetBoolean(6),rows.GetInt64(7),rows.GetInt64(8),rows.GetBoolean(9));
    }
    private static async Task<FrozenOutputPage?> PageAsync(NpgsqlConnection db,Guid? operation,Guid? run,long? number,CancellationToken ct)
    {
        await using var query=new NpgsqlCommand("""
            SELECT p.run_id,r.export_id,p.operation_id,p.page_number,p.expected_after,p.after_service,p.page_limit,p.row_count,p.exhausted,p.digest,p.created_epoch
            FROM marketplace_generation.output_pages p JOIN marketplace_generation.output_runs r ON r.run_id=p.run_id WHERE
            """+" "+(operation is not null ? "p.operation_id=@operation" : "p.run_id=@run AND p.page_number=@page"),db);
        if(operation is Guid op)query.Parameters.AddWithValue("operation",op);
        else {query.Parameters.AddWithValue("run",run!.Value);query.Parameters.AddWithValue("page",number!.Value);}
        await using var rows=await query.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? new(rows.GetGuid(0),rows.GetGuid(1),rows.GetGuid(2),rows.GetInt64(3),
            rows.IsDBNull(4) ? null : rows.GetGuid(4),rows.IsDBNull(5) ? null : rows.GetGuid(5),rows.GetInt32(6),rows.GetInt32(7),rows.GetBoolean(8),rows.GetString(9),rows.GetInt64(10)) : null;
    }
    private static async Task<FrozenOutputSeal?> SealRecordAsync(NpgsqlConnection db,Guid run,CancellationToken ct)
    {
        await using var query=new NpgsqlCommand("SELECT operation_id,page_count,item_count,digest,created_epoch FROM marketplace_generation.output_seals WHERE run_id=@run",db);
        query.Parameters.AddWithValue("run",run);await using var rows=await query.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? new(run,rows.GetGuid(0),rows.GetInt64(1),rows.GetInt64(2),rows.GetString(3),rows.GetInt64(4)) : null;
    }
    private static void PageParameters(NpgsqlCommand command,FrozenOutputPage page)
    {
        command.Parameters.AddWithValue("run",page.RunId);command.Parameters.AddWithValue("page",page.PageNumber);
        command.Parameters.AddWithValue("operation",page.OperationId);
        command.Parameters.AddWithValue("expected",NpgsqlDbType.Uuid,(object?)page.ExpectedAfter??DBNull.Value);
        command.Parameters.AddWithValue("after",NpgsqlDbType.Uuid,(object?)page.AfterService??DBNull.Value);
        command.Parameters.AddWithValue("limit",page.Limit);command.Parameters.AddWithValue("count",page.RowCount);
        command.Parameters.AddWithValue("exhausted",page.Exhausted);command.Parameters.AddWithValue("digest",page.Digest);command.Parameters.AddWithValue("epoch",page.CreatedEpoch);
    }
    private static void Identity(Guid first,Guid operation)
    {if(first==Guid.Empty || operation==Guid.Empty)throw new ArgumentException("Explicit local operation and target identities required.");}
    internal static string RowDigest(Guid id,string? payload)=>Hash("row-v1\n"+id.ToString("D")+"\n"+(payload is null ? "T" : "D"+payload));
    private static string PageDigest(IEnumerable<(Guid Id,string Digest)> rows)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);Append(hash,"page-v1");
        foreach(var (id,digest) in rows){Append(hash,id.ToString("D"));Append(hash,digest);}
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static void Append(IncrementalHash hash,string text)=>hash.AppendData(Encoding.UTF8.GetBytes(text+"\n"));
    private static string Hash(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

internal sealed record FrozenOutputCursor(Guid RunId,Guid ExportId,long NextPage,Guid? AfterService);
internal sealed record FrozenOutputRun(Guid RunId,Guid OperationId,Guid ExportId,string ComposerFingerprint,FrozenOutputCursor Cursor,bool Exhausted,long ItemCount,long CreatedEpoch,bool Sealed);
internal sealed record FrozenOutputPage(Guid RunId,Guid ExportId,Guid OperationId,long PageNumber,Guid? ExpectedAfter,Guid? AfterService,int Limit,int RowCount,bool Exhausted,string Digest,long CreatedEpoch)
{
    internal FrozenOutputCursor CursorAfter=>new(RunId,ExportId,PageNumber+1,AfterService);
}
internal sealed record FrozenOutputSeal(Guid RunId,Guid OperationId,long PageCount,long ItemCount,string Digest,long CreatedEpoch);
internal sealed record FrozenOutputLocalAck(Guid RunId,Guid OperationId,string SealDigest,long CreatedEpoch);
