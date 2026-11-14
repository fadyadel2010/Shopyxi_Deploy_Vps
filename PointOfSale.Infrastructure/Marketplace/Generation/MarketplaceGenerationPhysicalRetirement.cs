using System.Net;
using System.Text.Json;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal sealed record GenerationRetirementPermit(Guid Operation, MarketplacePhysicalAdmission.Expected Target);

/// <summary>
/// Exact physical deletion only. An index name is never reused by this application.
/// Credentials must exclude concurrent index/alias recreation by other principals;
/// OpenSearch does not offer an index-UUID conditional DELETE.
/// </summary>
internal static class MarketplaceGenerationPhysicalRetirement
{
    internal enum Boundary { BeforeDelete, AfterDeleteBeforeObservation, AfterAbsenceBeforePersistence }

    internal static async Task DeleteAsync(MarketplacePhysicalAdmission.Expected expected,
        MarketplaceGenerationTransport transport, CancellationToken ct,
        Action<Boundary>? injectFailure = null, GenerationRetirementPermit? permit = null)
    {
        MarketplacePhysicalAdmission.Validate(expected);
        if (permit is not null && (permit.Operation == Guid.Empty || permit.Target != expected))
            throw new InvalidDataException("Retirement permit cannot rebind the physical target.");
        var cluster = await transport.SendAsync(HttpMethod.Get, "/", null, ct);
        MarketplacePhysicalAdmission.RequireCluster(expected, cluster);
        var absent = await IsAbsentAsync(expected, transport, cluster, ct);
        if (!absent)
        {
            injectFailure?.Invoke(Boundary.BeforeDelete);
            using var response = JsonDocument.Parse(permit is null ?
                await transport.SendAsync(HttpMethod.Delete, "/" + expected.PhysicalName, null, ct) :
                await transport.SendRetirementDeleteAsync(permit, ct));
            if (response.RootElement.ValueKind != JsonValueKind.Object ||
                response.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != response.RootElement.EnumerateObject().Count() ||
                !response.RootElement.TryGetProperty("acknowledged", out var acknowledged) || acknowledged.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("Physical deletion was not acknowledged.");
            injectFailure?.Invoke(Boundary.AfterDeleteBeforeObservation);
        }
        cluster = await transport.SendAsync(HttpMethod.Get, "/", null, ct);
        MarketplacePhysicalAdmission.RequireCluster(expected, cluster);
        if (!await IsAbsentAsync(expected, transport, cluster, ct))
            throw new InvalidOperationException("Physical deletion absence not proven.");
        injectFailure?.Invoke(Boundary.AfterAbsenceBeforePersistence);
    }

    private static async Task<bool> IsAbsentAsync(MarketplacePhysicalAdmission.Expected expected,
        MarketplaceGenerationTransport transport, string cluster, CancellationToken ct)
    {
        try
        {
            var metadata = await transport.SendAsync(HttpMethod.Get, "/" + expected.PhysicalName, null, ct);
            _ = MarketplacePhysicalAdmission.InspectResponses(expected, cluster, metadata);
            return false;
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // Exact-index GET, after cluster identity verification. Authorization,
            // transport failure and every other status remain failures, not absence.
            return true;
        }
    }
}
