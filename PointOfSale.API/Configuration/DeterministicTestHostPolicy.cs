namespace PointOfSale.API.Configuration;

/// <summary>Prevents an inherited integration-test switch from disabling deployed workers.</summary>
public static class DeterministicTestHostPolicy
{
    public static bool Resolve(string environmentName, string? requireRealPostgresTests)
    {
        if (!string.Equals(requireRealPostgresTests, "1", StringComparison.Ordinal))
            return false;

        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase))
            return true;

        // Do not ignore the flag and unexpectedly start workers in a supposed isolated
        // harness either. Misconfigured deployed/test processes must fail before startup.
        throw new InvalidOperationException(
            "REQUIRE_REAL_POSTGRES_TESTS is restricted to Development, Test or Testing. " +
            "Remove the test-only switch before starting a deployed API.");
    }
}
