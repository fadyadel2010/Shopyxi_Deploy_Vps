using System.Net;
using System.Text;
using System.Text.Json;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

// Executed local protocol/transport contracts only, never physical provider proof.
public sealed class MarketplaceGenerationDeliveryContractTests
{
    private static readonly Guid Service = Guid.Parse("46b0af3e-c572-49f5-b88c-78d5c5199b5c");
    private const string Index = "marketplace_services_g_6318dc4aa01a4b1f8b495953349e2ad5";

    [Theory]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("noop")]
    public void ExactDefiniteWriteAcknowledgementIsAccepted(string result) =>
        MarketplaceGenerationResponse.RequireWrite(Write(result), Index, Service);

    [Fact]
    public void ZeroShardNoopStillRejectsAnExplicitTimeoutBeforeAcknowledging()
    {
        var noop = Write("noop").Replace("\"total\":1", "\"total\":0", StringComparison.Ordinal)
            .Replace("\"successful\":1", "\"successful\":0", StringComparison.Ordinal);
        MarketplaceGenerationResponse.RequireWrite(noop, Index, Service);
        var timedOut = noop.Replace("\"timed_out\":false", "\"timed_out\":true", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => MarketplaceGenerationResponse.RequireWrite(timedOut, Index, Service));
    }

    [Theory]
    [InlineData("index")]
    [InlineData("identity")]
    [InlineData("result")]
    [InlineData("sequence")]
    [InlineData("term")]
    [InlineData("partial")]
    [InlineData("failed")]
    [InlineData("timeout")]
    [InlineData("duplicate")]
    public void AmbiguousOrForeignWriteRepliesCannotAcknowledge(string defect)
    {
        var valid = Write("updated");
        var changed = defect switch
        {
            "index" => valid.Replace(Index, "unrelated-index", StringComparison.Ordinal),
            "identity" => valid.Replace(Service.ToString("D"), Guid.Empty.ToString("D"), StringComparison.Ordinal),
            "result" => valid.Replace("updated", "deleted", StringComparison.Ordinal),
            "sequence" => valid.Replace("\"_seq_no\":1", "\"_seq_no\":-1", StringComparison.Ordinal),
            "term" => valid.Replace("\"_primary_term\":1", "\"_primary_term\":0", StringComparison.Ordinal),
            "partial" => valid.Replace("\"total\":1", "\"total\":2", StringComparison.Ordinal),
            "failed" => valid.Replace("\"failed\":0", "\"failed\":1", StringComparison.Ordinal),
            "timeout" => valid.Replace("\"timed_out\":false", "\"timed_out\":true", StringComparison.Ordinal),
            _ => valid.Replace("\"result\":\"updated\"", "\"result\":\"updated\",\"result\":\"updated\"", StringComparison.Ordinal)
        };
        Assert.Throws<InvalidDataException>(() => MarketplaceGenerationResponse.RequireWrite(changed, Index, Service));
    }

    [Theory]
    [InlineData("{\"_shards\":{\"total\":2,\"successful\":1,\"failed\":0},\"count\":2}")]
    [InlineData("{\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0},\"count\":1}")]
    [InlineData("{\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0},\"count\":2,\"timed_out\":true}")]
    public void VisibilityRefusesPartialOrIncorrectCounts(string json) =>
        Assert.Throws<InvalidDataException>(() => MarketplaceGenerationResponse.RequireCount(json, 2));

    [Fact]
    public void CompleteCountIsOnlyAVisibilityObservation() =>
        MarketplaceGenerationResponse.RequireCount("{\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0},\"count\":2}", 2);

    [Theory]
    [InlineData("http://fixture.invalid/")]
    [InlineData("https://fixture.invalid/other")]
    [InlineData("https://other:secret@fixture.invalid/")]
    [InlineData("https://fixture.invalid/?query=bad")]
    public void TransportRejectsUnpinnedOrigins(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new MarketplaceGenerationTransport(new Uri(endpoint), "fixture", "not-real", new RecordingHandler()));

    [Theory]
    [InlineData("//foreign.invalid/x")]
    [InlineData("/%2e%2e/x")]
    [InlineData("/index#fragment")]
    [InlineData("/index\\escape")]
    public async Task TransportRejectsForeignOrEncodedPathsBeforeSending(string path)
    {
        var handler = new RecordingHandler();
        using var transport = Transport(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(HttpMethod.Get, path, null, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GenerationQueryParametersReachTheSupportedClientAsQueryNotEscapedPath()
    {
        var handler = new RecordingHandler(); using var transport = Transport(handler);
        await transport.SendAsync(HttpMethod.Post, "/index/_update/fictional?retry_on_conflict=0", "{}", default);
        Assert.Equal("/index/_update/fictional", handler.LastUri!.AbsolutePath);
        Assert.Equal("?retry_on_conflict=0", handler.LastUri.Query);
        await transport.SendAsync(HttpMethod.Post, "/index/_mget?realtime=false", "{}", default);
        Assert.Equal("/index/_mget", handler.LastUri.AbsolutePath);
        Assert.Equal("?realtime=false", handler.LastUri.Query);
    }

    [Theory]
    [InlineData("/index/_mget?realtime=true")]
    [InlineData("/index/_update/fictional?retry_on_conflict=9")]
    [InlineData("/index/_mget?realtime=false&preference=foreign")]
    public async Task GenerationTransportRejectsUnreviewedQueryParametersWithoutSending(string path)
    {
        var handler = new RecordingHandler(); using var transport = Transport(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(HttpMethod.Post, path, "{}", default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TransportDoesNotReplayAnUnknownRemoteOutcome()
    {
        var handler = new RecordingHandler { Failure = new HttpRequestException("Fixture lost acknowledgement") };
        using var transport = Transport(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(HttpMethod.Post, "/index/_update/fictional", "{}", default));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TransportDoesNotTurnErrorsOrRedirectsIntoAcknowledgement(HttpStatusCode code)
    {
        var handler = new RecordingHandler { Status = code };
        using var transport = Transport(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(HttpMethod.Get, "/", null, default));
        Assert.Equal(code, error.StatusCode); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancellationBeforeDispatchDoesNotSend()
    {
        var handler = new RecordingHandler(); using var transport = Transport(handler);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(HttpMethod.Get, "/", null, cancelled.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupportedClientPipelineRefusesOversizedKnownAndChunkedBodiesWithoutRetry(bool knownLength)
    {
        var bytes = new byte[16 * 1024 * 1024 + 1];
        var handler = new RecordingHandler { Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new NonSeekableBody(bytes)) };
        using var transport = Transport(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(HttpMethod.Get, "/", null, default));
        Assert.Equal(1, handler.Calls);
    }

    private static string Write(string result) => JsonSerializer.Serialize(new
    {
        _index = Index, _id = Service.ToString("D"), result, _seq_no = 1, _primary_term = 1,
        _shards = new { total = 1, successful = 1, failed = 0 }, timed_out = false
    });
    private static MarketplaceGenerationTransport Transport(RecordingHandler handler) =>
        new(new Uri("https://fixture.invalid/"), "fixture-only", "not-a-real-credential", handler);
    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal Uri? LastUri { get; private set; }
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        internal Exception? Failure { get; init; }
        internal HttpContent? Content { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            if (Failure is not null) throw Failure;
            Assert.Equal("fixture.invalid", request.RequestUri!.Host);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = Content ?? new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NonSeekableBody(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }
}
