using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using OpenSearch.Client;
using OpenSearch.Net;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Independently observes a pinned cluster and exact physical generation. Internal,
/// unregistered, read-only. An observation is NOT atomic remote write authority:
/// same-name recreation after inspection still requires server-role/lifecycle fencing.
/// </summary>
internal static class MarketplacePhysicalAdmission
{
    internal sealed record Expected(Guid Journal, Guid Generation, string ClusterUuid, string PhysicalUuid, string Fingerprint)
    {
        internal string PhysicalName => "marketplace_services_g_" + Generation.ToString("N");
    }
    internal sealed record Observation(string PhysicalName, string ClusterUuid, string PhysicalUuid, string Fingerprint);

    internal static async Task<Observation> InspectAsync(IOpenSearchClient client, Expected expected, CancellationToken ct = default)
    {
        Validate(expected); ct.ThrowIfCancellationRequested();
        var cluster = await client.LowLevel.DoRequestAsync<StringResponse>(global::OpenSearch.Net.HttpMethod.GET, "/", ct);
        if (!cluster.Success || string.IsNullOrWhiteSpace(cluster.Body)) throw new InvalidDataException("Generation cluster observation unavailable.");
        using (var identity = JsonDocument.Parse(cluster.Body))
        {
            Unique(identity.RootElement);
            if (Text(identity.RootElement, "cluster_uuid") != expected.ClusterUuid)
                throw new InvalidDataException("Generation cluster lineage changed.");
        }
        var physical = await client.LowLevel.DoRequestAsync<StringResponse>(global::OpenSearch.Net.HttpMethod.GET, "/" + expected.PhysicalName, ct);
        if (!physical.Success || string.IsNullOrWhiteSpace(physical.Body)) throw new InvalidDataException("Generation physical observation unavailable.");
        return InspectResponses(expected, cluster.Body, physical.Body);
    }

    internal static Observation InspectResponses(Expected expected, string clusterJson, string physicalJson)
    {
        Validate(expected);
        using var cluster = JsonDocument.Parse(clusterJson); using var physical = JsonDocument.Parse(physicalJson);
        Unique(cluster.RootElement); Unique(physical.RootElement);
        if (Text(cluster.RootElement, "cluster_uuid") != expected.ClusterUuid)
            throw new InvalidDataException("Generation cluster lineage changed.");
        var root = physical.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty(expected.PhysicalName, out var index))
            throw new InvalidDataException("Exact physical generation required; aliases/multiple targets rejected.");
        var aliases = Object(index, "aliases");
        if (aliases.EnumerateObject().Any()) throw new InvalidDataException("Private generation must not expose aliases.");
        var settings = Object(Object(index, "settings"), "index");
        if (Text(settings, "uuid") != expected.PhysicalUuid || Text(settings, "provided_name") != expected.PhysicalName)
            throw new InvalidDataException("Generation physical identity changed.");
        if (!int.TryParse(Text(settings, "number_of_shards"), NumberStyles.None, CultureInfo.InvariantCulture, out var shards) || shards < 1 ||
            !int.TryParse(Text(settings, "number_of_replicas"), NumberStyles.None, CultureInfo.InvariantCulture, out var replicas) || replicas < 0)
            throw new InvalidDataException("Generation shard settings malformed.");
        foreach (var pipeline in new[] { "default_pipeline", "final_pipeline" })
            if (settings.TryGetProperty(pipeline, out var value) && (value.ValueKind != JsonValueKind.String || value.GetString() != "_none"))
                throw new InvalidDataException("Unreviewed generation ingest pipeline rejected.");
        if (settings.TryGetProperty("blocks", out var blocks))
        {
            if (blocks.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Generation block settings malformed.");
            foreach (var flag in new[] { "write", "read_only", "read_only_allow_delete", "metadata", "read" })
                if (blocks.TryGetProperty(flag, out var value) && value.ValueKind != JsonValueKind.False &&
                    !(value.ValueKind == JsonValueKind.String && value.GetString() == "false"))
                    throw new InvalidDataException("Blocked generation rejected.");
        }
        var mappings = Object(index, "mappings"); var meta = Object(mappings, "_meta");
        if (Text(meta, "journal") != expected.Journal.ToString("D") || Text(meta, "generation") != expected.Generation.ToString("D") ||
            Text(meta, "schema") != "marketplace-generation-v1")
            throw new InvalidDataException("Generation mapping lineage/schema rejected.");
        var properties = Object(mappings, "properties");
        RequireType(properties, "serviceId", "keyword"); RequireType(properties, "isActive", "boolean");
        var envelope = Object(properties, "__marketplace");
        if (Text(envelope, "dynamic") != "strict") throw new InvalidDataException("Strict generation version envelope required.");
        var fields = Object(envelope, "properties");
        foreach (var field in new[] { "journal", "generation", "digest" }) RequireType(fields, field, "keyword");
        foreach (var field in new[] { "revision", "epoch" }) RequireType(fields, field, "long");
        RequireType(fields, "deleted", "boolean");
        if (mappings.TryGetProperty("_source", out var source) && source.TryGetProperty("enabled", out var enabled) && enabled.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Generation source must remain readable for content verification.");
        var fingerprint = Fingerprint(mappings, settings);
        if (fingerprint != expected.Fingerprint) throw new InvalidDataException("Generation mapping/settings fingerprint changed.");
        return new(expected.PhysicalName, expected.ClusterUuid, expected.PhysicalUuid, fingerprint);
    }

    // The entire mapping and protected settings participate, not a partial field
    // census. Only generated identity/server bookkeeping is excluded from settings.
    internal static string Fingerprint(JsonElement mappings, JsonElement settings)
    {
        Unique(mappings); Unique(settings);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray(); Canonical(writer, mappings); Canonical(writer, settings, true); writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    internal static void RequireCluster(Expected expected, string clusterJson)
    {
        Validate(expected);
        using var cluster = JsonDocument.Parse(clusterJson);
        Unique(cluster.RootElement);
        if (Text(cluster.RootElement, "cluster_uuid") != expected.ClusterUuid)
            throw new InvalidDataException("Generation cluster lineage changed.");
    }

    internal static void Validate(Expected expected)
    {
        if (expected.Journal == Guid.Empty || expected.Generation == Guid.Empty || !Identity(expected.ClusterUuid) || !Identity(expected.PhysicalUuid) ||
            expected.Fingerprint is null || expected.Fingerprint.Length != 64 || expected.Fingerprint.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))
            throw new ArgumentException("Independently pinned generation/cluster/physical/fingerprint required.");
    }
    private static bool Identity(string? value) => value is { Length: > 0 and <= 200 } && value != "_na_" &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static JsonElement Object(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : throw new InvalidDataException("Generation object required: " + name);
    private static string Text(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("Generation string required: " + name);
    private static void RequireType(JsonElement properties, string name, string type)
    {
        if (Text(Object(properties, name), "type") != type) throw new InvalidDataException("Generation field type rejected: " + name);
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate generation observation property.");
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private static void Canonical(Utf8JsonWriter writer, JsonElement value, bool settingsRoot = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (settingsRoot && property.Name is ("uuid" or "provided_name" or "creation_date" or "version")) continue;
                writer.WritePropertyName(property.Name); Canonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
