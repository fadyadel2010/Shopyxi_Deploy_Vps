namespace PointOfSale.Tests.Infrastructure;

/// <summary>No registry, connection-string, catalog or database dependencies.</summary>
internal static class PrimaryOnlyTestBoundary
{
    internal static void RejectDisposableHarness() => RejectDisposableHarness(
        Environment.GetEnvironmentVariable("AFFILIATE_PRIMARY_DEVELOPMENT_TESTS") == "1");

    internal static void RejectDisposableHarness(bool primaryOnly)
    {
        if (primaryOnly)
            throw new InvalidOperationException("PRIMARY_ONLY_TEST_BOUNDARY: TestDatabaseScope cannot create or reset an alternate database during a primary-only certification.");
    }
}
