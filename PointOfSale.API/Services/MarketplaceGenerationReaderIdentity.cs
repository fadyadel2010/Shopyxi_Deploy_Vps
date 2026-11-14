using System.Security.Claims;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.API.Services;

internal sealed class MarketplaceGenerationReaderIdentity(IHttpContextAccessor http) : IMarketplaceGenerationReaderIdentity
{
    public Guid RequireAuthenticatedReader()
    {
        var principal = http.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var reader) || reader == Guid.Empty)
            throw new InvalidOperationException("Authenticated reader identity required.");
        return reader;
    }
}
