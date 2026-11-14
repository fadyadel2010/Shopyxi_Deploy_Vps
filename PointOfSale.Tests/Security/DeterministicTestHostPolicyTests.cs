using PointOfSale.API.Configuration;

namespace PointOfSale.Tests.Security;

public sealed class DeterministicTestHostPolicyTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    [InlineData("Test")]
    [InlineData("Testing")]
    public void ExplicitLocalTestEnvironments_CanIsolateWorkers(string environment)
    {
        Assert.True(DeterministicTestHostPolicy.Resolve(environment, "1"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    [InlineData("Unknown")]
    [InlineData("")]
    public void DeployedOrUnknownEnvironments_RejectWorkerSuppression(string environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => DeterministicTestHostPolicy.Resolve(environment, "1"));
        Assert.Contains("test-only switch", error.Message);
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "0")]
    [InlineData("Production", "true")]
    [InlineData("QA", null)]
    [InlineData("Development", null)]
    [InlineData("Testing", "0")]
    [InlineData("Test", " 1 ")]
    public void WithoutExactOptIn_NormalWorkerRegistrationRemainsEnabled(string environment, string? value)
    {
        Assert.False(DeterministicTestHostPolicy.Resolve(environment, value));
    }
}
