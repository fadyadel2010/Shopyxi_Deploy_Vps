using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

public sealed class MarketplaceGenerationPhysicalRetirementTests
{
    [Fact]
    public async Task ExactDeletionRequiresIdentityAcknowledgementAndObservedAbsenceAndIsIdempotent()
    {
        var provider = new Provider(); using var transport = Transport(provider);
        await MarketplaceGenerationPhysicalRetirement.DeleteAsync(provider.Expected, transport, default);
        await MarketplaceGenerationPhysicalRetirement.DeleteAsync(provider.Expected, transport, default);
        Assert.Equal(1, provider.Deletes); Assert.True(provider.Absent);
        Assert.All(provider.DeletedPaths, path => Assert.Equal("/" + provider.Expected.PhysicalName, path));
    }

    [Theory]
    [InlineData("cluster")]
    [InlineData("uuid")]
    [InlineData("fingerprint")]
    [InlineData("alias")]
    public async Task ChangedIdentityNeverReachesPhysicalDelete(string defect)
    {
        var provider = new Provider { Defect = defect }; using var transport = Transport(provider);
        await Assert.ThrowsAsync<InvalidDataException>(() => MarketplaceGenerationPhysicalRetirement.DeleteAsync(provider.Expected, transport, default));
        Assert.Equal(0, provider.Deletes);
    }

    [Theory]
    [InlineData("negative-ack")]
    [InlineData("duplicate-ack")]
    [InlineData("still-present")]
    public async Task MissingDeletionProofCannotBecomeADurableSuccess(string defect)
    {
        var provider = new Provider { Defect = defect }; using var transport = Transport(provider);
        await Assert.ThrowsAnyAsync<Exception>(() => MarketplaceGenerationPhysicalRetirement.DeleteAsync(provider.Expected, transport, default));
        Assert.Equal(1, provider.Deletes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EveryPhysicalCrashBoundaryCanReconcileByExactIdentityOrAbsence(int stage)
    {
        var provider = new Provider(); using var transport = Transport(provider);
        void Fail(MarketplaceGenerationPhysicalRetirement.Boundary boundary)
        { if ((int)boundary == stage) throw new InjectedFailure(); }
        await Assert.ThrowsAsync<InjectedFailure>(() => MarketplaceGenerationPhysicalRetirement.DeleteAsync(provider.Expected, transport, default, Fail));
        await MarketplaceGenerationPhysicalRetirement.DeleteAsync(provider.Expected, transport, default);
        Assert.Equal(1, provider.Deletes); Assert.True(provider.Absent);
    }

    [Fact]
    public async Task TimeoutAfterDeleteDoesNotSucceedButReconnectProvesAbsenceWithoutASecondDelete()
    {
        var provider = new Provider { Defect = "lost-reply" }; using var transport = Transport(provider);
        await Assert.ThrowsAsync<HttpRequestException>(() => MarketplaceGenerationPhysicalRetirement.DeleteAsync(provider.Expected, transport, default));
        provider.Defect = null;
        await MarketplaceGenerationPhysicalRetirement.DeleteAsync(provider.Expected, transport, default);
        Assert.Equal(1, provider.Deletes);
    }

    private sealed class InjectedFailure : Exception { }
    private static MarketplaceGenerationTransport Transport(Provider handler) => new(new Uri("https://fictional-retirement.invalid/"), "fictional", "not-real", handler);
    private sealed class Provider : HttpMessageHandler
    {
        internal MarketplacePhysicalAdmission.Expected Expected;
        private readonly JsonObject metadata;
        internal string? Defect;
        internal int Deletes;
        internal bool Absent;
        internal List<string> DeletedPaths = [];
        internal Provider()
        {
            var journal = Guid.NewGuid(); var generation = Guid.NewGuid();
            Expected = new(journal, generation, "fictional-cluster", "fictional-index", new string('a', 64));
            var mapping = JsonNode.Parse("""
                {"_meta":{},"properties":{"serviceId":{"type":"keyword"},"isActive":{"type":"boolean"},
                "__marketplace":{"dynamic":"strict","properties":{"journal":{"type":"keyword"},"generation":{"type":"keyword"},
                "digest":{"type":"keyword"},"revision":{"type":"long"},"epoch":{"type":"long"},"deleted":{"type":"boolean"}}}}}
                """)!.AsObject();
            mapping["_meta"] = JsonSerializer.SerializeToNode(new { journal = journal.ToString("D"), generation = generation.ToString("D"), schema = "marketplace-generation-v1" });
            var settings = JsonSerializer.SerializeToElement(new { uuid = Expected.PhysicalUuid, provided_name = Expected.PhysicalName, number_of_shards = "1", number_of_replicas = "0" });
            Expected = Expected with { Fingerprint = MarketplacePhysicalAdmission.Fingerprint(JsonSerializer.SerializeToElement(mapping), settings) };
            metadata = new JsonObject { [Expected.PhysicalName] = JsonSerializer.SerializeToNode(new { aliases = new { }, settings = new { index = settings }, mappings = mapping }) };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/")
                return Reply(JsonSerializer.Serialize(new { cluster_uuid = Defect == "cluster" ? "other" : Expected.ClusterUuid }));
            Assert.Equal("/" + Expected.PhysicalName, request.RequestUri.AbsolutePath);
            if (request.Method == HttpMethod.Delete)
            {
                Deletes++; DeletedPaths.Add(request.RequestUri.AbsolutePath); Absent = Defect != "still-present";
                if (Defect == "lost-reply") throw new HttpRequestException("fictional lost response");
                return Reply(Defect == "negative-ack" ? "{\"acknowledged\":false}" : Defect == "duplicate-ack" ?
                    "{\"acknowledged\":false,\"acknowledged\":true}" : "{\"acknowledged\":true}");
            }
            if (Absent) return Reply("{\"error\":\"index_not_found_exception\",\"status\":404}", HttpStatusCode.NotFound);
            var body = metadata.DeepClone(); var index = body[Expected.PhysicalName]!;
            if (Defect == "uuid") index["settings"]!["index"]!["uuid"] = "other-index";
            if (Defect == "fingerprint") index["settings"]!["index"]!["number_of_replicas"] = "1";
            if (Defect == "alias") index["aliases"]!["products"] = new JsonObject();
            return Reply(body.ToJsonString());
        }
        private static Task<HttpResponseMessage> Reply(string body, HttpStatusCode code = HttpStatusCode.OK) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
