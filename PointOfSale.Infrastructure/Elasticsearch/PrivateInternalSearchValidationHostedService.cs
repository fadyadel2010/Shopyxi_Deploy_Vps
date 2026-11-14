using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.Infrastructure.Elasticsearch.Configuration;

namespace PointOfSale.Infrastructure.Elasticsearch;

/// <summary>Private startup observes existing search topology; never calls ensure/create/repair APIs.</summary>
public sealed class PrivateInternalSearchValidationHostedService(
    IOpenSearchClient client,
    IOptions<ElasticsearchSettings> settings) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = settings.Value;
        if (!options.Enabled)
            throw new InvalidOperationException("Private internal search validation requires the mandatory search dependency.");

        var marketplace = await MarketplaceSearchTopology.InspectAsync(client, cancellationToken);
        if (!marketplace.Compatible)
            throw new InvalidOperationException("Private internal search topology validation failed; provisioning is not permitted.");

        foreach (var name in new[] { options.ProductsIndexName, "categories", "cmspages",
                     options.PublicAffiliateProductsIndexName, options.CommerceHubProductsIndexName }.Distinct(StringComparer.Ordinal))
        {
            if (!MarketplaceSearchIndexOwnership.IsOtherOwnerIndexName(name))
                throw new InvalidOperationException("Private internal search target is not an approved explicit index.");
            var response = await client.LowLevel.DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.GET,
                "/" + Uri.EscapeDataString(name) + "/_mapping", cancellationToken);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Body))
                throw new InvalidOperationException("Private internal search mapping validation failed; no indexes were changed.");
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.EnumerateObject().Any() ||
                document.RootElement.EnumerateObject().Any(index =>
                    !index.Value.TryGetProperty("mappings", out var mappings) ||
                    !mappings.TryGetProperty("properties", out var properties) ||
                    properties.ValueKind != JsonValueKind.Object || !properties.EnumerateObject().Any()))
                throw new InvalidOperationException("Private internal search mapping is missing; no indexes were changed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
