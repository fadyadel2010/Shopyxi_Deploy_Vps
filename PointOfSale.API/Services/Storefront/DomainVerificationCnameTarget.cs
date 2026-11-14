namespace PointOfSale.API.Services.Storefront;

/// <summary>One contract for customer DNS instructions and the verifier.</summary>
internal static class DomainVerificationCnameTarget
{
    internal static string Create(string? verificationToken)
    {
        var token = verificationToken?.Trim().TrimEnd('.') ?? string.Empty;
        // Existing explicit targets remain verifiable; newly generated targets
        // always use the actual registered platform domain.
        if (token.Contains('.', StringComparison.Ordinal))
            return token.ToLowerInvariant();

        const string prefix = "shopixy-domain-verify-";
        if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            token = token[prefix.Length..];
        var label = new string(token.ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' ? ch : '-')
            .ToArray()).Trim('-');
        if (label.Length > 48)
            label = label[..48].Trim('-');
        if (string.IsNullOrWhiteSpace(label))
            label = "token";
        return $"verify-{label}.domains.shopyxi.com";
    }
}
