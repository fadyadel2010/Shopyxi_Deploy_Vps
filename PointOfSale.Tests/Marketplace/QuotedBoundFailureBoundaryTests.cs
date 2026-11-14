using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.Core.Abstractions;
using PointOfSale.Core.DTOs.Marketplace;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Elasticsearch.Documents;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Marketplace;

public sealed class QuotedBoundFailureBoundaryTests
{
    private static byte[] Response(bool quoted) => Encoding.UTF8.GetBytes(
        "{\"took\":1,\"timed_out\":false,\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0},\"hits\":{\"total\":{\"value\":0,\"relation\":\"eq\"},\"hits\":[]},\"aggregations\":{\"sterms#facet_categories\":{\"doc_count_error_upper_bound\":" +
        (quoted ? "\"-1\"" : "-1") + ",\"sum_other_doc_count\":0,\"buckets\":[]}}}");

    [Theory]
    [InlineData("")]
    [InlineData("ar-EG")]
    [InlineData("fa-IR")]
    [InlineData("fr-FR")]
    public async Task QuotedSyntheticBoundRemainsRejectedAndCannotBecomeSuccessfulApplicationSearch(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // Scoped diagnostic culture only; no application/global culture policy change.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var client = Client(true);
            using var body = new MemoryStream(Response(true));
            var parse = Assert.Throws<OpenSearch.Net.Utf8Json.JsonParsingException>(() =>
                client.RequestResponseSerializer.Deserialize<SearchResponse<MarketplaceServiceDocument>>(body));
            Assert.Contains("Number Token", parse.Message, StringComparison.Ordinal);
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var trust = new Mock<IProviderTrustService>(MockBehavior.Strict);
            var service = new MarketplaceSearchService(client, db, new TestTenantContext(Guid.NewGuid()), trust.Object, NullLogger<MarketplaceSearchService>.Instance);
            var error = await Record.ExceptionAsync(() => service.SearchServicesAsync(new MarketplaceSearchRequest()));
            Assert.NotNull(error);
            Assert.True(error is OpenSearchClientException or InvalidOperationException, error.ToString());
            if (error is OpenSearchClientException) Assert.Contains("Number Token", error.ToString(), StringComparison.Ordinal);
            else Assert.Equal("Marketplace search is temporarily unavailable.", error.Message);
            trust.VerifyNoOtherCalls();
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task NumericNegativeBoundRemainsValidThroughUnchangedTransport()
    {
        var response = await Client(false).SearchAsync<MarketplaceServiceDocument>(s => s.Index("synthetic-only"));
        Assert.True(response.IsValid, response.DebugInformation);
        Assert.Equal(-1, response.Aggregations.Terms("facet_categories").DocCountErrorUpperBound);
    }

    public static IEnumerable<object[]> NumericBoundCases()
    {
        foreach (var culture in new[] { "", "ar-EG", "fa-IR", "fr-FR" })
        foreach (var scope in new[] { "root", "bucket" })
        foreach (var value in new long[] { 0, 5, -1 })
            yield return [culture, scope, value];
    }

    public static IEnumerable<object[]> QuotedPartialHitCases()
    {
        foreach (var culture in new[] { "", "ar-EG", "fa-IR", "fr-FR" })
        foreach (var scope in new[] { "root", "bucket" })
            yield return [culture, scope];
    }

    [Theory]
    [MemberData(nameof(NumericBoundCases))]
    public async Task NumericRootAndBucketErrorBoundsKeepExactValuesAcrossCultures(string culture, string scope, long value)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var response = await FixtureClient(BoundResponse(scope, value, false))
                .SearchAsync<MarketplaceServiceDocument>(s => s.Index("synthetic-only"));
            Assert.True(response.IsValid, response.DebugInformation);
            var document = Assert.Single(response.Documents);
            Assert.Equal("SYNTHETIC-PARTIAL-HIT-NEVER-AUTHORITY", document.Title);
            var terms = response.Aggregations.Terms("facet_categories");
            Assert.NotNull(terms);
            Assert.Equal(scope == "root" ? value : 0L, terms.DocCountErrorUpperBound);
            var bucket = Assert.Single(terms.Buckets);
            Assert.Equal("fixture-category", bucket.Key);
            Assert.Equal(1L, bucket.DocCount);
            Assert.Equal(scope == "bucket" ? value : 0L, bucket.DocCountErrorUpperBound);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [MemberData(nameof(QuotedPartialHitCases))]
    public async Task QuotedRootOrBucketBoundCannotSalvagePoisonedPartialHits(string culture, string scope)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var bytes = BoundResponse(scope, -1, true);
            var client = FixtureClient(bytes);
            using var body = new MemoryStream(bytes);
            var parse = Assert.Throws<OpenSearch.Net.Utf8Json.JsonParsingException>(() =>
                client.RequestResponseSerializer.Deserialize<SearchResponse<MarketplaceServiceDocument>>(body));
            Assert.Contains("Number Token", parse.Message, StringComparison.Ordinal);
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var trust = new Mock<IProviderTrustService>(MockBehavior.Strict);
            var service = new MarketplaceSearchService(client, db, new TestTenantContext(Guid.NewGuid()), trust.Object,
                NullLogger<MarketplaceSearchService>.Instance);
            var error = await Record.ExceptionAsync(() => service.SearchServicesAsync(new MarketplaceSearchRequest()));
            Assert.NotNull(error);
            Assert.True(error is OpenSearchClientException or InvalidOperationException, error.ToString());
            if (error is OpenSearchClientException) Assert.Contains("Number Token", error.ToString(), StringComparison.Ordinal);
            else Assert.Equal("Marketplace search is temporarily unavailable.", error.Message);
            trust.VerifyNoOtherCalls();
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [MemberData(nameof(QuotedPartialHitCases))]
    public async Task HttpBoundaryReturnsUnavailableWithoutFallbackOrPartialSuccessForQuotedBounds(string culture, string scope)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var client = FixtureClient(BoundResponse(scope, -1, true));
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var trust = new Mock<IProviderTrustService>(MockBehavior.Strict);
            var service = new MarketplaceSearchService(client, db, new TestTenantContext(Guid.NewGuid()), trust.Object, NullLogger<MarketplaceSearchService>.Instance);
            var controller = Controller(service, db, client, trust.Object, default);
            var result = Assert.IsType<ObjectResult>(await controller.SearchServices(new MarketplaceSearchRequest()));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            var body = System.Text.Json.JsonSerializer.Serialize(result.Value);
            Assert.Contains("Marketplace search is temporarily unavailable.", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC-PARTIAL-HIT-NEVER-AUTHORITY", body, StringComparison.Ordinal);
            trust.VerifyNoOtherCalls(); Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpRequestAbortCancelsSearchWithoutA503OrSuccessfulFallback(bool alreadyCancelled)
    {
        using var abort = new CancellationTokenSource();
        var connection = new CancellationConnection();
        var client = new OpenSearchClient(new ConnectionSettings(new SingleNodeConnectionPool(new Uri("http://synthetic.invalid")), connection)
            .MaximumRetries(0).ThrowExceptions(false).UseSystemTextJson(false));
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var trust = new Mock<IProviderTrustService>(MockBehavior.Strict);
        var service = new MarketplaceSearchService(client, db, new TestTenantContext(Guid.NewGuid()), trust.Object, NullLogger<MarketplaceSearchService>.Instance);
        var controller = Controller(service, db, client, trust.Object, abort.Token);
        if (alreadyCancelled) { abort.Cancel(); connection.Release.TrySetResult(); }
        var pending = controller.SearchServices(new MarketplaceSearchRequest());
        try
        {
            if (!alreadyCancelled) await connection.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            abort.Cancel(); connection.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () => await pending);
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            if (alreadyCancelled) Assert.Equal(0, connection.Calls);
            else
            {
                Assert.Equal(1, connection.Calls);
                // The pinned SDK may use a linked timeout token, not token
                // equality. Actual request abort must cancel that token.
                Assert.True(connection.ObservedToken.IsCancellationRequested);
            }
            trust.VerifyNoOtherCalls(); Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            abort.Cancel(); connection.Release.TrySetResult();
            try { await pending; } catch { /* Drain only; actual assertion remains above. */ }
        }
    }

    [Fact]
    public async Task UnrelatedServiceCancellationWithoutRequestAbortRemainsUnavailableNotSuccessfulFallback()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var search = new Mock<IMarketplaceSearchService>(MockBehavior.Strict);
        search.Setup(s => s.SearchServicesAsync(It.IsAny<MarketplaceSearchRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Synthetic upstream cancellation, request still active"));
        var controller = Controller(search.Object, db, Client(false), Mock.Of<IProviderTrustService>(), default);
        var result = Assert.IsType<ObjectResult>(await controller.SearchServices(new MarketplaceSearchRequest()));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        search.Verify(s => s.SearchServicesAsync(It.IsAny<MarketplaceSearchRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        search.VerifyNoOtherCalls(); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("request-abort-cleanup-ok")]
    [InlineData("request-abort-cleanup-fails")]
    [InlineData("completed-scan-cleanup-fails")]
    [InlineData("failed-scan-cleanup-fails")]
    public async Task ScrollCleanupIsBoundedIndependentAndCannotTurnIncompleteWorkIntoSuccess(string state)
    {
        using var abort = new CancellationTokenSource();
        var transport = new ScrollCleanupConnection(state);
        var client = new OpenSearchClient(new ConnectionSettings(new SingleNodeConnectionPool(new Uri("http://synthetic.invalid")), transport)
            .MaximumRetries(0).ThrowExceptions(false).UseSystemTextJson(false));
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var trust = new Mock<IProviderTrustService>(MockBehavior.Strict);
        var service = new MarketplaceSearchService(client, db, new TestTenantContext(Guid.NewGuid()), trust.Object, NullLogger<MarketplaceSearchService>.Instance);
        var controller = Controller(service, db, client, trust.Object, abort.Token);
        var pending = controller.SearchServices(new MarketplaceSearchRequest());
        try
        {
            if (state.StartsWith("request-abort", StringComparison.Ordinal))
            {
                await transport.ScrollEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                abort.Cancel(); transport.Release.TrySetResult();
                var error = await Record.ExceptionAsync(async () => await pending);
                Assert.IsAssignableFrom<OperationCanceledException>(error);
            }
            else
            {
                var result = Assert.IsType<ObjectResult>(await pending);
                Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            }
            Assert.Equal(1, transport.CleanupCalls);
            Assert.True(transport.CleanupToken.CanBeCanceled, "Cleanup must have its own bounded cancellation budget.");
            Assert.False(transport.CleanupToken.IsCancellationRequested, "An already-aborted request cannot cancel required cleanup admission.");
            Assert.True(transport.CleanupBodyObserved);
            trust.VerifyNoOtherCalls(); Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            abort.Cancel(); transport.Release.TrySetResult();
            try { await pending; } catch { /* Drain owned transport; assertions retain outcome. */ }
        }
    }

    private sealed class ScrollCleanupConnection(string state) : InMemoryConnection
    {
        internal TaskCompletionSource ScrollEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CleanupCalls { get; private set; }
        internal CancellationToken CleanupToken { get; private set; }
        internal bool CleanupBodyObserved { get; private set; }
        public override async Task<TResponse> RequestAsync<TResponse>(RequestData data, CancellationToken cancellationToken)
        {
            if (data.Method == OpenSearch.Net.HttpMethod.DELETE)
            {
                CleanupCalls++; CleanupToken = cancellationToken;
                cancellationToken.ThrowIfCancellationRequested(); CleanupBodyObserved = true;
                var good = state == "request-abort-cleanup-ok";
                var body = good ? "{\"succeeded\":true,\"num_freed\":1}" : "{\"error\":{\"type\":\"unavailable_shards_exception\",\"reason\":\"synthetic cleanup failure\"},\"status\":503}";
                return await new InMemoryConnection(Encoding.UTF8.GetBytes(body), good ? 200 : 503).RequestAsync<TResponse>(data, cancellationToken);
            }
            if (data.Uri.AbsolutePath.EndsWith("/_search/scroll", StringComparison.Ordinal))
            {
                if (state.StartsWith("request-abort", StringComparison.Ordinal))
                {
                    ScrollEntered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellationToken.ThrowIfCancellationRequested();
                }
                if (state == "failed-scan-cleanup-fails")
                    return await new InMemoryConnection(Encoding.UTF8.GetBytes("{\"error\":{\"type\":\"unavailable_shards_exception\",\"reason\":\"synthetic scan failure\"},\"status\":503}"), 503).RequestAsync<TResponse>(data, cancellationToken);
                return await new InMemoryConnection(Encoding.UTF8.GetBytes("{\"_scroll_id\":\"fixture-scroll\",\"took\":1,\"timed_out\":false,\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0},\"hits\":{\"total\":{\"value\":1,\"relation\":\"eq\"},\"hits\":[]}}"), 200).RequestAsync<TResponse>(data, cancellationToken);
            }
            var first = System.Text.Json.Nodes.JsonNode.Parse(BoundResponse("root", -1, false))!.AsObject();
            first["_scroll_id"] = "fixture-scroll";
            return await new InMemoryConnection(Encoding.UTF8.GetBytes(first.ToJsonString()), 200).RequestAsync<TResponse>(data, cancellationToken);
        }
    }

    private static MarketplaceController Controller(IMarketplaceSearchService search, ApplicationDbContext db, IOpenSearchClient client,
        IProviderTrustService trust, CancellationToken abort) => new(search,
        Mock.Of<IMarketplaceProfileService>(MockBehavior.Strict), Mock.Of<IServiceCatalogService>(MockBehavior.Strict),
        Mock.Of<IServiceRequestService>(MockBehavior.Strict), Mock.Of<IServiceReviewService>(MockBehavior.Strict),
        Mock.Of<IChatService>(MockBehavior.Strict), trust, Mock.Of<IFileValidationService>(MockBehavior.Strict),
        Mock.Of<IR2StorageService>(MockBehavior.Strict), db, client, Mock.Of<IWebHostEnvironment>(MockBehavior.Strict),
        Mock.Of<IMarketplaceTelemetry>(), NullLogger<MarketplaceController>.Instance, Mock.Of<IMarketplaceSearchRepairAuthority>(MockBehavior.Strict))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = abort } } };

    private sealed class CancellationConnection : InMemoryConnection
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        internal CancellationToken ObservedToken { get; private set; }
        public override async Task<TResponse> RequestAsync<TResponse>(RequestData data, CancellationToken cancellationToken)
        {
            Calls++; ObservedToken = cancellationToken; Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellationToken.ThrowIfCancellationRequested();
            return await new InMemoryConnection(Response(false), 200).RequestAsync<TResponse>(data, cancellationToken);
        }
    }

    private static byte[] BoundResponse(string scope, long value, bool quoted)
    {
        Assert.True(scope is "root" or "bucket");
        object bound = quoted ? value.ToString(CultureInfo.InvariantCulture) : value;
        // Synthetic HTTP200 response only, never external server evidence. One
        // well-formed hit precedes aggregates to detect unsafe partial salvage.
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            took = 1, timed_out = false,
            _shards = new { total = 1, successful = 1, failed = 0 },
            hits = new
            {
                total = new { value = 1, relation = "eq" },
                hits = new[] { new { _index = "synthetic-only", _id = Guid.NewGuid().ToString(), _score = 1,
                    _source = new { serviceId = Guid.NewGuid(), tenantId = Guid.NewGuid(),
                        title = "SYNTHETIC-PARTIAL-HIT-NEVER-AUTHORITY", isActive = true } } }
            },
            aggregations = new Dictionary<string, object>
            {
                ["sterms#facet_categories"] = new
                {
                    doc_count_error_upper_bound = scope == "root" ? bound : 0L,
                    sum_other_doc_count = 0,
                    buckets = new[] { new { key = "fixture-category", doc_count = 1,
                        doc_count_error_upper_bound = scope == "bucket" ? bound : 0L } }
                }
            }
        });
    }

    private static OpenSearchClient FixtureClient(byte[] bytes) => new(new ConnectionSettings(
        new SingleNodeConnectionPool(new Uri("http://synthetic.invalid")), new InMemoryConnection(bytes, 200))
        .MaximumRetries(0).ThrowExceptions(false).UseSystemTextJson(false));

    private static OpenSearchClient Client(bool quoted) => new(new ConnectionSettings(
        new SingleNodeConnectionPool(new Uri("http://synthetic.invalid")), new InMemoryConnection(Response(quoted), 200)).UseSystemTextJson(false));
}
