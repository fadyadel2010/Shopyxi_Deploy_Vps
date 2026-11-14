using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;

namespace PointOfSale.API.Instrumentation;

/// <summary>Last-mile allowlist before trace exporters, including automatic instrumentation.</summary>
public sealed class ProductionTraceProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> NumericTags = new(StringComparer.Ordinal)
    {
        "http.response.status_code", "http.status_code", "http.request.duration_ms",
        "db.client.operation.duration", "retry.count"
    };
    public override void OnEnd(Activity activity)
    {
        // DisplayName/status descriptions, baggage and events are free-form data too.
        activity.DisplayName = activity.Kind.ToString();
        activity.SetStatus(activity.Status);
        foreach (var tag in activity.TagObjects.ToArray())
            if (!NumericTags.Contains(tag.Key) || tag.Value is not (int or long or double or float))
                activity.SetTag(tag.Key, null);
        foreach (var item in activity.Baggage.ToArray()) activity.SetBaggage(item.Key, null);
        // Activity events/links cannot be removed by the public Activity API. The
        // exporter is wrapped below so raw event payloads never reach it.
    }
}

/// <summary>Fail closed only for spans with unremovable payload channels, not all diagnostics.</summary>
// Inherit the pinned exporter so the SDK assigns its ParentProvider/resource.
// Composing an unregistered inner OtlpTraceExporter loses service metadata.
public sealed class ProductionTraceExporter(OtlpExporterOptions options) : OtlpTraceExporter(options)
{
    public override ExportResult Export(in Batch<Activity> batch)
    {
        var safe = new List<Activity>();
        foreach (var activity in batch)
            if (!activity.Events.Any() && !activity.Links.Any()) safe.Add(activity);
        if (safe.Count == 0) return ExportResult.Success;
        using var filtered = new Batch<Activity>(safe.ToArray(), safe.Count);
        return base.Export(filtered);
    }
}
