using System.Text.Json;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Elasticsearch.Documents;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Candidate read-only exact-export composition. Each export requires a FULL service
/// pass, including permanent input tombstones; no targeted event fan-out can skip a
/// provider/tenant/governance/request/review/evidence dependency. Not registered in DI.
/// This emits proposed documents, NOT durable output or search acknowledgements.
/// </summary>
internal static class MarketplaceFrozenServiceProjection
{
    internal static async Task<FrozenServicePage> ReadPageAsync(NpgsqlConnection db,Guid export,
        FrozenServiceCursor? after,int limit,CancellationToken ct=default)
    {
        if(export==Guid.Empty || limit is <1 or >128 || after is not null &&
            (after.ExportId!=export || after.ServiceId==Guid.Empty))throw new ArgumentException("Bounded export-bound service page required.");
        var header=await MarketplaceGenerationInputExport.ReadHeaderAsync(db,export,ct);
        await MarketplaceFrozenInputContract.RequireProjectionAsync(db,header,ct);
        var ids=new List<Guid>();
        await using(var page=new NpgsqlCommand("""
            SELECT source_id FROM marketplace_generation.export_inputs
            WHERE export_id=@export AND source_table='Services' AND (@first OR source_id>@after)
            ORDER BY source_id LIMIT @limit
            """,db))
        {
            page.Parameters.AddWithValue("export",export);page.Parameters.AddWithValue("first",after is null);
            page.Parameters.AddWithValue("after",after?.ServiceId??Guid.Empty);page.Parameters.AddWithValue("limit",limit);
            await using var rows=await page.ExecuteReaderAsync(ct);
            while(await rows.ReadAsync(ct))ids.Add(rows.GetGuid(0));
        }
        var result=new List<FrozenServiceProjection>();
        foreach(var id in ids)result.Add(new(id,await ComposeAsync(db,export,id,ct)));
        return new(header,result,ids.Count==limit ? new(export,ids[^1]) : null);
    }

    private static async Task<MarketplaceServiceDocument?> ComposeAsync(NpgsqlConnection db,Guid export,Guid id,CancellationToken ct)
    {
        var source=await InputAsync(db,export,"Services",id,ct)
            ?? throw new InvalidDataException("Frozen service enumeration lost its input.");
        var s=source.Body;
        if(source.Deleted || Required(s,"IsDeleted").GetBoolean() || !Required(s,"IsActive").GetBoolean())return null;
        var tenantId=Identity(s,"TenantId");var profileId=Identity(s,"ProfileId");
        var tenant=await InputAsync(db,export,"Tenants",tenantId,ct);
        var profile=await InputAsync(db,export,"MarketplaceProfiles",profileId,ct);
        if(tenant is null || tenant.Deleted || profile is null || profile.Deleted)return null;
        if(EnumValue<TenantStatus>(tenant.Body,"Status")!=TenantStatus.Active || !Required(tenant.Body,"IsActive").GetBoolean() ||
            Required(tenant.Body,"DeletedAtUtc").ValueKind!=JsonValueKind.Null ||
            Identity(profile.Body,"TenantId")!=tenantId || Required(profile.Body,"IsDeleted").GetBoolean() ||
            !Required(profile.Body,"IsActive").GetBoolean())return null;
        // Frozen governance only. Absence has the same non-blocking meaning as the
        // existing public service authority; an unknown status on a matching row rejects.
        await using(var governance=new NpgsqlCommand("""
            SELECT body::text FROM marketplace_generation.export_inputs
            WHERE export_id=@export AND source_table='UserGovernanceProfiles' AND NOT deleted
                AND (body->>'SubjectId')::uuid=@tenant AND (body->>'Role')::integer=@vendor
            """,db))
        {
            governance.Parameters.AddWithValue("export",export);governance.Parameters.AddWithValue("tenant",tenantId);
            governance.Parameters.AddWithValue("vendor",(int)UserGovernanceRole.Vendor);
            await using var rows=await governance.ExecuteReaderAsync(ct);
            while(await rows.ReadAsync(ct))
            {
                using var row=JsonDocument.Parse(rows.GetString(0));
                if(EnumValue<UserGovernanceStatus>(row.RootElement,"Status") is UserGovernanceStatus.Suspended or UserGovernanceStatus.Banned)return null;
            }
        }
        var pricing=Required(s,"PricingType");
        var document=new MarketplaceServiceDocument
        {
            ServiceId=id,TenantId=tenantId,ProviderProfileId=profileId,
            Title=Text(s,"Title"),Description=Text(s,"Description"),
            CategoryId=OptionalText(s,"Category"),CategoryPath=OptionalText(s,"Category"),
            ProviderDisplayName=Text(profile.Body,"DisplayName"),CreatedAt=Required(s,"CreatedAt").GetDateTime(),IsActive=true,
            StartingPrice=Required(s,"StartingPrice").ValueKind==JsonValueKind.Null ? null : Required(s,"StartingPrice").GetDecimal(),
            PricingType=pricing.ValueKind==JsonValueKind.Null ? null : EnumValue<PricingType>(s,"PricingType").ToString(),
            Currency=OptionalText(s,"Currency"),ServiceImages=OptionalText(s,"ServiceImages")
        };
        // Explicit versioned record shapes, never public composite types or live
        // source rows. Admission validates every frozen field before any SQL cast.
        await using(var stats=new NpgsqlCommand("""
            WITH requests AS (
                SELECT r.* FROM marketplace_generation.export_inputs i
                CROSS JOIN LATERAL jsonb_to_record(i.body) AS r("Id" uuid,"TenantId" uuid,"ServiceId" uuid,
                    "ProviderProfileId" uuid,"BuyerUserId" uuid,"ProviderUserId" uuid,"Status" text,"IsDeleted" boolean)
                WHERE i.export_id=@export AND i.source_table='ServiceRequests' AND NOT i.deleted)
            SELECT count(*)::integer,count(*) FILTER(WHERE "Status"=@completed)::integer
            FROM requests WHERE "ServiceId"=@service AND "TenantId"=@tenant
                AND "ProviderProfileId"=@profile AND NOT "IsDeleted"
            """,db))
        {
            Parameters(stats,export,tenantId,profileId);stats.Parameters.AddWithValue("service",id);
            await using var rows=await stats.ExecuteReaderAsync(ct);await rows.ReadAsync(ct);
            var total=rows.GetInt32(0);var completed=rows.GetInt32(1);
            document.ServiceStats=new(){TotalRequests=total,CompletedRequests=completed,CompletionRate=total==0 ? 0m : (decimal)completed/total};
        }
        await using(var trust=new NpgsqlCommand("""
            WITH requests AS (
                SELECT r.* FROM marketplace_generation.export_inputs i
                CROSS JOIN LATERAL jsonb_to_record(i.body) AS r("Id" uuid,"TenantId" uuid,"ServiceId" uuid,
                    "ProviderProfileId" uuid,"BuyerUserId" uuid,"ProviderUserId" uuid,"Status" text,"IsDeleted" boolean)
                WHERE i.export_id=@export AND i.source_table='ServiceRequests' AND NOT i.deleted),
            reviews AS (
                SELECT r.* FROM marketplace_generation.export_inputs i
                CROSS JOIN LATERAL jsonb_to_record(i.body) AS r("Id" uuid,"TenantId" uuid,"ServiceRequestId" uuid,
                    "ServiceId" uuid,"ReviewerUserId" uuid,"ProviderUserId" uuid,"Rating" integer,"CreatedAt" timestamptz,"IsDeleted" boolean)
                WHERE i.export_id=@export AND i.source_table='ServiceReviews' AND NOT i.deleted),
            evidence AS (
                SELECT r.* FROM marketplace_generation.export_inputs i
                CROSS JOIN LATERAL jsonb_to_record(i.body) AS r("Id" uuid,"TenantId" uuid,"ServiceReviewId" uuid,
                    "EvidenceType" text,"OrdersCount" integer,"RevenueAmount" numeric,"IsDeleted" boolean)
                WHERE i.export_id=@export AND i.source_table='MarketingReviewEvidences' AND NOT i.deleted)
            SELECT count(*)::integer,coalesce(sum(r."Rating"),0)::integer,
                count(*) FILTER(WHERE r."Rating"=5)::integer,count(*) FILTER(WHERE r."Rating"=4)::integer,
                count(*) FILTER(WHERE r."Rating"=3)::integer,count(*) FILTER(WHERE r."Rating"=2)::integer,
                count(*) FILTER(WHERE r."Rating"=1)::integer,max(r."CreatedAt"),
                count(e."Id")::integer,count(*) FILTER(WHERE e."EvidenceType"=@verified)::integer,
                coalesce(sum(e."OrdersCount") FILTER(WHERE e."EvidenceType"=@verified),0)::integer,
                coalesce(sum(e."RevenueAmount") FILTER(WHERE e."EvidenceType"=@verified),0)
            FROM reviews r JOIN requests q ON q."Id"=r."ServiceRequestId"
            LEFT JOIN evidence e ON e."ServiceReviewId"=r."Id" AND e."TenantId"=r."TenantId" AND NOT e."IsDeleted"
            WHERE q."ProviderProfileId"=@profile AND q."TenantId"=@tenant AND q."Status"=@completed
                AND NOT q."IsDeleted" AND NOT r."IsDeleted" AND r."TenantId"=q."TenantId"
                AND r."ServiceId"=q."ServiceId" AND r."ReviewerUserId"=q."BuyerUserId" AND r."ProviderUserId"=q."ProviderUserId"
            """,db))
        {
            Parameters(trust,export,tenantId,profileId);trust.Parameters.AddWithValue("verified",EvidenceType.PosVerified.ToString());
            await using var rows=await trust.ExecuteReaderAsync(ct);await rows.ReadAsync(ct);
            var aggregate=ProviderTrustAggregate.FromSqlReviewTruth(tenantId,profileId,rows.GetInt32(0),rows.GetInt32(1),
                rows.GetInt32(2),rows.GetInt32(3),rows.GetInt32(4),rows.GetInt32(5),rows.GetInt32(6),
                rows.IsDBNull(7) ? null : rows.GetDateTime(7),rows.GetInt32(8),rows.GetInt32(9),rows.GetInt32(10),rows.GetDecimal(11));
            document.Trust=new(){AverageRating=aggregate.AverageRating,TotalReviews=aggregate.TotalReviews,
                TrustBadge=aggregate.ComputeBadge().ToString(),MarketingReviewCount=aggregate.MarketingReviewCount,
                PosVerifiedCount=aggregate.PosVerifiedCount,MarketingWeightedScore=aggregate.MarketingWeightedScore};
        }
        return document;
    }

    private static void Parameters(NpgsqlCommand command,Guid export,Guid tenant,Guid profile)
    {
        command.Parameters.AddWithValue("export",export);command.Parameters.AddWithValue("tenant",tenant);
        command.Parameters.AddWithValue("profile",profile);command.Parameters.AddWithValue("completed",ServiceRequestStatus.CompletedByBuyer.ToString());
    }

    private static async Task<Input?> InputAsync(NpgsqlConnection db,Guid export,string table,Guid id,CancellationToken ct)
    {
        await using var query=new NpgsqlCommand("""
            SELECT deleted,body::text FROM marketplace_generation.export_inputs
            WHERE export_id=@export AND source_table=@table AND source_id=@id
            """,db);
        query.Parameters.AddWithValue("export",export);query.Parameters.AddWithValue("table",table);query.Parameters.AddWithValue("id",id);
        await using var rows=await query.ExecuteReaderAsync(ct);
        if(!await rows.ReadAsync(ct))return null;
        using var body=JsonDocument.Parse(rows.GetString(1));
        if(Identity(body.RootElement,"Id")!=id)throw new InvalidDataException("Frozen input identity mismatch.");
        return new(rows.GetBoolean(0),body.RootElement.Clone());
    }
    private static JsonElement Required(JsonElement row,string field)=>row.TryGetProperty(field,out var value)
        ? value : throw new InvalidDataException("Frozen projection field missing: "+field);
    private static Guid Identity(JsonElement row,string field)=>Required(row,field).GetGuid() is var id && id!=Guid.Empty
        ? id : throw new InvalidDataException("Frozen projection relationship empty: "+field);
    private static string Text(JsonElement row,string field)=>Required(row,field).GetString()
        ?? throw new InvalidDataException("Frozen projection text missing: "+field);
    private static string? OptionalText(JsonElement row,string field)=>Required(row,field).ValueKind==JsonValueKind.Null ? null : Text(row,field);
    private static T EnumValue<T>(JsonElement row,string field) where T:struct,Enum
    {
        var value=(T)Enum.ToObject(typeof(T),Required(row,field).GetInt32());
        return Enum.IsDefined(value) ? value : throw new InvalidDataException("Frozen projection enum rejected: "+field);
    }
    private sealed record Input(bool Deleted,JsonElement Body);
}

internal sealed record FrozenServiceCursor(Guid ExportId,Guid ServiceId);
internal sealed record FrozenServiceProjection(Guid ServiceId,MarketplaceServiceDocument? Document);
internal sealed record FrozenServicePage(FrozenGenerationInput Export,IReadOnlyList<FrozenServiceProjection> Services,FrozenServiceCursor? Next);
