using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Journal;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

// Candidate-only admission. No DI, source mutation, durable output or external I/O.
internal static class MarketplaceFrozenInputContract
{
    internal const int FormatVersion=1;
    internal const string BuildSchemaSql="""
        ALTER TABLE marketplace_generation.builds ADD COLUMN IF NOT EXISTS source_schema_fingerprint text
            CHECK(source_schema_fingerprint IS NULL OR source_schema_fingerprint ~ '^[0-9a-f]{64}$');
        """;
    internal const string ExportSchemaSql="""
        ALTER TABLE marketplace_generation.exports ADD COLUMN IF NOT EXISTS input_format_version integer NOT NULL DEFAULT 0 CHECK(input_format_version>=0);
        ALTER TABLE marketplace_generation.exports ADD COLUMN IF NOT EXISTS source_schema_fingerprint text
            CHECK(source_schema_fingerprint IS NULL OR source_schema_fingerprint ~ '^[0-9a-f]{64}$');
        """;

    private sealed record Column(string Table,string Name,string StoreType,bool Nullable);
    private static readonly Lazy<Column[]> SupportedColumns=new(() =>
    {
        // Model construction does not open a database connection. Match the complete
        // provider column inventory, not reflection properties/navigation defaults.
        using var context=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=unused").Options);
        return context.Model.GetRelationalModel().Tables
            .Where(t=>(t.Schema??"public")=="public" && MarketplaceSourceJournal.SourceTables.Contains(t.Name))
            .SelectMany(t=>t.Columns.Where(c=>c.Name!="xmin").Select(c=>new Column(t.Name,c.Name,Normalize(c.StoreType),c.IsNullable)))
            .OrderBy(c=>c.Table,StringComparer.Ordinal).ThenBy(c=>c.Name,StringComparer.Ordinal).ToArray();
    });
    internal static string SupportedFingerprint=>Fingerprint(SupportedColumns.Value);
    internal static string SupportedSchemaDescription=>JsonSerializer.Serialize(SupportedColumns.Value);

    internal static async Task<string> ReadSourceFingerprintAsync(NpgsqlConnection db,NpgsqlTransaction tx,CancellationToken ct)
    {
        // Hold table schema stable through snapshot/replay. ACCESS SHARE permits normal
        // journalled writes, but blocks incompatible DDL until this transaction ends.
        var tables=MarketplaceSourceJournal.SourceTables.Order(StringComparer.Ordinal);
        await using(var schemaLock=new NpgsqlCommand("LOCK TABLE "+string.Join(',',tables.Select(t=>"public.\""+t+"\""))+" IN ACCESS SHARE MODE",db,tx))
            await schemaLock.ExecuteNonQueryAsync(ct);
        await using var query=new NpgsqlCommand("""
            SELECT c.relname,a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),NOT a.attnotnull
            FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relname=ANY(@tables) AND a.attnum>0 AND NOT a.attisdropped
            """,db,tx);
        query.Parameters.AddWithValue("tables",MarketplaceSourceJournal.SourceTables.ToArray());
        var columns=new List<Column>();
        await using(var rows=await query.ExecuteReaderAsync(ct))
            while(await rows.ReadAsync(ct))columns.Add(new(rows.GetString(0),rows.GetString(1),Normalize(rows.GetString(2)),rows.GetBoolean(3)));
        if(columns.Select(c=>c.Table).Distinct().Count()!=MarketplaceSourceJournal.SourceTables.Count)
            throw new InvalidDataException("Source schema inventory incomplete.");
        return Fingerprint(columns.OrderBy(c=>c.Table,StringComparer.Ordinal).ThenBy(c=>c.Name,StringComparer.Ordinal));
    }

    internal static async Task RequireProjectionAsync(NpgsqlConnection db,FrozenGenerationInput header,CancellationToken ct)
    {
        if(header.InputFormatVersion!=FormatVersion || header.SourceSchemaFingerprint!=SupportedFingerprint)
            throw new InvalidDataException("Frozen input format/schema contract unsupported; restart unknown legacy builds, never relabel them.");
        await using(var inventory=new NpgsqlCommand("SELECT capture_tables FROM marketplace_generation.exports WHERE export_id=@export",db))
        {
            inventory.Parameters.AddWithValue("export",header.ExportId);
            var tables=(string[])(await inventory.ExecuteScalarAsync(ct))!;
            if(!tables.Order(StringComparer.Ordinal).SequenceEqual(MarketplaceSourceJournal.SourceTables.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Frozen capture inventory rejected.");
        }
        long count=0;
        await using(var input=new NpgsqlCommand("""
            SELECT source_table,source_id,revision,ordinal,body::text FROM marketplace_generation.export_inputs WHERE export_id=@export
            """,db))
        {
            input.Parameters.AddWithValue("export",header.ExportId);
            await using var rows=await input.ExecuteReaderAsync(CommandBehavior.SequentialAccess,ct);
            while(await rows.ReadAsync(ct))
            {
                var table=rows.GetString(0);var id=rows.GetGuid(1);var revision=rows.GetInt64(2);var ordinal=rows.GetInt64(3);
                if(id==Guid.Empty || revision<0 || revision>header.Revision || ordinal<=0)
                    throw new InvalidDataException("Frozen input position/identity rejected.");
                using var body=JsonDocument.Parse(rows.GetString(4));ValidateBody(table,body.RootElement);
                if(body.RootElement.GetProperty("Id").GetGuid()!=id)throw new InvalidDataException("Frozen input identity mismatch.");
                count++;
            }
        }
        if(count!=header.InputCount)throw new InvalidDataException("Frozen input manifest count mismatch.");
        count=0;
        await using(var invalidations=new NpgsqlCommand("""
            SELECT source_table,revision,ordinal,old_row::text,new_row::text
            FROM marketplace_generation.export_invalidations WHERE export_id=@export
            """,db))
        {
            invalidations.Parameters.AddWithValue("export",header.ExportId);
            await using var rows=await invalidations.ExecuteReaderAsync(CommandBehavior.SequentialAccess,ct);
            while(await rows.ReadAsync(ct))
            {
                var table=rows.GetString(0);var revision=rows.GetInt64(1);var ordinal=rows.GetInt64(2);
                if(revision<=0 || revision>header.Revision || ordinal<=0)throw new InvalidDataException("Frozen invalidation position rejected.");
                var images=0;
                for(var i=3;i<5;i++)if(!rows.IsDBNull(i))
                {using var body=JsonDocument.Parse(rows.GetString(i));ValidateBody(table,body.RootElement);images++;}
                if(images==0)throw new InvalidDataException("Frozen invalidation images missing.");
                count++;
            }
        }
        if(count!=header.InvalidationCount)throw new InvalidDataException("Frozen invalidation manifest count mismatch.");
        // Corrupt duplicate children could multiply aggregate joins. Reject them even
        // when they do not belong to this particular service page.
        await using var duplicates=new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM marketplace_generation.export_inputs
                WHERE export_id=@export AND source_table='ServiceReviews' AND NOT deleted AND NOT (body->>'IsDeleted')::boolean
                GROUP BY body->>'ServiceRequestId' HAVING count(*)>1)
              OR EXISTS(SELECT 1 FROM marketplace_generation.export_inputs
                WHERE export_id=@export AND source_table='MarketingReviewEvidences' AND NOT deleted AND NOT (body->>'IsDeleted')::boolean
                GROUP BY body->>'ServiceReviewId' HAVING count(*)>1)
              OR EXISTS(SELECT 1 FROM marketplace_generation.export_inputs
                WHERE export_id=@export AND source_table='UserGovernanceProfiles' AND NOT deleted
                GROUP BY body->>'SubjectId',body->>'Role' HAVING count(*)>1)
            """,db);
        duplicates.Parameters.AddWithValue("export",header.ExportId);
        if(await duplicates.ExecuteScalarAsync(ct) is not false)throw new InvalidDataException("Frozen duplicate authority children rejected.");
    }

    private static void ValidateBody(string table,JsonElement row)
    {
        var columns=SupportedColumns.Value.Where(c=>c.Table==table).ToArray();
        if(columns.Length==0 || row.ValueKind!=JsonValueKind.Object || row.EnumerateObject().Count()!=columns.Length)
            throw new InvalidDataException("Frozen row shape rejected: "+table);
        foreach(var column in columns)
        {
            if(!row.TryGetProperty(column.Name,out var value) || !ValidValue(column,value))
                throw new InvalidDataException("Frozen row field rejected: "+table+"."+column.Name);
        }
        if(row.GetProperty("Id").GetGuid()==Guid.Empty)throw new InvalidDataException("Frozen row identity empty.");
        foreach(var name in table switch
        {
            "Services"=>new[]{"TenantId","ProfileId"},
            "MarketplaceProfiles"=>new[]{"TenantId","UserId"},
            "ServiceRequests"=>new[]{"TenantId","ServiceId","ProviderProfileId","BuyerUserId","ProviderUserId"},
            "ServiceReviews"=>new[]{"TenantId","ServiceRequestId","ServiceId","ReviewerUserId","ProviderUserId"},
            "MarketingReviewEvidences"=>new[]{"TenantId","ServiceReviewId"},
            "ProviderTrustAggregates"=>new[]{"TenantId","ProviderProfileId"},
            "UserGovernanceProfiles"=>new[]{"SubjectId"},_=>Array.Empty<string>()
        })if(row.GetProperty(name).GetGuid()==Guid.Empty)throw new InvalidDataException("Frozen row relationship empty: "+table+"."+name);
        switch(table)
        {
            case "Services":
                if(row.GetProperty("PricingType").ValueKind!=JsonValueKind.Null)RequireEnum<PricingType>(row,"PricingType",false);
                break;
            case "Tenants":RequireEnum<TenantStatus>(row,"Status",false);break;
            case "UserGovernanceProfiles":
                RequireEnum<UserGovernanceRole>(row,"Role",false);RequireEnum<UserGovernanceStatus>(row,"Status",false);break;
            case "ServiceRequests":RequireEnum<ServiceRequestStatus>(row,"Status",true);break;
            case "ServiceReviews":
                if(row.GetProperty("Rating").GetInt32() is <1 or >5)throw new InvalidDataException("Frozen review rating rejected.");
                break;
            case "MarketingReviewEvidences":
                RequireEnum<EvidenceType>(row,"EvidenceType",true);
                var verified=row.GetProperty("EvidenceType").GetString()==EvidenceType.PosVerified.ToString();
                var orders=row.GetProperty("OrdersCount");var revenue=row.GetProperty("RevenueAmount");
                if(verified ? orders.ValueKind==JsonValueKind.Null || revenue.ValueKind==JsonValueKind.Null || orders.GetInt32()<0 || revenue.GetDecimal()<0
                    : orders.ValueKind!=JsonValueKind.Null || revenue.ValueKind!=JsonValueKind.Null)
                    throw new InvalidDataException("Frozen evidence classification/metrics rejected.");
                break;
        }
    }

    private static bool ValidValue(Column column,JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Null)return column.Nullable;
        var type=column.StoreType;
        if(type=="uuid")return value.ValueKind==JsonValueKind.String && value.TryGetGuid(out _);
        if(type=="boolean")return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if(type=="integer")return value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out _);
        if(type=="bigint")return value.ValueKind==JsonValueKind.Number && value.TryGetInt64(out _);
        if(type=="smallint")return value.ValueKind==JsonValueKind.Number && value.TryGetInt16(out _);
        if(type.StartsWith("numeric",StringComparison.Ordinal))return value.ValueKind==JsonValueKind.Number && value.TryGetDecimal(out _);
        if(type=="timestamp with time zone")return value.ValueKind==JsonValueKind.String && value.TryGetDateTimeOffset(out _);
        if(type=="date")return value.ValueKind==JsonValueKind.String && DateOnly.TryParseExact(value.GetString(),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _);
        if(type=="text" || type.StartsWith("character varying",StringComparison.Ordinal))return value.ValueKind==JsonValueKind.String;
        // Unreviewed provider types are not defaulted/coerced into a known shape.
        return false;
    }
    private static void RequireEnum<T>(JsonElement row,string name,bool text) where T:struct,Enum
    {
        var field=row.GetProperty(name);
        if(text ? field.ValueKind!=JsonValueKind.String || !Enum.GetNames<T>().Contains(field.GetString(),StringComparer.Ordinal)
            : field.ValueKind!=JsonValueKind.Number || !field.TryGetInt32(out var n) || !Enum.IsDefined(typeof(T),n))
            throw new InvalidDataException("Frozen row enum rejected: "+name);
    }
    private static string Normalize(string type)=>type.Replace("decimal(","numeric(",StringComparison.Ordinal).Replace(", ",",",StringComparison.Ordinal);
    private static string Fingerprint(IEnumerable<Column> columns)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(columns))));
}
