using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

public sealed partial class MarketplaceSourceJournalPostgresTests
{
    // Real PostgreSQL + recording HTTP collaborator. These cases are deliberately
    // NOT counted as real OpenSearch/cross-provider execution or script validation.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SqlDeliveryFailureBoundariesRetainObligationsAndCannotAdvanceRefreshedAcknowledgement(int stage)
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var lease = (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromMinutes(2)))!;
        var build = await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db, lease, Guid.NewGuid());
        var physical = RecordingGenerationProvider.Physical(lease.JournalId, build.GenerationId);
        var expected = physical.Expected;
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db, lease, build.GenerationId, expected.PhysicalUuid, expected.Fingerprint);
        var checkpoint = await MarketplaceGenerationRegistry.SnapshotAsync(f.Db, lease, build.GenerationId);
        var frozen = await MarketplaceGenerationInputExport.FreezeAsync(f.Db, lease, build.GenerationId, Guid.NewGuid(), checkpoint);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        using var transport = new MarketplaceGenerationTransport(new Uri("https://fictional-generation.invalid/"),
            "fictional-dispatcher", "not-a-real-credential", physical);
        await MarketplaceGenerationDeliveryProtocol.BindProviderAsync(f.Db, lease, expected, transport);
        var ids = new List<Guid>();
        await using (var read = new NpgsqlCommand("SELECT service_id FROM marketplace_generation.output_rows WHERE run_id=@run ORDER BY service_id", f.Db))
        {
            read.Parameters.AddWithValue("run", run.RunId);
            await using var rows = await read.ExecuteReaderAsync();
            while (await rows.ReadAsync()) ids.Add(rows.GetGuid(0));
        }
        Assert.Equal(2, ids.Count);
        void Fail(MarketplaceGenerationDeliveryProtocol.DeliveryBoundary boundary)
        {
            if ((int)boundary == stage) throw new InjectedLifecycleFailure();
        }
        for (var ordinal = 0; ordinal < ids.Count; ordinal++)
        {
            var ticket = await MarketplaceExternalWriteBarrier.AdmitAsync(f.Db, lease, run.RunId, ids[ordinal], Guid.NewGuid());
            if (ordinal == 0 && stage is 0 or 1)
            {
                await Assert.ThrowsAsync<InjectedLifecycleFailure>(() => MarketplaceGenerationDeliveryProtocol.DeliverAsync(
                    f.Db, lease, ticket, expected, transport, injectFailure: Fail));
                Assert.Equal(stage == 0 ? 0 : 1, physical.Writes);
                Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.refresh_observations"));
                if (stage == 0)
                {
                    Assert.Equal("not-sent", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.write_intents"));
                    ticket = await MarketplaceExternalWriteBarrier.AdmitAsync(f.Db, lease, run.RunId, ids[ordinal], Guid.NewGuid());
                    await MarketplaceGenerationDeliveryProtocol.DeliverAsync(f.Db, lease, ticket, expected, transport);
                }
                else
                {
                    Assert.Equal("outstanding", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.write_intents"));
                    await MarketplaceGenerationRedelivery.RetryAsync(f.Db, lease, ticket, expected, transport, Guid.NewGuid());
                    Assert.Equal(2L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.delivery_attempts"));
                }
            }
            else await MarketplaceGenerationDeliveryProtocol.DeliverAsync(f.Db, lease, ticket, expected, transport);
        }
        if (stage == 1)
        {
            // The retry's definite response does not cancel the original request.
            // A crash-lost original reply remains a separate durable drain blocker.
            await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationDeliveryProtocol.ObserveRefreshedRunAsync(
                f.Db, lease, run.RunId, expected, transport));
            Assert.False(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, lease, build.GenerationId));
            Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.refresh_observations"));
            Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.delivery_attempts d WHERE NOT EXISTS(SELECT 1 FROM marketplace_generation.delivery_attempt_outcomes a WHERE a.attempt_id=d.attempt_id)"));
            return;
        }
        if (stage >= 2)
        {
            await Assert.ThrowsAsync<InjectedLifecycleFailure>(() => MarketplaceGenerationDeliveryProtocol.ObserveRefreshedRunAsync(
                f.Db, lease, run.RunId, expected, transport, injectFailure: Fail));
            Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.refresh_observations"));
            Assert.Equal(stage == 2 ? 0 : 1, physical.Refreshes);
        }
        await MarketplaceGenerationDeliveryProtocol.ObserveRefreshedRunAsync(f.Db, lease, run.RunId, expected, transport);
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.refresh_observations"));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.write_intents WHERE state='outstanding'"));
        var evidence = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Owned evidence root required.");
        await File.WriteAllTextAsync(Path.Combine(evidence, "generation-recording-delivery-boundary-" + stage + ".json"),
            JsonSerializer.Serialize(new { stage, expected, physicalName = expected.PhysicalName,
                database = f.Db.Database, host = f.Db.Host, run = run.RunId,
                physical.Writes, physical.Refreshes, requests = physical.Requests,
                realPostgres = true, realOpenSearch = false,
                scope = "Recording HTTP collaborator through actual application client; SQL durability only" }));
    }

    private sealed class RecordingGenerationProvider : HttpMessageHandler
    {
        private readonly string physicalBody;
        private readonly Dictionary<string, JsonObject> documents = new();
        internal MarketplacePhysicalAdmission.Expected Expected { get; }
        internal int Writes { get; private set; }
        internal int Refreshes { get; private set; }
        internal List<string> Requests { get; } = new();
        private RecordingGenerationProvider(MarketplacePhysicalAdmission.Expected expected, string physicalBody)
        { Expected = expected; this.physicalBody = physicalBody; }

        internal static RecordingGenerationProvider Physical(Guid journal, Guid generation)
        {
            var expected = new MarketplacePhysicalAdmission.Expected(journal, generation, "recording-only-cluster", "recording-only-index", new string('a', 64));
            var mappings = JsonNode.Parse("""
                {"_meta":{},"properties":{"serviceId":{"type":"keyword"},"isActive":{"type":"boolean"},
                "__marketplace":{"dynamic":"strict","properties":{"journal":{"type":"keyword"},"generation":{"type":"keyword"},
                "digest":{"type":"keyword"},"revision":{"type":"long"},"epoch":{"type":"long"},"deleted":{"type":"boolean"}}}}}
                """)!.AsObject();
            mappings["_meta"] = JsonSerializer.SerializeToNode(new { journal = journal.ToString("D"), generation = generation.ToString("D"), schema = "marketplace-generation-v1" });
            var settings = JsonSerializer.SerializeToElement(new { uuid = expected.PhysicalUuid, provided_name = expected.PhysicalName,
                number_of_shards = "1", number_of_replicas = "0" });
            expected = expected with { Fingerprint = MarketplacePhysicalAdmission.Fingerprint(JsonSerializer.SerializeToElement(mappings), settings) };
            var body = JsonSerializer.Serialize(new Dictionary<string, object> { [expected.PhysicalName] =
                new { aliases = new { }, settings = new { index = settings }, mappings } });
            return new(expected, body);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("fictional-generation.invalid", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            Requests.Add(request.Method + " " + request.RequestUri.PathAndQuery);
            string response;
            if (path == "/") response = JsonSerializer.Serialize(new { cluster_uuid = Expected.ClusterUuid });
            else if (path == "/" + Expected.PhysicalName) response = physicalBody;
            else if (path.Contains("/_update/", StringComparison.Ordinal))
            {
                Assert.Equal("?retry_on_conflict=0", request.RequestUri.Query);
                var id = path.Split('/')[^1];
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                var parameters = body["script"]!["params"]!;
                var source = parameters["document"]!.DeepClone().AsObject();
                source["__marketplace"] = new JsonObject();
                foreach (var field in new[] { "journal", "generation", "revision", "epoch", "digest", "deleted" })
                    source["__marketplace"]![field] = parameters[field]!.DeepClone();
                documents[id] = source; Writes++;
                response = JsonSerializer.Serialize(new { _index = Expected.PhysicalName, _id = id, result = "updated", _seq_no = Writes,
                    _primary_term = 1, _shards = new { total = 1, successful = 1, failed = 0 } });
            }
            else if (path.EndsWith("/_refresh", StringComparison.Ordinal))
            { Refreshes++; response = "{\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0}}"; }
            else if (path.EndsWith("/_mget", StringComparison.Ordinal))
            {
                Assert.Equal("?realtime=false", request.RequestUri.Query);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                response = JsonSerializer.Serialize(new { docs = body.RootElement.GetProperty("ids").EnumerateArray().Select(id =>
                    new { _index = Expected.PhysicalName, _id = id.GetString(), found = true, _source = documents[id.GetString()!] }).ToArray() });
            }
            else if (path.EndsWith("/_count", StringComparison.Ordinal))
                response = JsonSerializer.Serialize(new { _shards = new { total = 1, successful = 1, failed = 0 }, count = documents.Count });
            else throw new InvalidOperationException("Unreviewed recording-provider path: " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
