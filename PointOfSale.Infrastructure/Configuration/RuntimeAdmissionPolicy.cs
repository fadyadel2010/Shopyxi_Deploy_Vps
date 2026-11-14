using Microsoft.Extensions.Configuration;

namespace PointOfSale.Infrastructure.Configuration;

/// <summary>An immutable startup decision, independent of test-host switches.</summary>
public sealed record RuntimeAdmissionPolicy(bool PrivateInternalOnly)
{
    public bool BusinessProcessingEnabled => !PrivateInternalOnly;

    public static RuntimeAdmissionPolicy Resolve(IConfiguration configuration)
    {
        var raw = configuration["RuntimeAdmission:PrivateInternalOnly"];
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Runtime admission must be explicitly configured before startup.");
        if (!bool.TryParse(raw, out var privateInternalOnly))
            throw new InvalidOperationException("Runtime admission requires an explicit valid boolean.");
        if (privateInternalOnly &&
            !string.Equals(configuration["MongoDB:ValidateIndexesOnly"], "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Private internal admission requires MongoDB validate-only mode.");
        return new(privateInternalOnly);
    }
}
