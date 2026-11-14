using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Journal;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

public sealed class MarketplaceSourceJournalContractTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("alias")]
    [InlineData("multiple")]
    [InlineData("uuid")]
    [InlineData("cluster")]
    [InlineData("provided-name")]
    [InlineData("attached-alias")]
    [InlineData("journal")]
    [InlineData("generation")]
    [InlineData("schema")]
    [InlineData("revision-type")]
    [InlineData("envelope-dynamic")]
    [InlineData("source-disabled")]
    [InlineData("shards")]
    [InlineData("pipeline")]
    [InlineData("write-block")]
    [InlineData("analysis-drift")]
    [InlineData("mapping-drift")]
    [InlineData("fingerprint")]
    public void PhysicalGenerationAdmissionPinsIndependentClusterExactPhysicalMappingSettingsAndEnvelope(string poison)
    {
        var journal = Guid.NewGuid(); var generation = Guid.NewGuid();
        var expected = new MarketplacePhysicalAdmission.Expected(journal, generation, "fixture-cluster", "fixture-physical", new string('a', 64));
        var mapping = new JsonObject {
            ["_meta"] = new JsonObject { ["journal"] = journal.ToString("D"), ["generation"] = generation.ToString("D"), ["schema"] = "marketplace-generation-v1" },
            ["properties"] = new JsonObject {
                ["serviceId"] = new JsonObject { ["type"] = "keyword" }, ["isActive"] = new JsonObject { ["type"] = "boolean" },
                ["__marketplace"] = new JsonObject { ["dynamic"] = "strict", ["properties"] = new JsonObject {
                    ["journal"] = new JsonObject { ["type"] = "keyword" }, ["generation"] = new JsonObject { ["type"] = "keyword" },
                    ["digest"] = new JsonObject { ["type"] = "keyword" }, ["revision"] = new JsonObject { ["type"] = "long" },
                    ["epoch"] = new JsonObject { ["type"] = "long" }, ["deleted"] = new JsonObject { ["type"] = "boolean" }
                } }
            }
        };
        var settings = new JsonObject { ["uuid"] = expected.PhysicalUuid, ["provided_name"] = expected.PhysicalName,
            ["number_of_shards"] = "1", ["number_of_replicas"] = "0", ["refresh_interval"] = "1s" };
        using (var m = JsonDocument.Parse(mapping.ToJsonString()))
        using (var s = JsonDocument.Parse(settings.ToJsonString()))
            expected = expected with { Fingerprint = MarketplacePhysicalAdmission.Fingerprint(m.RootElement, s.RootElement) };
        var index = new JsonObject { ["aliases"] = new JsonObject(), ["settings"] = new JsonObject { ["index"] = settings }, ["mappings"] = mapping };
        var root = new JsonObject { [expected.PhysicalName] = index };
        var cluster = "{\"cluster_uuid\":\"fixture-cluster\"}";
        switch (poison)
        {
            case "alias": root.Remove(expected.PhysicalName); root["other-physical"] = index; break;
            case "multiple": root["other-physical"] = index.DeepClone(); break;
            case "uuid": settings["uuid"] = "recreated-physical"; break;
            case "cluster": cluster = "{\"cluster_uuid\":\"other-cluster\"}"; break;
            case "provided-name": settings["provided_name"] = "other-name"; break;
            case "attached-alias": index["aliases"]!["reader"] = new JsonObject(); break;
            case "journal": mapping["_meta"]!["journal"] = Guid.NewGuid().ToString("D"); break;
            case "generation": mapping["_meta"]!["generation"] = Guid.NewGuid().ToString("D"); break;
            case "schema": mapping["_meta"]!["schema"] = "future-schema"; break;
            case "revision-type": mapping["properties"]!["__marketplace"]!["properties"]!["revision"]!["type"] = "double"; break;
            case "envelope-dynamic": mapping["properties"]!["__marketplace"]!["dynamic"] = "true"; break;
            case "source-disabled": mapping["_source"] = new JsonObject { ["enabled"] = false }; break;
            case "shards": settings["number_of_shards"] = "0"; break;
            case "pipeline": settings["final_pipeline"] = "unreviewed-transform"; break;
            case "write-block": settings["blocks"] = new JsonObject { ["write"] = "true" }; break;
            case "analysis-drift": settings["analysis"] = new JsonObject { ["analyzer"] = "changed" }; break;
            case "mapping-drift": mapping["properties"]!["poison"] = new JsonObject { ["type"] = "text" }; break;
            case "fingerprint": expected = expected with { Fingerprint = new string('b', 64) }; break;
        }
        if (poison == "valid")
        {
            var observed = MarketplacePhysicalAdmission.InspectResponses(expected, cluster, root.ToJsonString());
            Assert.Equal(expected.PhysicalName, observed.PhysicalName); Assert.Equal(expected.Fingerprint, observed.Fingerprint);
            // JSON property order is not a change to mapping/settings identity.
            using var m = JsonDocument.Parse(mapping.ToJsonString());
            using var reversed = JsonDocument.Parse(new JsonObject(settings.Reverse().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone()))).ToJsonString());
            Assert.Equal(expected.Fingerprint, MarketplacePhysicalAdmission.Fingerprint(m.RootElement, reversed.RootElement));
        }
        else Assert.Throws<InvalidDataException>(() => MarketplacePhysicalAdmission.InspectResponses(expected, cluster, root.ToJsonString()));
    }

    [Fact]
    public void PhysicalObservationRejectsDuplicateJsonPropertiesRatherThanAcceptingLastValue()
    {
        var expected = new MarketplacePhysicalAdmission.Expected(Guid.NewGuid(), Guid.NewGuid(), "cluster", "physical", new string('a', 64));
        Assert.Throws<InvalidDataException>(() => MarketplacePhysicalAdmission.InspectResponses(expected,
            "{\"cluster_uuid\":\"other\",\"cluster_uuid\":\"cluster\"}", "{}"));
    }

    [Fact]
    public void ExternalWholeDocumentEnvelopeBindsExactPayloadAndInt64RevisionAndNeverUsesPhysicalDelete()
    {
        var service = Guid.NewGuid(); var journal = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { serviceId = service, title = "Sealed output", isActive = true });
        var ticket = new ExternalWriteTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), service,
            long.MaxValue - 10, "fixture", new string('a', 64), MarketplaceFrozenOutput.RowDigest(service, payload), 2);
        using var request = JsonDocument.Parse(MarketplaceVersionedDocument.BuildUpdate(journal, ticket, payload));
        var parameters = request.RootElement.GetProperty("script").GetProperty("params");
        Assert.Equal(long.MaxValue - 10, parameters.GetProperty("revision").GetInt64());
        Assert.Equal(service, parameters.GetProperty("document").GetProperty("serviceId").GetGuid());
        Assert.False(parameters.GetProperty("deleted").GetBoolean());
        Assert.Throws<InvalidDataException>(() => MarketplaceVersionedDocument.BuildUpdate(journal, ticket, payload + " "));
        Assert.Throws<InvalidDataException>(() => MarketplaceVersionedDocument.BuildUpdate(journal, ticket, null));
        var tombstone = ticket with { Digest = MarketplaceFrozenOutput.RowDigest(service, null) };
        using var deletion = JsonDocument.Parse(MarketplaceVersionedDocument.BuildUpdate(journal, tombstone, null));
        var deleted = deletion.RootElement.GetProperty("script").GetProperty("params");
        Assert.True(deleted.GetProperty("deleted").GetBoolean());
        Assert.False(deleted.GetProperty("document").GetProperty("isActive").GetBoolean());
        Assert.Equal(2, deleted.GetProperty("document").EnumerateObject().Count());
        foreach (var poisoned in new[] {
            JsonSerializer.Serialize(new { serviceId = Guid.NewGuid() }),
            JsonSerializer.Serialize(new { serviceId = service, __marketplace = new { revision = 100 } }) })
            Assert.Throws<InvalidDataException>(() => MarketplaceVersionedDocument.BuildUpdate(journal,
                ticket with { Digest = MarketplaceFrozenOutput.RowDigest(service, poisoned) }, poisoned));
        // Script execution/atomicity is separately proven only by the real OpenSearch test.
    }

    [Fact]
    public void AuthorityTablesMatchActualRelationalModel()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=unused").Options);
        Type[] authority = [typeof(Service),typeof(MarketplaceProfile),typeof(ServiceRequest),typeof(ServiceReview),
            typeof(MarketingReviewEvidence),typeof(ProviderTrustAggregate),typeof(Tenant),typeof(UserGovernanceProfile)];
        Assert.Equal(MarketplaceSourceJournal.SourceTables.Order(),
            authority.Select(t => db.Model.FindEntityType(t)!.GetTableName()!).Order());
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("missing-new")]
    [InlineData("missing-old")]
    [InlineData("table")]
    [InlineData("revision")]
    [InlineData("operation")]
    [InlineData("lineage")]
    public void InvalidEntriesFailClosed(string defect)
    {
        var journal = Guid.NewGuid(); var id = Guid.NewGuid();
        var row = JsonSerializer.Serialize(new { Id=id, TenantId=Guid.NewGuid(), ProfileId=Guid.NewGuid() });
        var entry = new SourceChange(journal,new(1,1),"Services",id,id,"UPDATE",row,row);
        entry = defect switch
        {
            "identity" => entry with { SourceId=Guid.NewGuid() },
            "missing-new" => entry with { NewRow=null },
            "missing-old" => entry with { OldRow=null },
            "table" => entry with { Table="Users" },
            "revision" => entry with { Position=new(0,1) },
            "operation" => entry with { Operation="TRUNCATE" },
            _ => entry with { JournalId=Guid.NewGuid() }
        };
        Assert.Throws<InvalidDataException>(() => entry.Validate(journal));
    }

    [Fact]
    public void DuplicateOutOfOrderAndStaleRowsCannotResurrectTombstones()
    {
        var journal=Guid.NewGuid(); var id=Guid.NewGuid(); var tenant=Guid.NewGuid();
        var mirror=new JournalMirror(journal);
        var row=JsonSerializer.Serialize(new { Id=id,TenantId=tenant,ProfileId=Guid.NewGuid() });
        var created=new SourceChange(journal,new(1,1),"Services",id,null,"INSERT",null,row);
        var deleted=new SourceChange(journal,new(2,2),"Services",id,id,"DELETE",row,null);
        mirror.Apply(deleted); mirror.Apply(created); mirror.Apply(deleted);
        Assert.Empty(mirror.Rows);
        Assert.Equal(deleted.Position,mirror.Versions[("Services",id)]);
        Assert.Throws<InvalidDataException>(() => mirror.Apply(created with { JournalId=Guid.NewGuid() }));
    }

    [Fact]
    public void RelationshipReassignmentRetainsBothTenantIdentities()
    {
        var journal=Guid.NewGuid(); var id=Guid.NewGuid();var oldTenant=Guid.NewGuid();var newTenant=Guid.NewGuid();
        var row=new SourceChange(journal,new(2,2),"Services",id,id,"UPDATE",
            JsonSerializer.Serialize(new { Id=id,TenantId=oldTenant,ProfileId=Guid.NewGuid() }),
            JsonSerializer.Serialize(new { Id=id,TenantId=newTenant,ProfileId=Guid.NewGuid() }));
        row.Validate(journal);
        Assert.Contains(oldTenant.ToString(),row.OldRow!);
        Assert.Contains(newTenant.ToString(),row.NewRow!);
        var mirror=new JournalMirror(journal); mirror.Apply(row);
        Assert.DoesNotContain(oldTenant.ToString(),mirror.Rows[("Services",id)]);
    }
}

// Disposable validation harness only. Durable effects/checkpoint testing is performed in PostgreSQL.
internal sealed class JournalMirror(Guid journal, long snapshotFloor = 0)
{
    internal Dictionary<(string,Guid),string> Rows { get; } = [];
    internal Dictionary<(string,Guid),JournalPosition> Versions { get; } = [];
    internal void Apply(SourceChange change)
    {
        change.Validate(journal);
        if (change.Position.Revision <= snapshotFloor) return;
        if (change.OldSourceId is Guid old && old != change.SourceId) ApplyKey((change.Table,old),null,change.Position);
        ApplyKey((change.Table,change.SourceId),change.NewRow,change.Position);
    }
    private void ApplyKey((string,Guid) key,string? row,JournalPosition position)
    {
        if (Versions.TryGetValue(key,out var previous) && previous.CompareTo(position)>=0) return;
        Versions[key]=position;
        if (row==null) Rows.Remove(key); else Rows[key]=row;
    }
}
