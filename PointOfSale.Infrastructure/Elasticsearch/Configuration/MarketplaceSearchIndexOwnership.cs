namespace PointOfSale.Infrastructure.Elasticsearch.Configuration;

/// <summary>
/// Lexical namespace admission only, not physical/alias ownership or a write fence.
/// Other owners must not bootstrap Marketplace generations or future retained names.
/// </summary>
internal static class MarketplaceSearchIndexOwnership
{
    internal static bool IsOtherOwnerIndexName(string? name)
    {
        // Accept explicit index/alias names, never selectors, URL paths, escapes or
        // remote-cluster expressions. Do not normalize ambiguous input into authority.
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." ||
            name[0] is '-' or '_' or '+' ||
            name.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            return false;

        var legacy = MarketplaceServiceIndexConfiguration.IndexName;
        return !string.Equals(name, legacy, StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith(legacy + "_", StringComparison.OrdinalIgnoreCase);
    }
}
