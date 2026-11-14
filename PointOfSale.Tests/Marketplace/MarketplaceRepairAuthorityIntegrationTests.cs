using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.API.SuperAdmin.Models;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Abstractions;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.SuperAdmin.Models;
using PointOfSale.Infrastructure.Configuration;
using PointOfSale.Infrastructure.Consumers;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Elasticsearch.Configuration;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.Jobs;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

// No skip/fallback: every test needs the disposable PostgreSQL18 + authenticated TLS OS3.9 pair.
[Collection("Marketplace repair authority")]
public sealed class MarketplaceRepairAuthorityIntegrationTests
{
    [Fact]
    public async Task RealThreeShardNumericNegativeTermsBoundsReplayUnchangedThroughPinnedSerializerAcrossCultures()
    {
        await using var f = await Fixture.CreateAsync();
        var index = f.Index + "-numeric-terms";
        var create = await f.Control.Indices.CreateAsync(index, c => c.Settings(s => s.NumberOfShards(3).NumberOfReplicas(0))
            .Map<Dictionary<string, object>>(m => m.Properties(p => p.Keyword(k => k.Name("facet_key")))));
        Assert.True(create.IsValid && create.Acknowledged && create.ShardsAcknowledged, create.DebugInformation);
        try
        {
            for (var i = 0; i < 48; i++)
            {
                var inserted = await f.Control.IndexAsync(new Dictionary<string, object> { ["facet_key"] = "fixture-term-" + i % 12 },
                    d => d.Index(index).Id(i).Refresh(Refresh.True));
                Assert.True(inserted.IsValid, inserted.DebugInformation);
            }
            // A fully enumerated shard can correctly return zero even for
            // ascending count. Prove every actual primary has more than two
            // distinct terms, then explicitly truncate its candidate set to two.
            const string coverageQuery = """
                {"size":0,"aggs":{"fixture_terms":{"terms":{"field":"facet_key","size":100,"shard_size":100}}}}
                """;
            for (var shard = 0; shard < 3; shard++)
            {
                var coverageParameters = new SearchRequestParameters();
                coverageParameters.SetQueryString("typed_keys", true);
                coverageParameters.SetQueryString("preference", "_shards:" + shard.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var coverage = await f.Control.LowLevel.SearchAsync<StringResponse>(index, PostData.String(coverageQuery), coverageParameters);
                f.Record("numeric-terms-shard-coverage", new { Shard = shard, Request = coverageQuery, Response = coverage.Body });
                Assert.True(coverage.Success, coverage.Body);
                using var coverageJson = JsonDocument.Parse(coverage.Body);
                Assert.Equal(1, coverageJson.RootElement.GetProperty("_shards").GetProperty("successful").GetInt32());
                Assert.True(coverageJson.RootElement.GetProperty("aggregations").GetProperty("sterms#fixture_terms").GetProperty("buckets").GetArrayLength() > 2,
                    "Each actual primary must have excluded terms under shard_size=2; no fabricated -1 response or one-shard substitute.");
            }
            // Ascending count with actual excluded shard candidates has an
            // unbounded error estimate (-1), not a malformed number.
            const string query = """
                {"size":0,"aggs":{"fixture_terms":{"terms":{"field":"facet_key","size":2,"shard_size":2,"order":{"_count":"asc"},"show_term_doc_count_error":true}}}}
                """;
            var parameters = new SearchRequestParameters(); parameters.SetQueryString("typed_keys", true);
            var captured = await f.Control.LowLevel.SearchAsync<StringResponse>(index, PostData.String(query), parameters);
            f.Record("numeric-terms-raw-before-assertions", new { Request = query, Response = captured.Body });
            Assert.True(captured.Success, captured.Body);
            using var raw = JsonDocument.Parse(captured.Body);
            Assert.Equal(3, raw.RootElement.GetProperty("_shards").GetProperty("total").GetInt32());
            Assert.Equal(3, raw.RootElement.GetProperty("_shards").GetProperty("successful").GetInt32());
            var aggregate = raw.RootElement.GetProperty("aggregations").GetProperty("sterms#fixture_terms");
            Assert.Equal(JsonValueKind.Number, aggregate.GetProperty("doc_count_error_upper_bound").ValueKind);
            Assert.Equal(-1L, aggregate.GetProperty("doc_count_error_upper_bound").GetInt64());
            var buckets = aggregate.GetProperty("buckets").EnumerateArray().ToArray(); Assert.Equal(2, buckets.Length);
            Assert.All(buckets, bucket =>
            {
                Assert.Equal(JsonValueKind.Number, bucket.GetProperty("doc_count_error_upper_bound").ValueKind);
                Assert.Equal(-1L, bucket.GetProperty("doc_count_error_upper_bound").GetInt64());
            });
            var bytes = Encoding.UTF8.GetBytes(captured.Body);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "", "ar-EG", "fa-IR", "fr-FR" })
                {
                    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                    using var stream = new MemoryStream(bytes, writable: false);
                    var replay = f.Client.RequestResponseSerializer.Deserialize<SearchResponse<Dictionary<string, object>>>(stream);
                    var parsed = replay.Aggregations.Terms("fixture_terms"); Assert.NotNull(parsed);
                    Assert.Equal(-1L, parsed.DocCountErrorUpperBound); Assert.Equal(2, parsed.Buckets.Count);
                    Assert.All(parsed.Buckets, b => Assert.Equal(-1L, b.DocCountErrorUpperBound));
                    Assert.Equal(digest, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
                }
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
            // Separate live typed transport call, not only a parsed raw response.
            var live = await f.Client.SearchAsync<Dictionary<string, object>>(s => s.Index(index).Size(0)
                .Aggregations(a => a.Terms("fixture_terms", t => t.Field("facet_key").Size(2).ShardSize(2).Order(o => o.CountAscending()).ShowTermDocCountError())));
            Assert.True(live.IsValid, live.DebugInformation);
            Assert.Equal(-1L, live.Aggregations.Terms("fixture_terms").DocCountErrorUpperBound);
            Assert.All(live.Aggregations.Terms("fixture_terms").Buckets, b => Assert.Equal(-1L, b.DocCountErrorUpperBound));
            f.Record("real-three-shard-numeric-negative-bound-replay", new { Index = index, Request = query, Response = captured.Body,
                ResponseSHA256 = digest, Cultures = new[] { "", "ar-EG", "fa-IR", "fr-FR" },
                Serializer = f.Client.RequestResponseSerializer.GetType().AssemblyQualifiedName,
                Client = typeof(OpenSearchClient).Assembly.FullName, Scope = "Real OS3.9.0 numeric bounds only, no quoted tolerance or endpoint/fallback certification" });
        }
        finally
        {
            Assert.True((await f.Control.Indices.DeleteAsync(index)).IsValid);
            Assert.False((await f.Control.Indices.ExistsAsync(index)).Exists);
        }
    }

    [Fact]
    public async Task GenerationWholeDocumentScriptKeepsLongVersionsTombstonesAndRejectsOldEpochOrDivergentContent()
    {
        await using var f = await Fixture.CreateAsync();
        var journal = Guid.NewGuid(); var generation = Guid.NewGuid(); var service = f.Service.Id;
        var ticket = new ExternalWriteTicket(Guid.NewGuid(), Guid.NewGuid(), generation, Guid.NewGuid(), service,
            long.MaxValue - 10, "fixture-physical", new string('a', 64), new string('b', 64), 1);
        var live = JsonSerializer.Serialize(new { serviceId = service, title = "Versioned", isActive = true });
        ticket = ticket with { Digest = MarketplaceFrozenOutput.RowDigest(service, live) };
        async Task<StringResponse> Write(ExternalWriteTicket version, string? body, Guid? lineage = null)
        {
            // Raw SDK paths must not embed a query string. Let the pinned SDK
            // encode request parameters without changing the CAS payload/script.
            var parameters = new UpdateRequestParameters();
            parameters.SetQueryString("refresh", true);
            return await f.Control.LowLevel.UpdateAsync<StringResponse>(f.Index, service.ToString("D"),
                PostData.String(MarketplaceVersionedDocument.BuildUpdate(lineage ?? journal, version, body)), parameters);
        }
        var created = await Write(ticket, live); Assert.True(created.Success, created.Body);
        var duplicate = await Write(ticket, live); Assert.True(duplicate.Success, duplicate.Body);
        using (var response = JsonDocument.Parse(duplicate.Body)) Assert.Equal("noop", response.RootElement.GetProperty("result").GetString());
        var tombstone = ticket with { SourceRevision = ticket.SourceRevision + 1, Digest = MarketplaceFrozenOutput.RowDigest(service, null), Epoch = 2 };
        var deleted = await Write(tombstone, null); Assert.True(deleted.Success, deleted.Body);
        var staleEpoch = await Write(ticket with { SourceRevision = tombstone.SourceRevision + 1 }, live);
        Assert.False(staleEpoch.Success); Assert.Contains("MARKETPLACE_STALE_EPOCH", staleEpoch.Body);
        var staleSource = await Write(ticket with { Epoch = 3 }, live); Assert.True(staleSource.Success, staleSource.Body);
        var conflict = await Write(tombstone with { Epoch = 3, Digest = ticket.Digest }, live);
        Assert.False(conflict.Success); Assert.Contains("MARKETPLACE_REVISION_CONTENT_CONFLICT", conflict.Body);
        var foreign = await Write(tombstone with { Epoch = 3 }, null, Guid.NewGuid());
        Assert.False(foreign.Success); Assert.Contains("MARKETPLACE_LINEAGE_MISMATCH", foreign.Body);
        var raw = await f.Control.LowLevel.DoRequestAsync<StringResponse>(global::OpenSearch.Net.HttpMethod.GET,
            $"{f.Index}/_doc/{service:D}", default);
        Assert.True(raw.Success, raw.Body);
        using var persisted = JsonDocument.Parse(raw.Body); var source = persisted.RootElement.GetProperty("_source");
        Assert.False(source.GetProperty("isActive").GetBoolean()); Assert.False(source.TryGetProperty("title", out _));
        Assert.True(source.GetProperty("__marketplace").GetProperty("deleted").GetBoolean());
        Assert.Equal(tombstone.SourceRevision, source.GetProperty("__marketplace").GetProperty("revision").GetInt64());
        f.Record("generation-source-version-tombstone-script", new { created.Body, Tombstone = deleted.Body, Rejected = staleEpoch.Body });
    }

    [Fact]
    public async Task ActualPhysicalInspectorDetectsMappingDriftAndSameNameRecreationWithoutGrantingWriteAuthority()
    {
        await using var f = await Fixture.CreateAsync();
        var journal = Guid.NewGuid(); var generation = Guid.NewGuid(); var name = "marketplace_services_g_" + generation.ToString("N");
        var definition = JsonSerializer.Serialize(new
        {
            settings = new { number_of_shards = 1, number_of_replicas = 0 },
            mappings = new Dictionary<string, object>
            {
                ["_meta"] = new { journal = journal.ToString("D"), generation = generation.ToString("D"), schema = "marketplace-generation-v1" },
                ["properties"] = new Dictionary<string, object>
                {
                    ["serviceId"] = new { type = "keyword" }, ["isActive"] = new { type = "boolean" },
                    ["__marketplace"] = new Dictionary<string, object>
                    {
                        ["dynamic"] = "strict", ["properties"] = new Dictionary<string, object>
                        {
                            ["journal"] = new { type = "keyword" }, ["generation"] = new { type = "keyword" }, ["digest"] = new { type = "keyword" },
                            ["revision"] = new { type = "long" }, ["epoch"] = new { type = "long" }, ["deleted"] = new { type = "boolean" }
                        }
                    }
                }
            }
        });
        string? ownedUuid = null;
        async Task<string> PhysicalBody()
        {
            var response = await f.Control.LowLevel.DoRequestAsync<StringResponse>(global::OpenSearch.Net.HttpMethod.GET, "/" + name, default);
            Assert.True(response.Success, response.Body); return response.Body;
        }
        static (string Uuid, string Fingerprint) Identity(string body, string index)
        {
            using var parsed = JsonDocument.Parse(body); var physical = parsed.RootElement.GetProperty(index);
            var settings = physical.GetProperty("settings").GetProperty("index");
            return (settings.GetProperty("uuid").GetString()!, MarketplacePhysicalAdmission.Fingerprint(physical.GetProperty("mappings"), settings));
        }
        async Task Create()
        {
            var created = await f.Control.LowLevel.DoRequestAsync<StringResponse>(global::OpenSearch.Net.HttpMethod.PUT, "/" + name, default, PostData.String(definition));
            Assert.True(created.Success, created.Body);
            ownedUuid = Identity(await PhysicalBody(), name).Uuid;
        }
        try
        {
            await Create(); var originalBody = await PhysicalBody(); var original = Identity(originalBody, name);
            var cluster = await f.Control.LowLevel.DoRequestAsync<StringResponse>(global::OpenSearch.Net.HttpMethod.GET, "/", default);
            Assert.True(cluster.Success, cluster.Body); using var clusterJson = JsonDocument.Parse(cluster.Body);
            var expected = new MarketplacePhysicalAdmission.Expected(journal, generation,
                clusterJson.RootElement.GetProperty("cluster_uuid").GetString()!, original.Uuid, original.Fingerprint);
            Assert.Equal(original.Uuid, (await MarketplacePhysicalAdmission.InspectAsync(f.Control, expected)).PhysicalUuid);
            var drift = await f.Control.LowLevel.DoRequestAsync<StringResponse>(global::OpenSearch.Net.HttpMethod.PUT, "/" + name + "/_mapping", default,
                PostData.String("{\"properties\":{\"unreviewed_fixture_field\":{\"type\":\"keyword\"}}}"));
            Assert.True(drift.Success, drift.Body);
            await Assert.ThrowsAsync<InvalidDataException>(() => MarketplacePhysicalAdmission.InspectAsync(f.Control, expected));
            Assert.Equal(ownedUuid, Identity(await PhysicalBody(), name).Uuid);
            Assert.True((await f.Control.Indices.DeleteAsync(name)).IsValid); ownedUuid = null;
            await Create(); var recreatedBody = await PhysicalBody(); var recreated = Identity(recreatedBody, name);
            Assert.NotEqual(original.Uuid, recreated.Uuid); Assert.Equal(original.Fingerprint, recreated.Fingerprint);
            await Assert.ThrowsAsync<InvalidDataException>(() => MarketplacePhysicalAdmission.InspectAsync(f.Control, expected));
            Assert.Equal(recreated.Uuid, (await MarketplacePhysicalAdmission.InspectAsync(f.Control, expected with { PhysicalUuid = recreated.Uuid })).PhysicalUuid);
            f.Record("actual-physical-admission-recreation", new { PhysicalName = name, ClusterResponse = cluster.Body, OriginalPhysicalResponse = originalBody,
                RecreatedPhysicalResponse = recreatedBody, OriginalUuid = original.Uuid, RecreatedUuid = recreated.Uuid, original.Fingerprint,
                Scope = "Actual read-only inspector detects current mapping drift/UUID recreation; all_access fixture setup, not least privilege, an atomic remote fence, writer authority or readiness" });
        }
        finally
        {
            if (ownedUuid != null)
            {
                Assert.Equal(ownedUuid, Identity(await PhysicalBody(), name).Uuid);
                Assert.True((await f.Control.Indices.DeleteAsync(name)).IsValid);
                Assert.False((await f.Control.Indices.ExistsAsync(name)).Exists);
            }
        }
    }

    [Fact]
    public async Task TenantAdminCannotInvokeGlobalRepair_EvenByDirectControllerCall()
    {
        await using var f = await Fixture.CreateAsync();
        var controller = f.Controller(false);
        Assert.IsType<ForbidResult>(await controller.ReindexServicesGlobally(default));
        var policy = typeof(MarketplaceController).GetMethod(nameof(MarketplaceController.ReindexServicesGlobally))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single();
        Assert.Equal(SuperAdminSecurityConstants.PolicyName, policy.Policy);
        Assert.False((await f.Repair.RepairAsync(new(f.Admin.Id, null, 0))).Success);
    }

    [Fact]
    public async Task CurrentSqlRevocationRejectsGlobalAndTenantAuthority()
    {
        await using var f = await Fixture.CreateAsync();
        f.SuperAdmin.ClearRefreshToken();
        f.Admin.IsActive = false;
        await f.Db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await f.Controller(true).ReindexServicesGlobally(default));
        Assert.IsType<ForbidResult>(await f.Controller(false).ReindexServices(default));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("suspended")]
    [InlineData("banned")]
    public async Task GlobalRepairRequiresPersistedActiveSqlGovernance(string state)
    {
        await using var f = await Fixture.CreateAsync(includeGlobalGovernance: state != "missing");
        if (state != "missing") f.SuperAdminGovernance.Status = state == "suspended" ? UserGovernanceStatus.Suspended : UserGovernanceStatus.Banned;
        await f.Db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await f.Controller(true).ReindexServicesGlobally(default));
        var result = await f.Repair.RepairAsync(new(f.SuperAdmin.Id, null, 0));
        Assert.False(result.Success);
        Assert.Equal("REPAIR_AUTHORITY_REJECTED", result.Code);
        Assert.False((await f.Control.GetAsync<Dictionary<string, object>>(f.Service.Id.ToString(), g => g.Index(f.Index))).Found);
        f.Record("global-governance-negative", new { state, result });
    }

    [Fact]
    public async Task GlobalActorSuspensionCannotCommitBetweenSqlAdmissionAndTheSearchWrite()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);
        var attempted = false;
        f.AfterStatisticsRead = async () =>
        {
            attempted = true;
            await using var sql = new NpgsqlConnection(f.Db.Database.GetConnectionString());
            await sql.OpenAsync();
            await using var command = new NpgsqlCommand("SET lock_timeout = '250ms'; UPDATE \"UserGovernanceProfiles\" SET \"Status\"=@status WHERE \"Id\"=@id", sql);
            command.Parameters.AddWithValue("status", (int)UserGovernanceStatus.Suspended);
            command.Parameters.AddWithValue("id", f.SuperAdminGovernance.Id);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            f.Record("global-governance-write-fenced", new { error.SqlState });
        };
        Assert.True((await f.Repair.RepairAsync(new(f.SuperAdmin.Id, null, 0))).Success);
        Assert.True(attempted);
        f.AfterStatisticsRead = null;
        f.SuperAdminGovernance.Status = UserGovernanceStatus.Suspended;
        await f.Db.SaveChangesAsync();
        var before = await f.RawGetAsync(f.Service.Id.ToString());
        Assert.IsType<ForbidResult>(await f.Controller(true).ReindexServicesGlobally(default));
        Assert.Equal(before, await f.RawGetAsync(f.Service.Id.ToString()));
    }

    [Fact]
    public async Task StatisticsAndTrustRecomposeFromSql_WhileWatermarksAndOtherTenantDocumentsRemainIntact()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);
        await f.SeedSearchAsync(f.OtherService);
        var before = await f.GetAsync(f.Service.Id);
        var beforeTrust = before.GetProperty("trust");
        var other = await f.RawGetAsync(f.OtherService.Id.ToString());
        var mapping = await f.MappingAsync();
        Assert.True((await f.Repair.RepairAsync(f.Actor)).Success);
        Assert.True((await f.Repair.RepairAsync(f.Actor)).Success);
        var after = await f.GetAsync(f.Service.Id);
        Assert.Equal(f.Service.Title, after.GetProperty("title").GetString());
        Assert.Equal(5, after.GetProperty("serviceStats").GetProperty("totalRequests").GetInt32());
        Assert.Equal(3, after.GetProperty("serviceStats").GetProperty("completedRequests").GetInt32());
        Assert.Equal(3, after.GetProperty("trust").GetProperty("totalReviews").GetInt32());
        Assert.Equal(1, after.GetProperty("trust").GetProperty("marketingReviewCount").GetInt32());
        Assert.Equal(1, after.GetProperty("trust").GetProperty("posVerifiedCount").GetInt32());
        Assert.Equal(beforeTrust.GetProperty("lastEventId").GetString(),
            after.GetProperty("trust").GetProperty("lastEventId").GetString());
        Assert.Equal(beforeTrust.GetProperty("lastEventOccurredAtUtc").GetString(),
            after.GetProperty("trust").GetProperty("lastEventOccurredAtUtc").GetString());
        Assert.Equal(beforeTrust.GetProperty("lastEventVersion").GetInt32(),
            after.GetProperty("trust").GetProperty("lastEventVersion").GetInt32());
        Assert.Equal(other, await f.RawGetAsync(f.OtherService.Id.ToString()));
        Assert.Equal(mapping, await f.MappingAsync());
        f.Record("sql-recomposed-derived-data", new { before, after, otherTenant = f.OtherService.TenantId, mapping });
    }

    [Theory]
    [InlineData("wrong-source-id")]
    [InlineData("wrong-source-tenant")]
    [InlineData("malformed-id")]
    [InlineData("missing-sql")]
    public async Task PoisonedDerivativeIdentityCannotDeleteAnotherTenantsDocument(string poison)
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);
        await f.SeedSearchAsync(f.OtherService);
        string id;
        if (poison == "wrong-source-id")
        {
            id = f.Service.Id.ToString();
            await f.PutAsync(id, f.Document(f.Service) with { ServiceId = f.OtherService.Id });
        }
        else if (poison == "wrong-source-tenant")
        {
            id = f.OtherService.Id.ToString();
            await f.PutAsync(id, f.Document(f.OtherService) with { TenantId = f.Tenant.Id });
        }
        else
        {
            id = poison == "malformed-id" ? "malformed-" + Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString();
            await f.PutAsync(id, f.Document(f.Service) with { ServiceId = poison == "missing-sql" ? Guid.Parse(id) : f.OtherService.Id });
        }
        var other = await f.RawGetAsync(f.OtherService.Id.ToString());
        var poisoned = await f.RawGetAsync(id);
        var result = await f.Repair.RepairAsync(f.Actor);
        Assert.False(result.Success);
        Assert.True(result.Errors > 0);
        Assert.Equal(other, await f.RawGetAsync(f.OtherService.Id.ToString()));
        Assert.Equal(poisoned, await f.RawGetAsync(id));
        f.Record("identity-negative", new { poison, id, result, poisoned, other });
    }

    [Theory]
    [InlineData("tenant-inactive")]
    [InlineData("tenant-disabled")]
    [InlineData("tenant-deleted")]
    [InlineData("vendor-suspended")]
    [InlineData("vendor-banned")]
    [InlineData("profile-inactive")]
    [InlineData("profile-deleted")]
    [InlineData("profile-mismatch")]
    [InlineData("service-inactive")]
    [InlineData("service-deleted")]
    public async Task IneligibleSqlLineageCannotBeRestored(string state)
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);
        await f.SeedSearchAsync(f.OtherService);
        var other = await f.RawGetAsync(f.OtherService.Id.ToString());
        switch (state)
        {
            case "tenant-inactive": f.Tenant.Status = TenantStatus.Suspended; break;
            case "tenant-disabled": f.Tenant.IsActive = false; break;
            case "tenant-deleted": f.Tenant.DeletedAtUtc = DateTime.UtcNow; break;
            case "vendor-suspended": f.Vendor.Status = UserGovernanceStatus.Suspended; break;
            case "vendor-banned": f.Vendor.Status = UserGovernanceStatus.Banned; break;
            case "profile-inactive": f.Profile.Deactivate(); break;
            case "profile-deleted": f.Profile.IsDeleted = true; break;
            case "profile-mismatch": f.Profile.TenantId = f.OtherService.TenantId; break;
            case "service-inactive": f.Service.Deactivate(); break;
            case "service-deleted": f.Service.IsDeleted = true; break;
        }
        // Deliberate corrupt-lineage input is introduced by fixture SQL only, never a production bypass.
        if (state == "profile-mismatch")
        {
            await using var sql = new NpgsqlConnection(f.Db.Database.GetConnectionString()); await sql.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE \"MarketplaceProfiles\" SET \"TenantId\"=@tenant WHERE \"Id\"=@id", sql);
            cmd.Parameters.AddWithValue("tenant", f.OtherService.TenantId);cmd.Parameters.AddWithValue("id", f.Profile.Id);await cmd.ExecuteNonQueryAsync();
        }
        else await f.Db.SaveChangesAsync();
        var result = await f.Repair.RepairAsync(f.Actor);
        if (state.StartsWith("tenant-", StringComparison.Ordinal)) Assert.False(result.Success);
        else Assert.False((await f.Client.GetAsync<Dictionary<string, object>>(f.Service.Id.ToString(), g => g.Index(f.Index))).Found);
        Assert.Equal(other, await f.RawGetAsync(f.OtherService.Id.ToString()));
        f.Record("lifecycle-negative", new { state, result });
    }

    [Fact]
    public async Task MissingDocumentRebuildsNonzeroStatisticsAndTrustFromActualSql()
    {
        await using var f = await Fixture.CreateAsync();
        var result = await f.Repair.RepairAsync(f.Actor);
        Assert.True(result.Success);
        var doc = await f.GetAsync(f.Service.Id);
        Assert.Equal(5, doc.GetProperty("serviceStats").GetProperty("totalRequests").GetInt32());
        Assert.Equal(3, doc.GetProperty("serviceStats").GetProperty("completedRequests").GetInt32());
        Assert.Equal(3, doc.GetProperty("trust").GetProperty("totalReviews").GetInt32());
        Assert.Equal(1, doc.GetProperty("trust").GetProperty("marketingReviewCount").GetInt32());
        Assert.True(doc.GetProperty("trust").GetProperty("marketingWeightedScore").GetDecimal() > 0);
        f.Record("sql-reconstruction", new { result, doc });
    }

    [Fact]
    public async Task UnavailableSqlAuthorityDoesNotBecomeInventedDefaults()
    {
        await using var f = await Fixture.CreateAsync();
        var invalid = new NpgsqlConnectionStringBuilder(f.Db.Database.GetConnectionString()) { Password = "deliberately-invalid-fixture-password" };
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(invalid.ConnectionString).Options);
        var repair = f.NewRepair(db, f.Client);
        await Assert.ThrowsAsync<PostgresException>(() => repair.RepairAsync(f.Actor));
        Assert.False((await f.Client.GetAsync<Dictionary<string, object>>(f.Service.Id.ToString(), g => g.Index(f.Index))).Found);
    }

    [Fact]
    public async Task MissingVendorGovernanceCannotAuthorizeARepairOrInventMetrics()
    {
        await using var f = await Fixture.CreateAsync(includeVendorGovernance: false);
        var result = await f.Repair.RepairAsync(f.Actor);
        Assert.False(result.Success);
        Assert.True(result.Errors > 0);
        Assert.False((await f.Control.GetAsync<Dictionary<string, object>>(f.Service.Id.ToString(), g => g.Index(f.Index))).Found);
        f.Record("missing-governance-negative", new { result });
    }

    [Fact]
    public async Task VendorSuspensionCannotCommitBetweenSqlAdmissionAndTheSearchWrite()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);
        var attempted = false;
        f.AfterStatisticsRead = async () =>
        {
            attempted = true;
            await using var sql = new NpgsqlConnection(f.Db.Database.GetConnectionString());
            await sql.OpenAsync();
            await using var command = new NpgsqlCommand("SET lock_timeout = '250ms'; UPDATE \"UserGovernanceProfiles\" SET \"Status\"=@status WHERE \"Id\"=@id", sql);
            command.Parameters.AddWithValue("status", (int)UserGovernanceStatus.Suspended);
            command.Parameters.AddWithValue("id", f.Vendor.Id);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            f.Record("governance-write-fenced", new { error.SqlState });
        };
        Assert.True((await f.Repair.RepairAsync(f.Actor)).Success);
        Assert.True(attempted);
        f.Vendor.Status = UserGovernanceStatus.Suspended;
        await f.Db.SaveChangesAsync();
        Assert.True((await f.Repair.RepairAsync(f.Actor)).Success);
        Assert.False((await f.Control.GetAsync<Dictionary<string, object>>(f.Service.Id.ToString(), g => g.Index(f.Index))).Found);
    }

    [Theory]
    [InlineData("statistics")]
    [InlineData("trust")]
    [InlineData("projection")]
    public async Task NewerSearchWritesFenceAnOlderRepair(string change)
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);
        f.AfterGet = async () =>
        {
            if (change == "projection")
            {
                await using var db = f.NewDb();
                var service = await db.Set<Service>().IgnoreQueryFilters().SingleAsync(s => s.Id == f.Service.Id);
                service.Update("newer SQL projection", service.Description, service.Category);
                await db.SaveChangesAsync();
                var consumer = new ServiceDiscoveryProjectionConsumer(f.Control, db, NullLogger<ServiceDiscoveryProjectionConsumer>.Instance, f.Index);
                var envelope = new Mock<ConsumeContext<ServiceUpdatedEvent>>();
                envelope.SetupGet(c => c.Message).Returns(new ServiceUpdatedEvent { ServiceId = service.Id, TenantId = service.TenantId });
                await consumer.Consume(envelope.Object);
            }
            else
            {
                if (change == "statistics") await f.AddRequestsAsync(106, 96);
                var fields = change == "statistics" ? new { serviceStats = new { totalRequests = 111, completedRequests = 99, completionRate = 99m / 111 } } as object
                    : new { trust = new { averageRating = 4.99m, totalReviews = 200, lastEventVersion = 88, lastEventOccurredAtUtc = DateTime.UtcNow } };
                var update = await f.Control.UpdateAsync<Dictionary<string, object>, object>(f.Service.Id.ToString(), u => u.Index(f.Index).Doc(fields));
                Assert.True(update.IsValid, update.DebugInformation);
            }
        };
        var result = await f.Repair.RepairAsync(f.Actor);
        Assert.False(result.Success);
        var after = await f.GetAsync(f.Service.Id);
        if (change == "statistics") Assert.Equal(111, after.GetProperty("serviceStats").GetProperty("totalRequests").GetInt32());
        else if (change == "trust") Assert.Equal(88, after.GetProperty("trust").GetProperty("lastEventVersion").GetInt32());
        else Assert.Equal("newer SQL projection", after.GetProperty("title").GetString());
        Assert.True((await f.Repair.RepairAsync(f.Actor)).Success);
        f.Record("cas-fence", new { change, result, after });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationOrDeletionAfterRepairStartedCannotResurrect(bool deleted)
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);
        f.AfterGet = async () =>
        {
            await using var db = f.NewDb();
            var service = await db.Set<Service>().IgnoreQueryFilters().SingleAsync(s => s.Id == f.Service.Id);
            if (deleted) service.IsDeleted = true; else service.Deactivate();
            await db.SaveChangesAsync();
        };
        Assert.True((await f.Repair.RepairAsync(f.Actor)).Success);
        Assert.False((await f.Client.GetAsync<Dictionary<string, object>>(f.Service.Id.ToString(), g => g.Index(f.Index))).Found);
        Assert.True((await f.Repair.RepairAsync(f.Actor)).Success);
    }

    [Fact]
    public async Task InterruptedRepairPropagatesCancellationAndSafelyRetriesWithoutLosingNewerData()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);
        using var cancel = new CancellationTokenSource();
        f.AfterGet = () => { cancel.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Repair.RepairAsync(f.Actor, cancel.Token));
        await f.AddRequestsAsync(26, 9);
        var update = await f.Control.UpdateAsync<Dictionary<string, object>, object>(f.Service.Id.ToString(), u => u.Index(f.Index)
            .Doc(new { serviceStats = new { totalRequests = 31, completedRequests = 12, completionRate = 12m / 31 } }));
        Assert.True(update.IsValid);
        Assert.True((await f.Repair.RepairAsync(f.Actor)).Success);
        Assert.Equal(31, (await f.GetAsync(f.Service.Id)).GetProperty("serviceStats").GetProperty("totalRequests").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualWriteAndDeleteFailuresCannotReportSuccess(bool deletion)
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);
        if (deletion) { f.Service.Deactivate(); await f.Db.SaveChangesAsync(); }
        Assert.True((await f.Control.Indices.UpdateSettingsAsync(f.Index, s => s.IndexSettings(i => i.Setting("index.blocks.write", true)))).IsValid);
        try
        {
            var result = await f.Repair.RepairAsync(f.Actor);
            Assert.False(result.Success);Assert.True(result.Errors > 0);
            Assert.True((await f.Client.GetAsync<Dictionary<string, object>>(f.Service.Id.ToString(), g => g.Index(f.Index))).Found);
            var http = Assert.IsType<ObjectResult>(await f.Controller(false).ReindexServices(default));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.StatusCode);
            Assert.False(JsonSerializer.SerializeToElement(http.Value).GetProperty("success").GetBoolean());
            Assert.Contains("incomplete", JsonSerializer.SerializeToElement(http.Value).GetProperty("message").GetString(), StringComparison.Ordinal);
            f.Record("actual-server-failure", new { deletion, result });
        }
        finally { Assert.True((await f.Control.Indices.UpdateSettingsAsync(f.Index, s => s.IndexSettings(i => i.Setting("index.blocks.write", false)))).IsValid); }
        Assert.True((await f.Repair.RepairAsync(f.Actor)).Success);
    }

    [Fact]
    public async Task GlobalRecoveryIsNondestructive_AndMappingFailureLeavesExistingIndexIntact()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SeedSearchAsync(f.Service);await f.SeedSearchAsync(f.OtherService);
        var before = await f.GetAsync(f.Service.Id);
        var mapping = await f.MappingAsync();
        Assert.IsType<OkObjectResult>(await f.Controller(true).ReindexServicesGlobally(default));
        var after = await f.GetAsync(f.Service.Id);
        Assert.Equal(before.GetProperty("trust").GetProperty("averageRating").GetDecimal(),
            after.GetProperty("trust").GetProperty("averageRating").GetDecimal());
        Assert.Equal(before.GetProperty("trust").GetProperty("totalReviews").GetInt32(),
            after.GetProperty("trust").GetProperty("totalReviews").GetInt32());
        Assert.Equal(before.GetProperty("trust").GetProperty("lastEventVersion").GetInt32(),
            after.GetProperty("trust").GetProperty("lastEventVersion").GetInt32());
        Assert.Equal(mapping, await f.MappingAsync());
        Assert.DoesNotContain(f.Captures, c => c.Method == "DELETE" && c.Path == "/" + f.Index);
        var missing = await f.NewRepair(f.Db, f.Client, f.Index + "-absent").RepairAsync(new(f.SuperAdmin.Id, null, 0));
        Assert.False(missing.Success);
        Assert.Equal("GLOBAL_SEARCH_INDEX_UNAVAILABLE", missing.Code);
        Assert.True((await f.Control.GetAsync<Dictionary<string, object>>(f.Service.Id.ToString(), g => g.Index(f.Index))).Found);
        f.Record("missing-global-index-negative", new { missing });
        var malformed = f.Index + "-incompatible";
        Assert.True((await f.Control.Indices.CreateAsync(malformed, c => c.Map<Dictionary<string, object>>(m => m
            .Properties(p => p.Nested<object>(n => n.Name("trust")))))).IsValid);
        try
        {
            var result = await f.NewRepair(f.Db, f.Client, malformed).RepairAsync(new(f.SuperAdmin.Id, null, 0));
            Assert.False(result.Success);Assert.Equal("REPAIR_MAPPING_REQUIRES_SEPARATE_REBUILD", result.Code);
            Assert.True((await f.Control.Indices.ExistsAsync(malformed)).Exists);
        }
        finally { Assert.True((await f.Control.Indices.DeleteAsync(malformed)).IsValid); }
    }

    private sealed record Capture(string Method, string Path, int? Status, string? Request, string? Response);
    private sealed record SearchDocument(Guid ServiceId, Guid TenantId, Guid ProviderProfileId, string Title,
        object ServiceStats, object Trust, string Description = "fixture", bool IsActive = true);

    private sealed class Fixture : IAsyncDisposable
    {
        public ApplicationDbContext Db { get; private set; } = null!;
        public OpenSearchClient Client { get; private set; } = null!;
        public OpenSearchClient Control { get; private set; } = null!;
        private ConnectionSettings? ClientSettings { get; set; }
        private ConnectionSettings? ControlSettings { get; set; }
        public ElasticsearchDriftReconciliationJob Repair { get; private set; } = null!;
        public Tenant Tenant { get; private set; } = null!;
        public User Admin { get; private set; } = null!;
        public SuperAdminUser SuperAdmin { get; private set; } = null!;
        public UserGovernanceProfile Vendor { get; private set; } = null!;
        public UserGovernanceProfile SuperAdminGovernance { get; private set; } = null!;
        public MarketplaceProfile Profile { get; private set; } = null!;
        public Service Service { get; private set; } = null!;
        public Service OtherService { get; private set; } = null!;
        public string Index { get; } = "app17c-repair-" + Guid.NewGuid().ToString("N");
        private string Schema { get; } = "app17c_" + Guid.NewGuid().ToString("N");
        public ConcurrentQueue<Capture> Captures { get; } = new();
        private SqlTrace Trace { get; } = new();
        public Func<Task>? AfterGet { get; set; }
        public Func<Task>? AfterStatisticsRead { get => Trace.AfterStatisticsRead; set => Trace.AfterStatisticsRead = value; }
        private object SqlTrust { get; set; } = null!;
        public MarketplaceSearchRepairActor Actor => new(Admin.Id, Tenant.Id, null);
        private string DirectoryPath => Path.Combine(Environment.GetEnvironmentVariable("APP17C_EVIDENCE_DIR")!, Index);
        public static async Task<Fixture> CreateAsync(bool includeVendorGovernance = true, bool includeGlobalGovernance = true)
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.DirectoryPath);
            var connection = Environment.GetEnvironmentVariable("APP17C_POSTGRES") ?? throw new InvalidOperationException("Disposable PostgreSQL18 fixture required");
            var cs = new NpgsqlConnectionStringBuilder(connection);
            if (cs.Host != "app17c-pg" || cs.Database != "app17c_fixture" || cs.Username != "app17c_fixture") throw new InvalidOperationException("Refusing nonfixture SQL");
            cs.SearchPath = f.Schema;cs.Pooling = false;
            f.Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(cs.ConnectionString).AddInterceptors(f.Trace).Options);
            await f.Db.Database.OpenConnectionAsync();
            await using (var command = f.Db.Database.GetDbConnection().CreateCommand())
            {
                Assert.StartsWith("18.", f.Db.Database.GetDbConnection().ServerVersion);
                command.CommandText = "CREATE SCHEMA \"" + f.Schema + "\"";await command.ExecuteNonQueryAsync();
                command.CommandText = f.Db.Database.GenerateCreateScript();command.CommandTimeout = 60;await command.ExecuteNonQueryAsync();
            }
            await f.Db.Database.CloseConnectionAsync();
            var ca = Environment.GetEnvironmentVariable("APP17C_CA") ?? throw new InvalidOperationException("Fixture CA required");
            var pw = Environment.GetEnvironmentVariable("APP17C_OS_PASSWORD") ?? throw new InvalidOperationException("Fixture authentication required");
            var policy = new ServerCertificatePolicy(ca);
            ConnectionSettings Settings() => new ConnectionSettings(new Uri("https://opensearch:9201"))
                .BasicAuthentication("app17_admin", pw).ServerCertificateValidationCallback(policy.Validate)
                .RequestTimeout(TimeSpan.FromSeconds(10)).DisableDirectStreaming().ThrowExceptions(false).UseSystemTextJson(false)
                .OnRequestCompleted(d =>
                {
                    f.Captures.Enqueue(new(d.HttpMethod.ToString(), d.Uri.PathAndQuery, d.HttpStatusCode,
                        d.RequestBodyInBytes == null ? null : Encoding.UTF8.GetString(d.RequestBodyInBytes),
                        d.ResponseBodyInBytes == null ? null : Encoding.UTF8.GetString(d.ResponseBodyInBytes)));
                });
            f.ControlSettings = Settings();
            f.Control = new OpenSearchClient(f.ControlSettings);
            f.ClientSettings = Settings().OnRequestCompleted(d =>
            {
                f.Captures.Enqueue(new(d.HttpMethod.ToString(), d.Uri.PathAndQuery, d.HttpStatusCode,
                    d.RequestBodyInBytes == null ? null : Encoding.UTF8.GetString(d.RequestBodyInBytes),
                    d.ResponseBodyInBytes == null ? null : Encoding.UTF8.GetString(d.ResponseBodyInBytes)));
                if (d.HttpMethod == OpenSearch.Net.HttpMethod.GET && d.Uri.AbsolutePath == "/" + f.Index + "/_doc/" + f.Service?.Id && f.AfterGet != null)
                {
                    var action = f.AfterGet;f.AfterGet = null;action().GetAwaiter().GetResult();
                }
            });
            f.Client = new OpenSearchClient(f.ClientSettings);
            try
            {
                var info = await f.Control.RootNodeInfoAsync();Assert.True(info.IsValid, info.DebugInformation);Assert.Equal("3.9.0", info.Version.Number);
            }
            catch
            {
                // A failed handshake must not leave pooled sockets behind for the next case.
                f.DisposeSettings();
                throw;
            }
            var create = await f.Control.Indices.CreateAsync(MarketplaceServiceIndexConfiguration.GetIndexDescriptor(f.Index));
            Assert.True(create.IsValid && create.Acknowledged && create.ShardsAcknowledged, create.DebugInformation);
            f.Tenant = new Tenant { Id = Guid.NewGuid(), Name = "APP17C tenant A", Currency = "EGP", IsActive = true, Status = TenantStatus.Active };
            var otherTenant = new Tenant { Id = Guid.NewGuid(), Name = "APP17C tenant B", Currency = "EGP", IsActive = true, Status = TenantStatus.Active };
            f.Admin = new User { Id = Guid.NewGuid(), TenantId = f.Tenant.Id, Role = UserRole.Admin, Username = Guid.NewGuid().ToString("N"), Email = Guid.NewGuid().ToString("N") + "@fixture.invalid", PasswordHash = "fixture-only" };
            f.SuperAdmin = SuperAdminUser.Create("super-" + Guid.NewGuid().ToString("N") + "@fixture.invalid", "fixture-only", false, DateTime.UtcNow);
            f.Vendor = new UserGovernanceProfile { Id = Guid.NewGuid(), Role = UserGovernanceRole.Vendor, SubjectId = f.Tenant.Id, Status = UserGovernanceStatus.Active, CreatedAtUtc = DateTime.UtcNow };
            f.SuperAdminGovernance = new UserGovernanceProfile { Id = Guid.NewGuid(), Role = UserGovernanceRole.Admin, SubjectId = f.SuperAdmin.Id, Status = UserGovernanceStatus.Active, CreatedAtUtc = DateTime.UtcNow };
            var otherVendor = new UserGovernanceProfile { Id = Guid.NewGuid(), Role = UserGovernanceRole.Vendor, SubjectId = otherTenant.Id, Status = UserGovernanceStatus.Active, CreatedAtUtc = DateTime.UtcNow };
            f.Profile = MarketplaceProfile.Create(f.Tenant.Id, f.Admin.Id, "APP17C Provider A");
            var otherProfile = MarketplaceProfile.Create(otherTenant.Id, Guid.NewGuid(), "APP17C Provider B");
            f.Service = Service.Create(f.Tenant.Id, f.Profile.Id, "SQL service A", "SQL description", "Marketing", 50m, currency: "EGP");
            f.OtherService = Service.Create(otherTenant.Id, otherProfile.Id, "SQL service B", "SQL description", "General");
            f.Db.AddRange(f.Tenant, otherTenant, f.Admin, f.SuperAdmin, otherVendor, f.Profile, otherProfile, f.Service, f.OtherService);
            // Absent-authority cases model an incomplete/legacy store at seed time.
            // Do not bypass the application's immutable-governance deletion guard.
            if (includeVendorGovernance) f.Db.Add(f.Vendor);
            if (includeGlobalGovernance) f.Db.Add(f.SuperAdminGovernance);
            for (var i = 0; i < 5; i++)
            {
                var buyer = Guid.NewGuid();
                var request = ServiceRequest.Create(f.Tenant.Id, f.Service.Id, buyer, f.Admin.Id, f.Profile.Id, f.Service.Title, "Marketing", 50m, null, "EGP", null);
                if (i < 3) { request.Accept(); request.MarkCompletedByProvider(); request.ConfirmCompletedByBuyer(); }
                f.Db.Add(request);
                if (i < 3)
                {
                    var review = ServiceReview.Create(f.Tenant.Id, request.Id, f.Service.Id, buyer, f.Admin.Id, i == 0 ? 4 : 5);
                    f.Db.Add(review);
                    if (i == 0) f.Db.Add(MarketingReviewEvidence.CreatePosVerified(f.Tenant.Id, review.Id, "fixture campaign", MarketingPlatform.Google, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1), 7, 150m));
                }
            }
            await f.Db.SaveChangesAsync();
            var trust = (await new ProviderTrustService(f.Db).GetTrustAggregateAsync(f.Profile.Id))!;
            f.SqlTrust = new { averageRating = trust.AverageRating, totalReviews = trust.TotalReviews, trustBadge = trust.ComputeBadge().ToString(),
                marketingReviewCount = trust.MarketingReviewCount, posVerifiedCount = trust.PosVerifiedCount, marketingWeightedScore = trust.MarketingWeightedScore,
                lastEventId = Guid.Parse("c17c0000-0000-0000-0000-000000000001"), lastEventOccurredAtUtc = "2026-10-30T12:00:00.0000000Z", lastEventVersion = 12 };
            f.Repair = f.NewRepair(f.Db, f.Client);
            f.Record("owned-identities", new { f.Index, f.Schema, tenant = f.Tenant.Id, otherTenant = otherTenant.Id, service = f.Service.Id, otherService = f.OtherService.Id });
            return f;
        }
        public ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(Db.Database.GetConnectionString()).Options);
        public ElasticsearchDriftReconciliationJob NewRepair(ApplicationDbContext db, IOpenSearchClient client, string? index = null) =>
            new(db, client, NullLogger<ElasticsearchDriftReconciliationJob>.Instance, new MarketplaceTelemetry(), new ProviderTrustService(db), index ?? Index);
        public MarketplaceController Controller(bool global)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, (global ? SuperAdmin.Id : Admin.Id).ToString()), new(ClaimTypes.Role, global ? "SuperAdmin" : "Admin") };
            if (global) { claims.Add(new("auth_type", "super_admin")); claims.Add(new("token_version", "0")); }
            else claims.Add(new("tenant_id", Tenant.Id.ToString()));
            return new MarketplaceController(Mock.Of<IMarketplaceSearchService>(), Mock.Of<IMarketplaceProfileService>(), Mock.Of<IServiceCatalogService>(),
                Mock.Of<IServiceRequestService>(), Mock.Of<IServiceReviewService>(), Mock.Of<IChatService>(), new ProviderTrustService(Db),
                Mock.Of<IFileValidationService>(), Mock.Of<IR2StorageService>(), Db, Client, Mock.Of<IWebHostEnvironment>(), new MarketplaceTelemetry(),
                NullLogger<MarketplaceController>.Instance, Repair)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture")) } } };
        }
        public SearchDocument Document(Service service) => new(service.Id, service.TenantId, service.ProfileId, "stale projected title",
            new { totalRequests = 5, completedRequests = 3, completionRate = 0.6m }, SqlTrust);
        public async Task AddRequestsAsync(int count, int completed)
        {
            await using var db = NewDb();
            for (var i = 0; i < count; i++)
            {
                var r = ServiceRequest.Create(Tenant.Id, Service.Id, Guid.NewGuid(), Admin.Id, Profile.Id, Service.Title, "Marketing", 50m, null, "EGP", null);
                if (i < completed) { r.Accept(); r.MarkCompletedByProvider(); r.ConfirmCompletedByBuyer(); }
                db.Add(r);
            }
            await db.SaveChangesAsync();
        }
        public Task SeedSearchAsync(Service service) => PutAsync(service.Id.ToString(), Document(service));
        public async Task PutAsync(string id, SearchDocument document)
        {
            var response = await Control.IndexAsync(document, i => i.Index(Index).Id(id).Refresh(Refresh.True));Assert.True(response.IsValid, response.DebugInformation);
        }
        public async Task<string> RawGetAsync(string id)
        {
            var r = await Control.LowLevel.GetAsync<StringResponse>(Index, id);Assert.True(r.Success, r.Body);return r.Body;
        }
        public async Task<JsonElement> GetAsync(Guid id)
        {
            using var doc = JsonDocument.Parse(await RawGetAsync(id.ToString()));return doc.RootElement.GetProperty("_source").Clone();
        }
        public async Task<string> MappingAsync()
        {
            var r = await Control.LowLevel.Indices.GetMappingAsync<StringResponse>(Index);Assert.True(r.Success, r.Body);return r.Body;
        }
        public void Record(string name, object value) => File.WriteAllText(Path.Combine(DirectoryPath, name + "-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(value));
        // The fixture relay admits a bounded number of concurrent connections and destroys
        // sockets beyond that during the TLS handshake, so every pooled transport this
        // fixture opened is released when the case ends.
        private void DisposeSettings()
        {
            (ClientSettings as IDisposable)?.Dispose();ClientSettings = null;
            (ControlSettings as IDisposable)?.Dispose();ControlSettings = null;
        }
        public async ValueTask DisposeAsync()
        {
            AfterGet = null;
            try
            {
                Assert.StartsWith("app17c-repair-", Index);
                var response = await Control.Indices.DeleteAsync(Index);Assert.True(response.IsValid, response.DebugInformation);
                await Db.Database.OpenConnectionAsync();
                await using var cmd = Db.Database.GetDbConnection().CreateCommand();
                Assert.Matches("^app17c_[0-9a-f]{32}$", Schema);cmd.CommandText = "DROP SCHEMA \"" + Schema + "\" CASCADE";cmd.CommandTimeout = 60;await cmd.ExecuteNonQueryAsync();
                Record("cleanup", new { success = true, Index, Schema });
            }
            finally
            {
                Record("opensearch-captures", Captures.ToArray());Record("sql-operations", Trace.Queries.ToArray());
                await Db.DisposeAsync();
                DisposeSettings();
            }
        }
    }

    private sealed class SqlTrace : DbCommandInterceptor
    {
        public ConcurrentQueue<object> Queries { get; } = new();
        public Func<Task>? AfterStatisticsRead { get; set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (AfterStatisticsRead != null && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("\"ServiceRequests\"", StringComparison.Ordinal))
            {
                var action = AfterStatisticsRead;
                AfterStatisticsRead = null;
                await action();
            }
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Queries.Enqueue(new { sql = command.CommandText, parameters = command.Parameters.Cast<DbParameter>().Select(p => new { p.ParameterName, p.Value }).ToArray() });
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

[CollectionDefinition("Marketplace repair authority", DisableParallelization = true)]
public sealed class MarketplaceRepairAuthorityCollection;
