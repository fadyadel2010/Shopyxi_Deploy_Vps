using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Elasticsearch.Configuration;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.Reliability;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Jobs;

/// <summary>
/// SQL-subordinate service-search recovery. Recovery never deletes/recreates an index,
/// replaces an existing document, or infers SQL ownership from search identities.
/// </summary>
public class ElasticsearchDriftReconciliationJob : IMarketplaceSearchRepairAuthority
{
    private readonly ApplicationDbContext _context;
    private readonly IOpenSearchClient _elasticClient;
    private readonly ILogger<ElasticsearchDriftReconciliationJob> _logger;
    private readonly IProviderTrustService _trust;
    private readonly MarketplaceTelemetry _telemetry;
    private readonly string _indexName;
    private readonly IMarketplaceGenerationMutationRouter? _generationRouter;
    private const string IndexName = MarketplaceServiceIndexConfiguration.IndexName;
    private const int BatchSize = 128;
    private static readonly Guid ServiceMarketplaceGlobalRebuildId =
        Guid.Parse("c4d9438e-11e2-41c8-b652-2ab638a642c2");

    public ElasticsearchDriftReconciliationJob(ApplicationDbContext context, IOpenSearchClient elasticClient,
        ILogger<ElasticsearchDriftReconciliationJob> logger, MarketplaceTelemetry telemetry,
        IProviderTrustService trust, IMarketplaceGenerationMutationRouter generationRouter)
        : this(context, elasticClient, logger, telemetry, trust, IndexName)
    {
        _generationRouter = generationRouter;
    }

    internal ElasticsearchDriftReconciliationJob(ApplicationDbContext context, IOpenSearchClient elasticClient,
        ILogger<ElasticsearchDriftReconciliationJob> logger, MarketplaceTelemetry telemetry, IProviderTrustService trust)
        : this(context, elasticClient, logger, telemetry, trust, IndexName) { }

    // Test-only index isolation; production DI always uses the established index name above.
    internal ElasticsearchDriftReconciliationJob(ApplicationDbContext context, IOpenSearchClient elasticClient,
        ILogger<ElasticsearchDriftReconciliationJob> logger, MarketplaceTelemetry telemetry,
        IProviderTrustService trust, string indexName)
    {
        _context = context;
        _elasticClient = elasticClient;
        _logger = logger;
        _trust = trust;
        _telemetry = telemetry;
        _indexName = indexName;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = SqlTruthRebuildGuard.RequireGlobalSqlAuthority(SqlTruthRebuildTarget.Search,
            ServiceMarketplaceGlobalRebuildId, SqlAuthorityOverlayKind.ServiceMarketplace);
        // Ordinary same-generation repair is not a globally consistent rebuild. Do not
        // report it as one or begin SQL/search work without complete capture and all-writer
        // fencing. RepairAsync remains the separately authorized non-topology repair route.
        return Task.FromException(new InvalidOperationException(MarketplaceSearchTopology.RecoveryBlockedCode));
    }

    public async Task<MarketplaceSearchRepairResult> RepairAsync(
        MarketplaceSearchRepairActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return await RepairCoreAsync(actor, cancellationToken);
    }

    private async Task<MarketplaceSearchRepairResult> RepairCoreAsync(
        MarketplaceSearchRepairActor? actor, CancellationToken ct)
    {
        // SQL locks are part of this protocol, not an optional best-effort optimization.
        if (!_context.Database.IsNpgsql() || _context.Database.CurrentTransaction != null)
            return new(false, "REPAIR_SQL_BOUNDARY_UNAVAILABLE", Errors: 1);
        if (actor != null && !await IsAuthorizedAsync(actor, ct))
            return new(false, "REPAIR_AUTHORITY_REJECTED", Errors: 1);
        if (_generationRouter is not null && await _generationRouter.TryRouteAsync(Guid.NewGuid(), "repair",
            actor?.TenantId ?? ServiceMarketplaceGlobalRebuildId, ct))
            return new(false, "GENERATION_DELIVERY_PENDING");

        var exists = await _elasticClient.Indices.ExistsAsync(_indexName, ct: ct);
        if (!exists.IsValid && exists.ApiCall.HttpStatusCode != 404)
            return new(false, "GLOBAL_SEARCH_INDEX_UNAVAILABLE", Errors: 1);
        // Missing indices are not recreated in place. A staged rebuild and cutover is a
        // separately approved topology change; until then recovery fails closed.
        if (!exists.Exists)
            return new(false, "GLOBAL_SEARCH_INDEX_UNAVAILABLE", Errors: 1);

        // No implicit topology/mapping migration: absent/incompatible indices need a separately
        // approved rebuild/cutover. A failed repair leaves the existing index and mappings intact.
        var mapping = await _elasticClient.Indices.GetMappingAsync(new GetMappingRequest(_indexName), ct);
        if (!mapping.IsValid || mapping.Indices.Count != 1)
            return new(false, "REPAIR_MAPPING_UNAVAILABLE", Errors: 1);
        var properties = mapping.Indices.Values.Single().Mappings.Properties;
        if (!properties.TryGetValue("trust", out var trustProperty) || trustProperty is not ObjectProperty ||
            !properties.TryGetValue("serviceStats", out var statsProperty) || statsProperty is not ObjectProperty)
            return new(false, "REPAIR_MAPPING_REQUIRES_SEPARATE_REBUILD", Errors: 1);

        var indexed = 0;
        var deleted = 0;
        var errors = 0;
        var missingInEs = 0;
        var staleInEs = 0;
        var extraInEs = 0;
        Guid? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var ids = await LoadLiveServiceSearchAuthorityAsync(actor, after).ToListAsync(ct);
            if (ids.Count == 0) break;
            foreach (var id in ids)
            {
                switch (await ConvergeOneAsync(id.ToString(), actor, false, ct))
                {
                    case Outcome.Created: indexed++; missingInEs++; break;
                    case Outcome.Updated: indexed++; staleInEs++; break;
                    case Outcome.Deleted: deleted++; extraInEs++; break;
                    case Outcome.Rejected: errors++; break;
                }
            }
            after = ids[^1];
        }

        var refresh = await _elasticClient.Indices.RefreshAsync(_indexName, ct: ct);
        if (!refresh.IsValid || refresh.Shards.Failed > 0)
            return new(false, "REPAIR_REFRESH_FAILED", indexed, deleted, errors + 1);

        // Scan actual _ids, not projected ServiceId values. Never use poisoned derivative IDs
        // to address a different shared document. Every deletion reloads SQL ownership below.
        var scan = await _elasticClient.SearchAsync<Dictionary<string, object>>(s => s.Index(_indexName)
            .Size(BatchSize).Scroll("1m").TrackTotalHits()
            .Query(q => actor?.TenantId is Guid tenantId
                ? q.Term(t => t.Field("tenantId").Value(tenantId)) : q.MatchAll()), ct);
        string? scrollId = scan.ScrollId;
        try
        {
            while (true)
            {
                if (!scan.IsValid || scan.TimedOut || scan.Shards.Failed > 0)
                {
                    errors++;
                    break;
                }
                if (scan.Hits.Count == 0) break;
                foreach (var hit in scan.Hits)
                {
                    switch (await ConvergeOneAsync(hit.Id, actor, true, ct))
                    {
                        case Outcome.Deleted: deleted++; extraInEs++; break;
                        case Outcome.Rejected: errors++; break;
                    }
                }
                if (string.IsNullOrEmpty(scrollId)) { errors++; break; }
                scan = await _elasticClient.ScrollAsync<Dictionary<string, object>>("1m", scrollId, ct: ct);
                scrollId = scan.ScrollId ?? scrollId;
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(scrollId))
            {
                if (!await ClearScrollSafelyAsync(scrollId)) errors++;
            }
        }
        // Pruning can happen after the first refresh. Do not report completed repair while
        // those deletions are still absent from refreshed search visibility.
        var finalRefresh = await _elasticClient.Indices.RefreshAsync(_indexName, ct: ct);
        if (!finalRefresh.IsValid || finalRefresh.Shards.Failed > 0) errors++;
        if (actor != null && !await IsAuthorizedAsync(actor, ct)) errors++;
        var totalDrift = missingInEs + staleInEs + extraInEs;
        if (totalDrift > 0)
        {
            OperationalConvergenceTelemetry.RecordDriftDiagnostic("marketplace_service_search",
                OperationalConvergenceAuthority.SearchIndex, OperationalConvergenceDecision.DriftDetected,
                OperationalConvergenceReason.DriftDetected, actor?.TenantId, totalDrift);
            _telemetry.RecordElasticsearchDrift(totalDrift);
        }
        else if (errors == 0)
        {
            OperationalConvergenceTelemetry.RecordDriftDiagnostic("marketplace_service_search",
                OperationalConvergenceAuthority.SearchIndex, OperationalConvergenceDecision.DriftWithinTolerance,
                OperationalConvergenceReason.DriftWithinTolerance, actor?.TenantId);
        }
        _logger.LogWarning("Service search repair completed | Actor: {Actor} | Tenant: {Tenant} | Indexed: {Indexed} | Deleted: {Deleted} | Errors: {Errors}",
            actor?.UserId, actor?.TenantId, indexed, deleted, errors);
        return new(errors == 0, errors == 0 ? "REPAIR_COMPLETE" : "REPAIR_INCOMPLETE", indexed, deleted, errors);
    }

    private async Task<bool> ClearScrollSafelyAsync(string scrollId)
    {
        // Cleanup must run after caller cancellation, but must never replace the original
        // cancellation/failure with a cleanup transport timeout or malformed response.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            // Client 2.3.0's typed ClearScrollResponse omits the server's succeeded field.
            var clear = await _elasticClient.LowLevel.DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.DELETE,
                "/_search/scroll", cleanup.Token, PostData.Serializable(new { scroll_id = new[] { scrollId } }));
            if (!clear.Success || string.IsNullOrWhiteSpace(clear.Body)) return false;
            using var body = JsonDocument.Parse(clear.Body);
            return body.RootElement.TryGetProperty("succeeded", out var succeeded) && succeeded.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Service search repair scroll cleanup failed | Index: {Index}", _indexName);
            return false;
        }
    }

    private IQueryable<Guid> LoadLiveServiceSearchAuthorityAsync(MarketplaceSearchRepairActor? actor, Guid? after)
    {
        var query = _context.Set<Service>().AsNoTracking().IgnoreQueryFilters();
        if (actor?.TenantId is Guid tenantId) query = query.Where(s => s.TenantId == tenantId);
        if (after is Guid cursor) query = query.Where(s => s.Id.CompareTo(cursor) > 0);
        return query.OrderBy(s => s.Id).Take(BatchSize).Select(s => s.Id);
    }

    private enum Outcome { Unchanged, Created, Updated, Deleted, Rejected }

    private async Task<Outcome> ConvergeOneAsync(string rawId, MarketplaceSearchRepairActor? actor, bool pruneOnly, CancellationToken ct)
    {
        Outcome Reject(string reason)
        {
            _logger.LogWarning("Service search repair item rejected | SearchId: {SearchId} | Tenant: {TenantId} | Reason: {Reason}",
                rawId, actor?.TenantId, reason);
            return Outcome.Rejected;
        }
        if (!Guid.TryParseExact(rawId, "D", out var id) || id == Guid.Empty || rawId != id.ToString())
            return Reject("noncanonical search identity");
        // Capture the search CAS BEFORE loading SQL. Any intervening projection/statistics/trust
        // write makes this operation conflict rather than overwriting its newer state. No retry_on_conflict.
        var existing = await _elasticClient.GetAsync<Dictionary<string, object>>(rawId, g => g.Index(_indexName), ct);
        if ((!existing.IsValid && existing.ApiCall.HttpStatusCode != 404) ||
            existing.ServerError?.Error?.Type == "index_not_found_exception") return Reject("search read failed or index missing");
        var source = existing.Source;
        if (existing.Found && (source is null || !HasIdentity(source, "serviceId", id) ||
            !TryIdentity(source, "tenantId", out _))) return Reject("malformed derivative identity");

        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        // Service/profile/tenant share locks are held through the HTTP CAS, so lifecycle changes
        // cannot commit between current SQL admission and the derivative write. No session/global locks.
        var service = await _context.Set<Service>()
            .FromSqlInterpolated($"SELECT xmin, * FROM \"Services\" WHERE \"Id\" = {id} FOR SHARE NOWAIT")
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(ct);
        if (service == null || actor?.TenantId is Guid scope && service.TenantId != scope)
            return existing.Found ? Reject("missing SQL identity or tenant mismatch") : Outcome.Unchanged;
        var tenant = await _context.Tenants
            .FromSqlInterpolated($"SELECT xmin, * FROM \"Tenants\" WHERE \"Id\" = {service.TenantId} FOR SHARE NOWAIT")
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(ct);
        var profile = await _context.Set<MarketplaceProfile>()
            .FromSqlInterpolated($"SELECT xmin, * FROM \"MarketplaceProfiles\" WHERE \"Id\" = {service.ProfileId} FOR SHARE NOWAIT")
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(ct);
        if (actor != null && !await IsAuthorizedAsync(actor, ct)) return Reject("current SQL actor rejected");
        if (existing.Found && (source is null || !HasIdentity(source, "tenantId", service.TenantId)))
            return Reject("search tenant differs from SQL owner");

        // A prior absence check does not fence creation/suspension of a governance row.
        // Privileged repair therefore requires a persisted governance identity and holds its
        // share lock through the search CAS. Normal public-read admission is unchanged.
        var vendor = await _context.UserGovernanceProfiles
            .FromSqlInterpolated($"SELECT xmin, * FROM \"UserGovernanceProfiles\" WHERE \"SubjectId\" = {service.TenantId} AND \"Role\" = {(int)UserGovernanceRole.Vendor} FOR SHARE NOWAIT")
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(ct);
        if (vendor == null) return Reject("SQL vendor governance identity missing");

        var live = IsLiveSqlLineage(service, tenant, profile, vendor);
        if (!live)
        {
            if (!existing.Found) return Outcome.Unchanged;
            var response = await _elasticClient.DeleteAsync<Dictionary<string, object>>(rawId, d => d.Index(_indexName)
                .IfSequenceNumber(existing.SequenceNumber).IfPrimaryTerm(existing.PrimaryTerm), ct);
            if (!response.IsValid || response.Result != Result.Deleted)
                return Reject($"guarded delete failed: HTTP {response.ApiCall.HttpStatusCode}, {response.ServerError?.Error?.Type}");
            await transaction.CommitAsync(ct);
            return Outcome.Deleted;
        }
        if (pruneOnly) return Outcome.Unchanged;
        if (existing.Found && (source is null || !HasIdentity(source, "providerProfileId", service.ProfileId)))
            return Reject("search provider differs from SQL service profile");

        // The search source is never business truth. Recompose every business metric from the
        // same locked SQL snapshot. Missing authority is failure, not a synthetic zero aggregate.
        var trust = await _trust.GetTrustAggregateAsync(service.ProfileId, ct);
        if (trust == null || trust.TenantId != service.TenantId || trust.ProviderProfileId != service.ProfileId)
            return Reject("SQL trust authority missing or mismatched");
        var requests = _context.Set<ServiceRequest>().IgnoreQueryFilters().AsNoTracking().Where(r =>
            r.ServiceId == id && r.TenantId == service.TenantId && r.ProviderProfileId == service.ProfileId && !r.IsDeleted);
        var total = await requests.CountAsync(ct);
        var completed = await requests.CountAsync(r => r.Status == ServiceRequestStatus.CompletedByBuyer, ct);

        // A partial document update replaces nested objects, so carry the event watermark
        // fields forward explicitly. Watermarks are lineage metadata, not search-derived truth.
        var trustFields = new Dictionary<string, object?>
        {
            ["averageRating"] = trust.AverageRating, ["totalReviews"] = trust.TotalReviews,
            ["trustBadge"] = trust.ComputeBadge().ToString(), ["marketingReviewCount"] = trust.MarketingReviewCount,
            ["posVerifiedCount"] = trust.PosVerifiedCount, ["marketingWeightedScore"] = trust.MarketingWeightedScore
        };
        if (existing.Found) PreserveTrustWatermarks(source, trustFields);
        var fields = new Dictionary<string, object?>
        {
            ["serviceId"] = id.ToString(), ["tenantId"] = service.TenantId.ToString(),
            ["title"] = service.Title, ["description"] = service.Description,
            ["categoryId"] = service.Category, ["categoryPath"] = service.Category,
            ["providerProfileId"] = service.ProfileId.ToString(), ["providerDisplayName"] = profile!.DisplayName,
            ["createdAt"] = service.CreatedAt, ["isActive"] = true,
            ["startingPrice"] = service.StartingPrice, ["pricingType"] = service.PricingType?.ToString(),
            ["currency"] = service.Currency, ["serviceImages"] = service.ServiceImages,
            ["serviceStats"] = new { totalRequests = total, completedRequests = completed,
                completionRate = total == 0 ? 0m : (decimal)completed / total },
            ["trust"] = trustFields
        };
        if (existing.Found)
        {
            // CAS rejects a newer projection/statistics/trust update, with no automatic stale retry.
            var response = await _elasticClient.UpdateAsync<Dictionary<string, object>, Dictionary<string, object?>>(rawId,
                u => u.Index(_indexName).IfSequenceNumber(existing.SequenceNumber).IfPrimaryTerm(existing.PrimaryTerm).Doc(fields), ct);
            if (!response.IsValid || response.Result is not (Result.Updated or Result.Noop))
                return Reject("conditional search write failed; no stale retry performed");
            await transaction.CommitAsync(ct);
            return response.Result == Result.Updated ? Outcome.Updated : Outcome.Unchanged;
        }
        // An actually empty SQL set can yield zero; no event watermark is invented on create.
        var created = await _elasticClient.IndexAsync(fields, i => i.Index(_indexName).Id(rawId).OpType(OpType.Create), ct);
        if (!created.IsValid || created.Result != Result.Created)
            return Reject("conditional search write failed; no stale retry performed");
        await transaction.CommitAsync(ct);
        return Outcome.Created;
    }

    // Only the pure visibility predicate. This is not actor admission, a SQL lock,
    // a generation write token, physical identity proof, or external fencing.
    internal static bool IsLiveSqlLineage(Service service, Tenant? tenant,
        MarketplaceProfile? profile, UserGovernanceProfile? vendor) =>
        !service.IsDeleted && service.IsActive &&
        tenant != null && tenant.Id == service.TenantId && tenant.IsActive &&
        tenant.Status == TenantStatus.Active && tenant.DeletedAtUtc == null &&
        profile != null && profile.Id == service.ProfileId && !profile.IsDeleted &&
        profile.IsActive && profile.TenantId == service.TenantId &&
        vendor != null && vendor.SubjectId == service.TenantId && vendor.Role == UserGovernanceRole.Vendor &&
        vendor.Status is UserGovernanceStatus.Active or UserGovernanceStatus.FinanciallyLocked;

    private static void PreserveTrustWatermarks(Dictionary<string, object>? source,
        Dictionary<string, object?> trustFields)
    {
        if (source is null) return;
        try
        {
            var document = JsonSerializer.SerializeToElement(source);
            if (!document.TryGetProperty("trust", out var trust) || trust.ValueKind != JsonValueKind.Object) return;
            if (trust.TryGetProperty("lastEventId", out var eventId) && eventId.ValueKind == JsonValueKind.String &&
                Guid.TryParse(eventId.GetString(), out var parsedEventId) && parsedEventId != Guid.Empty)
                trustFields["lastEventId"] = parsedEventId.ToString();
            if (trust.TryGetProperty("lastEventOccurredAtUtc", out var occurredAt) &&
                occurredAt.ValueKind == JsonValueKind.String)
                trustFields["lastEventOccurredAtUtc"] = occurredAt.GetString();
            if (trust.TryGetProperty("lastEventVersion", out var eventVersion) &&
                eventVersion.ValueKind == JsonValueKind.Number && eventVersion.TryGetInt32(out var parsedVersion) &&
                parsedVersion > 0)
                trustFields["lastEventVersion"] = parsedVersion;
        }
        catch (JsonException)
        {
            // A malformed derivative watermark is never replaced with an invented value.
        }
    }

    private async Task<bool> IsAuthorizedAsync(MarketplaceSearchRepairActor actor, CancellationToken ct)
    {
        if (actor.UserId == Guid.Empty) return false;
        if (actor.TenantId is Guid tenantId)
        {
            if (actor.SuperAdminTokenVersion != null || tenantId == Guid.Empty) return false;
            if (_context.Database.CurrentTransaction != null)
            {
                var locked = await _context.Users
                    .FromSqlInterpolated($"SELECT xmin, * FROM \"Users\" WHERE \"Id\" = {actor.UserId} FOR SHARE NOWAIT")
                    .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(ct);
                if (locked == null || locked.TenantId != tenantId || !locked.IsActive || locked.IsDeleted || locked.Role != UserRole.Admin) return false;
            }
            return await _context.Users.IgnoreQueryFilters().AsNoTracking().AnyAsync(u =>
                u.Id == actor.UserId && u.TenantId == tenantId && u.Role == UserRole.Admin && u.IsActive && !u.IsDeleted &&
                _context.Tenants.IgnoreQueryFilters().Any(t => t.Id == tenantId && t.IsActive && t.Status == TenantStatus.Active && t.DeletedAtUtc == null), ct);
        }
        if (actor.SuperAdminTokenVersion is not int version) return false;
        if (_context.Database.CurrentTransaction != null)
        {
            var locked = await _context.SuperAdmins
                .FromSqlInterpolated($"SELECT xmin, * FROM \"SuperAdmins\" WHERE \"Id\" = {actor.UserId} FOR SHARE NOWAIT")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (locked == null || locked.TokenVersion != version) return false;
            // An absence/ordinary read cannot fence insertion or suspension of governance.
            // Require persisted active authority and hold its row lock through the search CAS.
            var governance = await _context.UserGovernanceProfiles
                .FromSqlInterpolated($"SELECT xmin, * FROM \"UserGovernanceProfiles\" WHERE \"SubjectId\" = {actor.UserId} AND \"Role\" = {(int)UserGovernanceRole.Admin} FOR SHARE NOWAIT")
                .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(ct);
            if (governance == null || governance.Status != UserGovernanceStatus.Active) return false;
        }
        return await _context.SuperAdmins.AsNoTracking().AnyAsync(a => a.Id == actor.UserId && a.TokenVersion == version, ct) &&
            await _context.UserGovernanceProfiles.IgnoreQueryFilters().AsNoTracking().AnyAsync(g => g.SubjectId == actor.UserId &&
                g.Role == UserGovernanceRole.Admin && g.Status == UserGovernanceStatus.Active, ct);
    }

    private static bool TryIdentity(Dictionary<string, object> source, string field, out Guid id)
    {
        id = Guid.Empty;
        return source.TryGetValue(field, out var value) && Guid.TryParse(value?.ToString(), out id) && id != Guid.Empty;
    }
    private static bool HasIdentity(Dictionary<string, object> source, string field, Guid id) =>
        TryIdentity(source, field, out var actual) && actual == id;

}
