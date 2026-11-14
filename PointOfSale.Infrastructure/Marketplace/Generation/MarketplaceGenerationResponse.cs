using System.Text.Json;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

// Strict server-response verification. Pure parsing is unit-testable but never
// represents provider evidence; callers must retain actual server/role receipts.
internal static class MarketplaceGenerationResponse
{
    internal static void RequireWrite(string json, string physicalName, Guid service)
    {
        using var document = Parse(json); var root = document.RootElement;
        if (Text(root, "_index") != physicalName || Text(root, "_id") != service.ToString("D") ||
            Text(root, "result") is not ("created" or "updated" or "noop") ||
            Number(root, "_seq_no") < 0 || Number(root, "_primary_term") <= 0 ||
            root.TryGetProperty("timed_out", out var timeout) && timeout.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Exact generation write acknowledgement required.");
        if (Text(root, "result") == "noop")
        {
            var shards = Field(root, "_shards");
            if (Number(shards, "total") == 0 && Number(shards, "successful") == 0 && Number(shards, "failed") == 0) return;
        }
        Shards(root);
    }

    internal static void RequireShards(string json)
    {
        using var document = Parse(json); Shards(document.RootElement);
    }

    internal static void RequireCount(string json, long count)
    {
        using var document = Parse(json);
        Shards(document.RootElement);
        if (Number(document.RootElement, "count") != count) throw new InvalidDataException("Generation contains missing or unaccounted documents.");
    }

    internal static void RequireDocuments(string json, MarketplacePhysicalAdmission.Expected expected,
        long revision, IReadOnlyList<DeliveredGenerationRow> rows)
    {
        using var document = Parse(json);
        var docs = Field(document.RootElement, "docs");
        if (docs.ValueKind != JsonValueKind.Array || docs.GetArrayLength() != rows.Count || rows.Select(row => row.ServiceId).Distinct().Count() != rows.Count)
            throw new InvalidDataException("Exact refreshed page coverage required.");
        var wanted = rows.ToDictionary(row => row.ServiceId);
        foreach (var doc in docs.EnumerateArray())
        {
            var rawId = Text(doc, "_id");
            if (!Guid.TryParseExact(rawId, "D", out var id) || rawId != id.ToString("D") || !wanted.Remove(id, out var row) ||
                Text(doc, "_index") != expected.PhysicalName || Field(doc, "found").ValueKind != JsonValueKind.True || doc.TryGetProperty("error", out _))
                throw new InvalidDataException("Refreshed generation document identity/coverage rejected.");
            if (MarketplaceFrozenOutput.RowDigest(id, row.Payload) != row.Digest)
                throw new InvalidDataException("Sealed generation payload digest changed.");
            var source = Field(doc, "_source"); var envelope = Field(source, "__marketplace");
            if (envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Count() != 6 ||
                Text(envelope, "journal") != expected.Journal.ToString("D") || Text(envelope, "generation") != expected.Generation.ToString("D") ||
                Number(envelope, "revision") != revision || Number(envelope, "epoch") != row.Epoch || Text(envelope, "digest") != row.Digest ||
                Field(envelope, "deleted").ValueKind != (row.Payload is null ? JsonValueKind.True : JsonValueKind.False))
                throw new InvalidDataException("Refreshed generation version/tombstone envelope rejected.");
            using var body = Parse(row.Payload ?? JsonSerializer.Serialize(new { serviceId = id, isActive = false }));
            if (source.ValueKind != JsonValueKind.Object || source.EnumerateObject().Count() != body.RootElement.EnumerateObject().Count() + 1 ||
                body.RootElement.TryGetProperty("__marketplace", out _) ||
                body.RootElement.EnumerateObject().Any(property => !source.TryGetProperty(property.Name, out var actual) || !Equivalent(property.Value, actual, ProviderDouble(property.Name))))
                throw new InvalidDataException("Refreshed generation content differs from its immutable output.");
        }
        if (wanted.Count != 0) throw new InvalidDataException("Refreshed page omitted identities.");
    }

    private static void Shards(JsonElement root)
    {
        var shards = Field(root, "_shards");
        var total = Number(shards, "total"); var successful = Number(shards, "successful");
        // A single write may acknowledge primary only while a refresh/count must
        // cover all allocated shards. Requiring total==successful is conservative
        // and deliberately refuses unassigned/partial generations.
        if (total <= 0 || successful != total || Number(shards, "failed") != 0 ||
            root.TryGetProperty("timed_out", out var timeout) && timeout.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Complete successful generation shard acknowledgement required.");
    }

    private static JsonDocument Parse(string json)
    {
        var document = JsonDocument.Parse(json);
        try { Unique(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static JsonElement Field(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        ? value : throw new InvalidDataException("Generation response field missing: " + name);
    private static string Text(JsonElement root, string name) => Field(root, name).ValueKind == JsonValueKind.String
        ? Field(root, name).GetString()! : throw new InvalidDataException("Generation response text required: " + name);
    private static long Number(JsonElement root, string name) => Field(root, name).ValueKind == JsonValueKind.Number && Field(root, name).TryGetInt64(out var number)
        ? number : throw new InvalidDataException("Generation response integer required: " + name);
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate generation response property.");
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private static bool ProviderDouble(string field) => field is "startingPrice" or "averageRating" or "marketingWeightedScore" or "completionRate";
    private static bool Equivalent(JsonElement left, JsonElement right, bool providerDouble = false)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count() &&
                left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out var value) && Equivalent(property.Value, value, ProviderDouble(property.Name))),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second)),
            // Java and .NET can emit different shortest decimal spellings of one
            // IEEE double (e.g. 1E23). Only declared double fields use that equality;
            // counters and the separately checked Int64 envelope remain exact.
            JsonValueKind.Number => providerDouble ?
                left.TryGetDouble(out var x) && right.TryGetDouble(out var y) && double.IsFinite(x) && x == y :
                left.TryGetDecimal(out var a) && right.TryGetDecimal(out var b) && a == b,
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False => true,
            _ => false
        };
    }
}
