using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.Infrastructure.Elasticsearch.Configuration;
using PointOfSale.Infrastructure.Elasticsearch.Services;

namespace PointOfSale.Tests.CommerceHub;

public class CommerceHubAliasReadScopeTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task AliasDiscovery_UsesOnlyOwnerScopedRead_WithoutGlobalFallback(int status)
    {
        var connection = new RecordingConnection(status);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Service(connection, provider);

        var aliases = await ReadAliases(service, "commercehub_products");

        Assert.Equal(new[] { "GET /commercehub_products/_alias/commercehub_products" }, connection.Requests);
        if (status == 200)
        {
            Assert.Single(aliases);
            Assert.True(aliases["commercehub_products_v2"]);
        }
        else
        {
            Assert.Empty(aliases);
        }
    }

    [Theory]
    [InlineData("marketplace_services")]
    [InlineData("commercehub_products,marketplace_services")]
    [InlineData("commercehub_products/_alias")]
    public async Task AliasDiscovery_RejectsForeignOrSelectorTargetsBeforeTransport(string alias)
    {
        var connection = new RecordingConnection(200);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Service(connection, provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAliases(service, alias));

        Assert.Empty(connection.Requests);
    }

    private static CommerceHubSearchIndexService Service(RecordingConnection connection, IServiceProvider provider)
    {
        var client = new OpenSearchClient(new ConnectionSettings(
            new SingleNodeConnectionPool(new Uri("https://fixture.invalid")), connection).MaximumRetries(0));
        return new CommerceHubSearchIndexService(client,
            Options.Create(new ElasticsearchSettings { Enabled = true, CommerceHubProductsIndexName = "commercehub_products" }),
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CommerceHubSearchIndexService>.Instance);
    }

    private static async Task<Dictionary<string, bool>> ReadAliases(CommerceHubSearchIndexService service, string alias)
    {
        var method = typeof(CommerceHubSearchIndexService).GetMethod("LoadAliasMapAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            return await (Task<Dictionary<string, bool>>)method.Invoke(service, [alias, CancellationToken.None])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException inner)
        {
            throw inner;
        }
    }

    private sealed class RecordingConnection(int status) : InMemoryConnection
    {
        public List<string> Requests { get; } = [];

        public override Task<TResponse> RequestAsync<TResponse>(RequestData data, CancellationToken ct)
        {
            Requests.Add(data.Method + " " + data.Uri.AbsolutePath);
            const string body = "{\"commercehub_products_v2\":{\"aliases\":{\"commercehub_products\":{\"is_write_index\":true}}}}";
            return new InMemoryConnection(Encoding.UTF8.GetBytes(body), status).RequestAsync<TResponse>(data, ct);
        }
    }
}
