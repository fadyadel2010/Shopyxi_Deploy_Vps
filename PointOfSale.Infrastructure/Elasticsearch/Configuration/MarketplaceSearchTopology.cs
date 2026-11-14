using System.Text.Json;
using OpenSearch.Client;
using OpenSearch.Net;

namespace PointOfSale.Infrastructure.Elasticsearch.Configuration;

/// <summary>
/// Read-only admission for the legacy physical topology. This is NOT generation ownership:
/// no durable UUID pin, journal, epoch or cutover protocol exists yet. Never creates or repairs
/// topology, and never accepts an alias as a substitute for the established physical index.
/// </summary>
public static class MarketplaceSearchTopology
{
    public const string RecoveryBlockedCode = "MARKETPLACE_GENERATION_RECOVERY_PREREQUISITE_MISSING";

    public sealed record Inspection(bool Compatible, string Code, string? PhysicalUuid = null);

    public static async Task<Inspection> InspectAsync(IOpenSearchClient client, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var response = await client.LowLevel.DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.GET,
                "/" + MarketplaceServiceIndexConfiguration.IndexName, ct);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Body))
                return new(false, "MARKETPLACE_TOPOLOGY_UNAVAILABLE");
            using var json = JsonDocument.Parse(response.Body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty(MarketplaceServiceIndexConfiguration.IndexName, out var index) ||
                index.ValueKind != JsonValueKind.Object)
                return new(false, "MARKETPLACE_PHYSICAL_INDEX_REQUIRED");
            if (!index.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object ||
                !settings.TryGetProperty("index", out var indexSettings) || indexSettings.ValueKind != JsonValueKind.Object ||
                !indexSettings.TryGetProperty("uuid", out var uuid) || uuid.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(uuid.GetString()) || uuid.GetString() == "_na_")
                return new(false, "MARKETPLACE_PHYSICAL_UUID_REQUIRED");
            if (!index.TryGetProperty("mappings", out var mappings) || mappings.ValueKind != JsonValueKind.Object ||
                !mappings.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
                !HasType(properties, "serviceId", "keyword") || !HasType(properties, "tenantId", "keyword") ||
                !HasType(properties, "providerProfileId", "keyword") || !HasType(properties, "title", "text") ||
                !HasType(properties, "description", "text") || !HasType(properties, "isActive", "boolean") ||
                !HasType(properties, "createdAt", "date") ||
                !HasObject(properties, "trust", [("averageRating", "double"), ("totalReviews", "integer"), ("trustBadge", "keyword")]) ||
                !HasObject(properties, "serviceStats", [("totalRequests", "integer"), ("completedRequests", "integer"), ("completionRate", "double")]))
                return new(false, "MARKETPLACE_MAPPING_INCOMPATIBLE", uuid.GetString());
            return new(true, "MARKETPLACE_LEGACY_TOPOLOGY_COMPATIBLE", uuid.GetString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is JsonException or OpenSearchClientException)
        {
            return new(false, "MARKETPLACE_TOPOLOGY_UNAVAILABLE");
        }
    }

    private static bool HasType(JsonElement properties, string name, string expected) =>
        properties.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object &&
        field.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == expected;

    private static bool HasObject(JsonElement properties, string name, (string Name, string Type)[] required) =>
        properties.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object &&
        (!field.TryGetProperty("type", out var type) || type.ValueKind == JsonValueKind.String && type.GetString() == "object") &&
        field.TryGetProperty("properties", out var children) && children.ValueKind == JsonValueKind.Object &&
        required.All(child => HasType(children, child.Name, child.Type));
}
