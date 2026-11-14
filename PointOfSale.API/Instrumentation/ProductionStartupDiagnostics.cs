using System.Text.Json;

namespace PointOfSale.API.Instrumentation;

/// <summary>Last-resort boundary, including failures before logging/DI exists.</summary>
public static class ProductionStartupDiagnostics
{
    public static void WriteFailure(Exception exception, string stage, TextWriter writer)
    {
        // Neither exception messages nor inner exceptions are safe here: binders,
        // JSON readers and TLS/file parsers may include the rejected value/path.
        var category = exception switch
        {
            Microsoft.Extensions.Options.OptionsValidationException => "options-validation",
            System.Security.Cryptography.CryptographicException => "certificate",
            IOException => "configuration-io",
            FormatException or ArgumentException => "configuration-format",
            _ => "startup"
        };
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            EventId = "PRODUCTION_STARTUP_FAILED",
            Stage = stage is "configuration" or "services" or "host-build" or "host-run" ? stage : "startup",
            Category = category,
            Message = "Startup failed. Check the configuration contract, mounted credentials and trust/storage prerequisites. Values and paths are not reported."
        }));
    }
}
