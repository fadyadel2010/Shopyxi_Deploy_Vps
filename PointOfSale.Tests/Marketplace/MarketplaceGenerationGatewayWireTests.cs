using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

/// <summary>Supported client/real adapter wire tests with in-memory handlers only. No socket/provider/host is started.</summary>
public sealed class MarketplaceGenerationGatewayWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupportedProviderAdapterSubmitsFixedImmutableRequestAndRecoversLostReply(bool lost)
    {
        var wire = new ProviderWire { LoseReply = lost };
        using var transport = new MarketplaceGenerationTransport(new Uri("https://fictional.invalid/"), "gateway-only", "fictional", wire);
        var boundary = new MarketplaceGenerationGateway(new Journal(), new MarketplaceGenerationGatewayProvider(transport));
        if (lost)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => boundary.SubmitAsync(wire.Operation, default));
            Assert.Equal(GenerationOperationState.Applied, (await boundary.LookupAsync(wire.Operation, default))!.State);
        }
        else Assert.Equal(GenerationOperationState.Applied, (await boundary.SubmitAsync(wire.Operation, default)).State);
        Assert.Equal(GenerationOperationState.VisibleAfterRefresh, (await boundary.RefreshAndVerifyAsync(wire.Operation, default)).State);
        Assert.Equal(1, wire.Updates); Assert.True(wire.Refreshed);
        Assert.All(wire.Paths.Where(p => p != "/"), p => Assert.StartsWith("/" + wire.Operation.Target.PhysicalName, p));
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("submit")]
    [InlineData("verify")]
    public async Task RemoteGatewayClientBindsReceiptAndExactOriginWithoutAnyProviderCredential(string phase)
    {
        var op = new MarketplaceGenerationOperationRecoveryTests.Fixture().Operation();
        var handler = new GatewayWire(op);
        using var client = new MarketplaceGenerationRemoteBoundary(new Uri("https://gateway.invalid/"), handler);
        var receipt = phase switch
        {
            "lookup" => await client.LookupAsync(op, default),
            "submit" => await client.SubmitAsync(op, default),
            _ => await client.RefreshAndVerifyAsync(op, default)
        };
        Assert.Equal(op, receipt!.Operation); Assert.Equal("https://gateway.invalid/generation/" + phase, handler.Url);
        Assert.Null(handler.Authorization);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("error")]
    [InlineData("redirect")]
    [InlineData("null")]
    public async Task RemoteFailuresCannotProduceDefiniteSuccessOrFollowAnAlternateOrigin(string poison)
    {
        var op = new MarketplaceGenerationOperationRecoveryTests.Fixture().Operation();
        var handler = new GatewayWire(op) { Poison = poison };
        using var client = new MarketplaceGenerationRemoteBoundary(new Uri("https://gateway.invalid/"), handler);
        await Assert.ThrowsAnyAsync<Exception>(() => client.SubmitAsync(op, default));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("http://gateway.invalid/")]
    [InlineData("https://user:secret@gateway.invalid/")]
    [InlineData("https://gateway.invalid/prefix")]
    [InlineData("https://gateway.invalid/?alternate=origin")]
    public void RemoteGatewayRejectsInsecureOrAmbiguousOrigins(string origin) =>
        Assert.Throws<ArgumentException>(() => new MarketplaceGenerationRemoteBoundary(new Uri(origin), new GatewayWire(null!)));

    [Fact]
    public void IsolatedHostStartsBeforeApplicationGraphAndDefaultsToDisabled()
    {
        var root = Environment.GetEnvironmentVariable("TEST_SOLUTION_ROOT")!;
        var program = File.ReadAllText(Path.Combine(root, "PointOfSale.API/Program.cs"));
        Assert.True(program.IndexOf("--generation-write-gateway", StringComparison.Ordinal) < program.IndexOf("var builder =", StringComparison.Ordinal));
        var host = File.ReadAllText(Path.Combine(root, "PointOfSale.API/Infrastructure/MarketplaceGenerationGatewayHost.cs"));
        Assert.Contains("SHOPIXY_GENERATION_GATEWAY_ENABLED", host);
        Assert.Contains("ClientCertificateMode.RequireCertificate", host);
        Assert.Contains("SslMode.VerifyFull", host); Assert.Contains("Sources.Clear()", host);
        Assert.DoesNotContain("AddMarketplaceGenerationCoordination", host);
        Assert.DoesNotContain("AddHostedService", host); Assert.DoesNotContain("MigrateAsync", host);
        var run = File.ReadAllText(Path.Combine(root, "PointOfSale.Infrastructure/Marketplace/Generation/MarketplaceGenerationRunDelivery.cs"));
        Assert.DoesNotContain("new MarketplaceGenerationGatewayProvider", run);
        Assert.Contains("IGenerationAtomicWriteBoundary boundary", run);
    }

    [Fact]
    public async Task DisabledGatewayModeRefusesBeforeConstructingAHostOrAnyLiveClient()
    {
        const string key = "SHOPIXY_GENERATION_GATEWAY_ENABLED";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, null);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PointOfSale.API.Infrastructure.MarketplaceGenerationGatewayHost.RunAsync());
            Assert.Equal("GENERATION_GATEWAY_RELEASE_AUTHORIZATION_REQUIRED", error.Message);
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }

    [Fact]
    public async Task GuardedRetirementTransportUsesOnlyExactAuthorizedTargetAndVerifiesAbsence()
    {
        var wire = new ProviderWire();
        using var transport = new MarketplaceGenerationTransport(new Uri("https://fictional.invalid/"), "retirement-only", "fictional", wire);
        await MarketplaceGenerationPhysicalRetirement.DeleteAsync(wire.Operation.Target, transport, default,
            permit: new(Guid.NewGuid(), wire.Operation.Target));
        Assert.Equal(1, wire.Deletes);
    }

    [Fact]
    public async Task RetirementPermitCannotRebindToAnotherPhysicalTarget()
    {
        var wire = new ProviderWire();
        using var transport = new MarketplaceGenerationTransport(new Uri("https://fictional.invalid/"), "retirement-only", "fictional", wire);
        await Assert.ThrowsAsync<InvalidDataException>(() => MarketplaceGenerationPhysicalRetirement.DeleteAsync(wire.Operation.Target, transport, default,
            permit: new(Guid.NewGuid(), wire.Operation.Target with { PhysicalUuid = "foreign" })));
        Assert.Empty(wire.Paths); Assert.Equal(0, wire.Deletes);
    }

    private sealed class Journal : IGenerationGatewayJournal
    {
        public Task<GenerationDispatchPermit?> AdmitAsync(GenerationOperation operation, CancellationToken ct) =>
            Task.FromResult<GenerationDispatchPermit?>(new(Guid.NewGuid(), operation));
        public Task SettleAsync(GenerationOperation operation, CancellationToken ct, GenerationOperation? successor = null) => Task.CompletedTask;
        public Task<bool> HasOutstandingAsync(GenerationOperation operation, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class GatewayWire(GenerationOperation operation) : HttpMessageHandler
    {
        internal string? Url, Authorization, Poison;
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Url = request.RequestUri!.ToString(); Authorization = request.Headers.Authorization?.ToString();
            var actual = JsonSerializer.Deserialize<GenerationOperation>(await request.Content!.ReadAsStringAsync(ct))!;
            MarketplaceGenerationOperationRecovery.RequireSame(operation, actual);
            var receipt = new GenerationBoundaryReceipt(Poison == "foreign" ? operation with { Target = operation.Target with { PhysicalUuid = "foreign" } } : operation,
                request.RequestUri.AbsolutePath.EndsWith("verify", StringComparison.Ordinal) ? GenerationOperationState.VisibleAfterRefresh : GenerationOperationState.Applied);
            var reply = new HttpResponseMessage(Poison == "error" ? HttpStatusCode.ServiceUnavailable : Poison == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK)
                { Content = new StringContent(Poison == "null" ? "null" : JsonSerializer.Serialize(receipt)) };
            if (Poison == "redirect") reply.Headers.Location = new Uri("https://alternate.invalid/");
            return reply;
        }
    }

    private sealed class ProviderWire : HttpMessageHandler
    {
        internal readonly GenerationOperation Operation;
        private readonly JsonObject metadata;
        internal readonly List<string> Paths = new();
        internal int Updates, Deletes;
        internal bool LoseReply, Refreshed;
        private bool applied, deleted;
        internal ProviderWire()
        {
            var f = new MarketplaceGenerationOperationRecoveryTests.Fixture(); var op = f.Operation();
            var definition = JsonNode.Parse(MarketplaceGenerationIndexDefinition.CreateBody(op.Target.Journal, op.Target.Generation, 1, 0))!;
            var settings = definition["settings"]!.AsObject(); settings["uuid"] = op.Target.PhysicalUuid; settings["provided_name"] = op.Target.PhysicalName;
            settings["number_of_shards"] = "1"; settings["number_of_replicas"] = "0";
            using var m = JsonDocument.Parse(definition["mappings"]!.ToJsonString()); using var s = JsonDocument.Parse(settings.ToJsonString());
            var fingerprint = MarketplacePhysicalAdmission.Fingerprint(m.RootElement, s.RootElement);
            Operation = op with { Target = op.Target with { Fingerprint = fingerprint }, Ticket = op.Ticket with { Fingerprint = fingerprint } };
            metadata = new() { [Operation.Target.PhysicalName] = new JsonObject { ["aliases"] = new JsonObject(),
                ["mappings"] = definition["mappings"]!.DeepClone(), ["settings"] = new JsonObject { ["index"] = settings.DeepClone() } } };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path); string response;
            if (path == "/") response = JsonSerializer.Serialize(new { cluster_uuid = Operation.Target.ClusterUuid });
            else if (request.Method == System.Net.Http.HttpMethod.Delete)
            { Assert.Equal("/" + Operation.Target.PhysicalName, path); Deletes++; deleted = true; response = "{\"acknowledged\":true}"; }
            else if (path == "/" + Operation.Target.PhysicalName)
            {
                if (deleted) return new(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"index_not_found_exception\"}") };
                response = metadata.ToJsonString();
            }
            else if (path.Contains("/_update/", StringComparison.Ordinal))
            {
                Assert.Equal(MarketplaceVersionedDocument.BuildUpdate(Operation.Target.Journal, Operation.Ticket, Operation.Payload), await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("?retry_on_conflict=0", request.RequestUri.Query);
                Updates++; applied = true;
                if (LoseReply) { LoseReply = false; throw new IOException("Deterministic lost provider response."); }
                response = JsonSerializer.Serialize(new { _index = Operation.Target.PhysicalName, _id = Operation.Ticket.ServiceId.ToString("D"),
                    result = "created", _seq_no = 0, _primary_term = 1, _shards = new { total = 1, successful = 1, failed = 0 } });
            }
            else if (path.EndsWith("/_refresh", StringComparison.Ordinal))
            { Refreshed = true; response = "{\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0}}"; }
            else if (path.EndsWith("/_mget", StringComparison.Ordinal))
            {
                if (request.RequestUri.Query == "?realtime=false") Assert.True(Refreshed);
                var doc = new JsonObject { ["_index"] = Operation.Target.PhysicalName, ["_id"] = Operation.Ticket.ServiceId.ToString("D"), ["found"] = applied };
                if (applied)
                {
                    var source = JsonNode.Parse(Operation.Payload!)!.AsObject();
                    source["__marketplace"] = JsonSerializer.SerializeToNode(new { journal = Operation.Target.Journal.ToString("D"), generation = Operation.Target.Generation.ToString("D"),
                        revision = Operation.Ticket.SourceRevision, epoch = Operation.Ticket.Epoch, digest = Operation.Ticket.Digest, deleted = false });
                    doc["_source"] = source;
                }
                response = new JsonObject { ["docs"] = new JsonArray(doc) }.ToJsonString();
            }
            else throw new InvalidOperationException("Unreviewed wire request: " + path);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
