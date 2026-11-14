using System.Runtime.CompilerServices;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Rejection-boundary tests, NOT PostgreSQL business certification.</summary>
public sealed class AffiliatePrimaryOnlyBoundaryTests
{
    [Fact]
    public void SelectedMode_RejectsWithoutAnyInfrastructureDependency()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PrimaryOnlyTestBoundary.RejectDisposableHarness(true));
        Assert.StartsWith("PRIMARY_ONLY_TEST_BOUNDARY:", error.Message);
        PrimaryOnlyTestBoundary.RejectDisposableHarness(false);
    }

    [PrimaryAffiliateFact]
    public void SyntheticRoute_DirectOpenRejectsBeforeRegistryInitialization()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestDatabaseScope.Open("AffiliateSyntheticBalanceIsolationTests.cs"));
        Assert.StartsWith("PRIMARY_ONLY_TEST_BOUNDARY:", error.Message);
    }

    [PrimaryAffiliateFact]
    public void EmailRoute_TryCreateScopeRejectsRatherThanSilentlyPassingFixture()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RealPostgresTestSupport.TryCreateScope(out _, out _, out _));
        Assert.StartsWith("PRIMARY_ONLY_TEST_BOUNDARY:", error.Message);
    }

    [Fact]
    public void BothLegacyMethods_StillEnterTheCoveredGuardedRoutes()
    {
        var root = TestConfiguration.GetSolutionRoot();
        var synthetic = File.ReadAllText(Path.Combine(root, "PointOfSale.Tests/Affiliate/AffiliateSyntheticBalanceIsolationTests.cs"));
        var email = File.ReadAllText(Path.Combine(root, "PointOfSale.Tests/Affiliate/AffiliateEmailVerificationLifecycleTests.cs"));
        Assert.Contains("Postgres_ConcurrentSyntheticRejectionsAndLegitimateReservations_AreAtomic()\n    {\n        await using var database = TestDatabaseScope.Open();", synthetic.Replace("\r\n", "\n"));
        Assert.Contains("Postgres_FreshToken_TenantFilteredRequiredIncludeDropsRow_AndConditionalClaimArbitratesOnce()\n    {\n        if (!RealPostgresTestSupport.TryCreateScope", email.Replace("\r\n", "\n"));
        var scope = File.ReadAllText(Path.Combine(root, "PointOfSale.Tests/Infrastructure/TestDatabaseScope.cs"));
        Assert.Contains("PrimaryOnlyTestBoundary.RejectDisposableHarness();\n        return OpenDisposable(callerFilePath);", scope.Replace("\r\n", "\n"));
        var initializer = typeof(TestDatabaseScope).GetMethod("OpenDisposable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.True(initializer.MethodImplementationFlags.HasFlag(System.Reflection.MethodImplAttributes.NoInlining));
    }
}
