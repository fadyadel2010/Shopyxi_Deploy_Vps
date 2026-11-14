using PointOfSale.API.Controllers;

namespace PointOfSale.Tests.Architecture;

public sealed class ReleaseDebugDriverIsolationTests
{
    [Fact]
    public void ReleaseApiContainsNoLocalFinancialDriverTypesOrReferences()
    {
        var api = typeof(AuthController).Assembly;
        Assert.Null(api.GetType("PointOfSale.API.Development.AffiliatePhase52LocalDriver", throwOnError: false));
        Assert.DoesNotContain(api.GetTypes(), type => type.Namespace == "PointOfSale.API.Development" &&
            (type.Name.Contains("LocalDriver", StringComparison.Ordinal) || type.Name.Contains("Phase52", StringComparison.Ordinal)));
        // This proves Release binary reachability only. It never starts a driver,
        // application, financial operation, or accesses its named QA database.
    }
}
