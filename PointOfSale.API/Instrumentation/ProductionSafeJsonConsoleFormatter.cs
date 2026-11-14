using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace PointOfSale.API.Instrumentation;

/// <summary>
/// Fail-closed Production console boundary. Unstructured messages, state, scopes,
/// exception text and stacks are not a safe secret-classification interface.
/// Keep event/category/level/time only; use approved metrics for richer operations.
/// Development/Test retain the existing detailed feed-token-redacting formatter.
/// </summary>
public sealed class ProductionSafeJsonConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "productionSafeJson";
    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopes, TextWriter writer)
    {
        // Deliberately do not invoke entry.Formatter, state.ToString or scope renderers.
        var operationalFields = new Dictionary<string, double>();
        if (entry.State is IReadOnlyList<KeyValuePair<string, object?>> fields)
        {
            foreach (var field in fields)
            {
                // Only numeric, bounded, explicitly named operational fields.
                // Never allow string parsing, arbitrary names or identifiers.
                if (field.Key is not ("StatusCode" or "LatencyMs" or "DurationMs" or "AttemptCount")) continue;
                double? value = field.Value switch
                {
                    int number => number,
                    long number => number,
                    double number => number,
                    float number => number,
                    _ => null
                };
                if (value is { } numberValue && double.IsFinite(numberValue) && numberValue >= 0 &&
                    (field.Key != "StatusCode" || numberValue is >= 100 and <= 599))
                    operationalFields[field.Key] = numberValue;
            }
        }
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventId = entry.EventId.Id,
            LogLevel = entry.LogLevel.ToString(),
            Category = entry.Category,
            Message = "Production event; free-form diagnostic payload omitted.",
            ExceptionCategory = entry.Exception?.GetType().Name,
            OperationalFields = operationalFields
        }));
    }
}
