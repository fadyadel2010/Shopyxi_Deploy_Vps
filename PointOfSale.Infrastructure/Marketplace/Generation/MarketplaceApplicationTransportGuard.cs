using System.Net;
using System.Text.Json;
using PointOfSale.Infrastructure.Elasticsearch.Configuration;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Shared application clients cannot mutate a physical Generation target, including
/// via bulk, reindex or an alternate alias. Generation uses its dedicated transport.
/// This is an application boundary, not a substitute for provider role isolation.
/// </summary>
internal sealed class MarketplaceApplicationTransportGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly SemaphoreSlim mutations = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Exact provider origin required.");
        var path = uri.AbsolutePath;
        if (IsRead(request.Method, path)) return await base.SendAsync(request, ct);
        await mutations.WaitAsync(ct);
        try
        {
            var targets = await TargetsAsync(request, ct);
            foreach (var target in targets)
            {
                using var inspect = new HttpRequestMessage(HttpMethod.Get, new Uri(uri.GetLeftPart(UriPartial.Authority) + "/" + target));
                inspect.Headers.Authorization = request.Headers.Authorization;
                using var reply = await base.SendAsync(inspect, ct);
                if (reply.StatusCode == HttpStatusCode.NotFound)
                {
                    // An alternate alias must not be auto-created into Marketplace.
                    // Exact names are the only creation selectors admitted here.
                    RequireTarget(target);
                    continue;
                }
                if (!reply.IsSuccessStatusCode) throw new InvalidOperationException("Physical target ownership inspection failed.");
                using var metadata = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
                if (metadata.RootElement.ValueKind != JsonValueKind.Object || !metadata.RootElement.EnumerateObject().Any())
                    throw new InvalidDataException("Physical target ownership unavailable.");
                foreach (var physical in metadata.RootElement.EnumerateObject())
                {
                    RequireTarget(physical.Name);
                    if (physical.Value.TryGetProperty("settings", out var settings) && settings.TryGetProperty("index", out var indexSettings))
                        RejectPipelines(indexSettings);
                    if (target == "marketplace_services" && physical.Name != target)
                        throw new InvalidOperationException("Legacy Marketplace writes require the exact legacy physical index, never an alias.");
                }
            }
            return await base.SendAsync(request, ct);
        }
        finally { mutations.Release(); }
    }

    private static bool IsRead(HttpMethod method, string path) => method == HttpMethod.Get || method == HttpMethod.Head ||
        (method == HttpMethod.Delete && path == "/_search/scroll") ||
        (method == HttpMethod.Post && (path.EndsWith("/_search", StringComparison.Ordinal) || path == "/_search/scroll" ||
            path.EndsWith("/_count", StringComparison.Ordinal) || path.EndsWith("/_mget", StringComparison.Ordinal) ||
            path.EndsWith("/_msearch", StringComparison.Ordinal) || path.EndsWith("/_validate/query", StringComparison.Ordinal)));

    private static async Task<HashSet<string>> TargetsAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.Contains('%') || path.Contains('\\')) throw new InvalidOperationException("Encoded provider mutation selectors rejected.");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Uri.UnescapeDataString(request.RequestUri.Query).Contains("pipeline=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Shared mutations cannot select ingest pipelines.");
        if (segments.Length == 0) throw new InvalidOperationException("Cluster mutation is not an application write.");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        string? defaultTarget = segments[0].StartsWith('_') ? null : segments[0];
        if (defaultTarget is not null) { RequireTarget(defaultTarget); targets.Add(defaultTarget); }
        if (segments[^1] == "_bulk")
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            using var lines = new StringReader(body);
            string? line;
            while ((line = lines.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var action = JsonDocument.Parse(line);
                var commands = action.RootElement.EnumerateObject().ToArray();
                if (commands.Length != 1 || commands[0].Name is not ("index" or "create" or "update" or "delete"))
                    throw new InvalidDataException("Unknown bulk mutation action.");
                var target = commands[0].Value.TryGetProperty("_index", out var index) ? index.GetString() : defaultTarget;
                if (commands[0].Value.TryGetProperty("pipeline", out _))
                    throw new InvalidOperationException("Bulk ingest pipelines can redirect physical writes.");
                RequireTarget(target); targets.Add(target!);
                if (commands[0].Name != "delete" && lines.ReadLine() is null) throw new InvalidDataException("Incomplete bulk action.");
            }
        }
        else if (segments[0] is "_aliases" or "_reindex")
        {
            using var body = JsonDocument.Parse(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct));
            if (segments[0] == "_reindex" && (body.RootElement.TryGetProperty("script", out _) ||
                body.RootElement.GetProperty("source").TryGetProperty("remote", out _) ||
                body.RootElement.GetProperty("dest").TryGetProperty("pipeline", out _)))
                throw new InvalidOperationException("Reindex may not redirect an admitted physical target.");
            CollectSelectors(body.RootElement, targets);
            if (targets.Count == 0) throw new InvalidDataException("Explicit lifecycle selectors required.");
        }
        else if (defaultTarget is null)
            throw new InvalidOperationException("Unreviewed cluster mutation rejected by shared application transport.");
        else if (segments.Length == 1 && request.Content is not null && request.Method is not null && request.Method != HttpMethod.Delete)
        {
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("aliases", out var aliases))
                foreach (var alias in aliases.EnumerateObject()) { RequireTarget(alias.Name); targets.Add(alias.Name); }
            if (body.RootElement.TryGetProperty("settings", out var settings))
            {
                RejectPipelines(settings);
                if (settings.TryGetProperty("index", out var nested)) RejectPipelines(nested);
            }
        }
        else if (segments.Length > 1 && segments[1] is "_alias" or "_aliases")
        {
            if (segments.Length != 3) throw new InvalidOperationException("Explicit alias required.");
            RequireTarget(segments[2]); targets.Add(segments[2]);
        }
        return targets;
    }

    private static void CollectSelectors(JsonElement value, HashSet<string> targets)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "index" or "indices" or "alias" or "aliases")
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                        foreach (var name in property.Value.EnumerateArray()) { RequireTarget(name.GetString()); targets.Add(name.GetString()!); }
                    else { RequireTarget(property.Value.GetString()); targets.Add(property.Value.GetString()!); }
                }
                else CollectSelectors(property.Value, targets);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CollectSelectors(item, targets);
    }

    private static void RequireTarget(string? target)
    {
        if (target != "marketplace_services" && !MarketplaceSearchIndexOwnership.IsOtherOwnerIndexName(target))
            throw new InvalidOperationException("Shared application transport cannot mutate Generation topology.");
    }

    private static void RejectPipelines(JsonElement settings)
    {
        foreach (var name in new[] { "default_pipeline", "final_pipeline", "index.default_pipeline", "index.final_pipeline" })
            if (settings.TryGetProperty(name, out var pipeline) && (pipeline.ValueKind != JsonValueKind.String || pipeline.GetString() != "_none"))
                throw new InvalidOperationException("Ingest pipeline can redirect a physical mutation.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) mutations.Dispose();
        base.Dispose(disposing);
    }
}
