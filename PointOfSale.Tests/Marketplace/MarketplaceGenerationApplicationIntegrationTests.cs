using System.Net;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.Core.DTOs.Marketplace;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Consumers;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Marketplace;

/// <summary>Actual application entry points with deterministic collaborators; not provider proof.</summary>
public sealed class MarketplaceGenerationApplicationIntegrationTests
{
    [Fact]
    public void ProvisioningPlanMapsEveryCanonicalFieldBeforeAnyDeliveryCanChangeTheFingerprint()
    {
        using var plan = System.Text.Json.JsonDocument.Parse(MarketplaceGenerationIndexDefinition.CreateBody(Guid.NewGuid(), Guid.NewGuid(), 1, 0));
        var mappings = plan.RootElement.GetProperty("mappings");
        Assert.Equal("strict", mappings.GetProperty("dynamic").GetString());
        var properties = mappings.GetProperty("properties");
        foreach (var property in typeof(PointOfSale.Infrastructure.Elasticsearch.Documents.MarketplaceServiceDocument).GetProperties())
            Assert.True(properties.TryGetProperty(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property.Name), out _), property.Name);
        Assert.Equal("strict", properties.GetProperty("__marketplace").GetProperty("dynamic").GetString());
        Assert.Equal("long", properties.GetProperty("__marketplace").GetProperty("properties").GetProperty("revision").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("activation")]
    [InlineData("trust")]
    [InlineData("completion")]
    public async Task EveryProductionConsumerRoutesBeforeAnyLegacyQueryScriptOrDelete(string entry)
    {
        await using var db = Database();
        var client = new Mock<IOpenSearchClient>(MockBehavior.Strict);
        var router = new Mock<IMarketplaceGenerationMutationRouter>(MockBehavior.Strict);
        router.Setup(r => r.TryRouteAsync(It.Is<Guid>(id => id != Guid.Empty), It.IsAny<string>(),
            It.Is<Guid>(id => id != Guid.Empty), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var consumer = new ServiceDiscoveryProjectionConsumer(client.Object, db, NullLogger<ServiceDiscoveryProjectionConsumer>.Instance, router.Object);
        var service = Guid.NewGuid();
        switch (entry)
        {
            case "create": await consumer.Consume(Context(new ServiceCreatedEvent { ServiceId = service })); break;
            case "update": await consumer.Consume(Context(new ServiceUpdatedEvent { ServiceId = service })); break;
            case "activation": await consumer.Consume(Context(new ServiceActivationChangedEvent { ServiceId = service })); break;
            case "trust": await consumer.Consume(Context(new ProviderTrustUpdatedEvent { ProviderProfileId = service })); break;
            case "completion": await consumer.Consume(Context(new ServiceRequestCompletedEvent { ServiceId = service })); break;
        }
        router.Verify(r => r.TryRouteAsync(It.IsAny<Guid>(), It.IsAny<string>(), service, It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls(); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task RoutingPersistenceFailureCannotFallBackToLegacyMutation()
    {
        await using var db = Database();
        var client = new Mock<IOpenSearchClient>(MockBehavior.Strict);
        var router = new Mock<IMarketplaceGenerationMutationRouter>(MockBehavior.Strict);
        router.Setup(r => r.TryRouteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("durability unavailable"));
        var consumer = new ServiceDiscoveryProjectionConsumer(client.Object, db, NullLogger<ServiceDiscoveryProjectionConsumer>.Instance, router.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(Context(new ServiceUpdatedEvent { ServiceId = Guid.NewGuid() })));
        client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchServiceUsesPinnedPhysicalTargetAndAlwaysDisposesScope(bool fail)
    {
        await using var db = Database();
        var connection = new SearchConnection(fail);
        var client = new OpenSearchClient(new ConnectionSettings(new SingleNodeConnectionPool(new Uri("https://fictional.invalid")), connection)
            .MaximumRetries(0).UseSystemTextJson(false));
        var reads = new PinnedReads();
        var service = new MarketplaceSearchService(client, db, new TestTenantContext(Guid.NewGuid()),
            Mock.Of<IProviderTrustService>(MockBehavior.Strict), NullLogger<MarketplaceSearchService>.Instance, reads);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchServicesAsync(new MarketplaceSearchRequest()));
        else
        {
            var first = await service.SearchServicesAsync(new MarketplaceSearchRequest());
            Assert.Equal("opaque-pin", first.GenerationContinuation);
            var second = await service.SearchServicesAsync(new MarketplaceSearchRequest { Page = 2, GenerationContinuation = first.GenerationContinuation });
            Assert.Equal(first.GenerationContinuation, second.GenerationContinuation);
        }
        Assert.Equal(fail ? 1 : 2, reads.Disposals);
        Assert.All(connection.Paths, path => Assert.StartsWith("/" + PinnedReads.Physical + "/_search", path));
    }

    [Fact]
    public async Task PagedGenerationSearchWithoutContinuationIsRejectedBeforeProviderAccess()
    {
        await using var db = Database(); var client = new Mock<IOpenSearchClient>(MockBehavior.Strict); var reads = new PinnedReads();
        var service = new MarketplaceSearchService(client.Object, db, new TestTenantContext(Guid.NewGuid()),
            Mock.Of<IProviderTrustService>(), NullLogger<MarketplaceSearchService>.Instance, reads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchServicesAsync(new MarketplaceSearchRequest { Page = 2 }));
        Assert.Equal(1, reads.Disposals); client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisabledReadersNeverAcceptOrIgnoreAGenerationContinuation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => InactiveMarketplaceGenerationReads.Instance.BeginAsync(
            Guid.NewGuid(), new string('a', 64), "foreign-pin", true, default));
    }

    [Fact]
    public void QueryIdentityPreservesOrderingFiltersPageSizeAndCursorVersionButNotPageNumber()
    {
        var request = new MarketplaceSearchRequest { Query = "fixture", Category = "Design", Sort = "newest", PageSize = 10 };
        var hash = MarketplaceGenerationReadScope.QueryHash(request);
        Assert.Equal(hash, MarketplaceGenerationReadScope.QueryHash(request with { Page = 2, GenerationContinuation = "pin" }));
        Assert.NotEqual(hash, MarketplaceGenerationReadScope.QueryHash(request with { PageSize = 20 }));
        Assert.NotEqual(hash, MarketplaceGenerationReadScope.QueryHash(request with { Sort = "rating" }));
        Assert.NotEqual(hash, MarketplaceGenerationReadScope.QueryHash(request with { Category = "Other" }));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("bulk")]
    [InlineData("alias")]
    [InlineData("reindex")]
    [InlineData("alternate-alias")]
    [InlineData("legacy-alias")]
    public async Task SharedTransportRejectsEveryReviewedGenerationMutationBoundary(string boundary)
    {
        var provider = new GuardProvider();
        using var client = new HttpClient(new MarketplaceApplicationTransportGuard(provider));
        var target = "marketplace_services_g_" + Guid.NewGuid().ToString("N");
        var path = boundary switch { "direct" => "/" + target + "/_update/id", "bulk" => "/_bulk",
            "alias" => "/_aliases", "reindex" => "/_reindex", "legacy-alias" => "/marketplace_services/_update/id", _ => "/apparently-products/_update/id" };
        var body = boundary switch { "bulk" => "{\"update\":{\"_index\":\"" + target + "\",\"_id\":\"id\"}}\n{}\n",
            "alias" => "{\"actions\":[{\"add\":{\"index\":\"" + target + "\",\"alias\":\"products\"}}]}",
            "reindex" => "{\"source\":{\"index\":\"products\"},\"dest\":{\"index\":\"" + target + "\"}}", _ => "{}" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("https://fictional.invalid" + path, new StringContent(body)));
        Assert.Equal(0, provider.Mutations);
    }

    [Theory]
    [InlineData("/products/_update/id", "{}")]
    [InlineData("/_bulk", "{\"update\":{\"_index\":\"products\",\"_id\":\"id\"}}\n{}\n")]
    [InlineData("/_reindex", "{\"source\":{\"index\":\"products\"},\"dest\":{\"index\":\"products_backup\"}}")]
    public async Task SharedTransportPreservesExplicitOtherOwnerMutationPaths(string path, string body)
    {
        var provider = new GuardProvider { GenerationAlias = false };
        using var client = new HttpClient(new MarketplaceApplicationTransportGuard(provider));
        using var response = await client.PostAsync("https://fictional.invalid" + path, new StringContent(body));
        Assert.True(response.IsSuccessStatusCode); Assert.Equal(1, provider.Mutations);
    }

    private static ApplicationDbContext Database() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static ConsumeContext<T> Context<T>(T message) where T : class
    {
        var mock = new Mock<ConsumeContext<T>>(); mock.SetupGet(c => c.Message).Returns(message);
        mock.SetupGet(c => c.MessageId).Returns(Guid.NewGuid()); return mock.Object;
    }
    private sealed class PinnedReads : IMarketplaceGenerationReads
    {
        internal const string Physical = "marketplace_services_g_11111111111111111111111111111111";
        internal int Disposals;
        public Task<MarketplaceGenerationReadScope> BeginAsync(Guid tenant, string hash, string? cursor, bool keep, CancellationToken ct) =>
            Task.FromResult<MarketplaceGenerationReadScope>(new Scope(this, ct));
        private sealed class Scope(PinnedReads owner, CancellationToken ct) : MarketplaceGenerationReadScope
        {
            public override string PhysicalName => Physical;
            public override string? Continuation => "opaque-pin";
            public override CancellationToken CancellationToken => ct;
            public override ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
        }
    }
    private sealed class SearchConnection(bool fail) : InMemoryConnection
    {
        internal List<string> Paths = [];
        public override Task<TResponse> RequestAsync<TResponse>(RequestData data, CancellationToken ct)
        {
            Paths.Add(data.Uri.AbsolutePath);
            var json = fail ? "{\"error\":{\"type\":\"unavailable\"},\"status\":503}" :
                "{\"took\":1,\"timed_out\":false,\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0},\"hits\":{\"total\":{\"value\":0,\"relation\":\"eq\"},\"hits\":[]}}";
            return new InMemoryConnection(Encoding.UTF8.GetBytes(json), fail ? 503 : 200).RequestAsync<TResponse>(data, ct);
        }
    }
    private sealed class GuardProvider : HttpMessageHandler
    {
        internal int Mutations;
        internal bool GenerationAlias = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != System.Net.Http.HttpMethod.Get) Mutations++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                GenerationAlias ? "{\"marketplace_services_g_22222222222222222222222222222222\":{}}" :
                    "{\"" + request.RequestUri!.AbsolutePath.Trim('/') + "\":{}}") });
        }
    }
}
