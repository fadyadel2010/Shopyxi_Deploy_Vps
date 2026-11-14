using PointOfSale.API.Development.CommerceHub;
using Xunit;

namespace PointOfSale.Tests.CommerceHub;

public class P520DevelopmentMailboxTests
{
    [Theory]
    [InlineData("Development", true, "p520d-20261028-e2e01-c1@example.test", true)]
    [InlineData("Development", false, "p520d-20261028-e2e01-c1@example.test", false)]
    [InlineData("Production", true, "p520d-20261028-e2e01-c1@example.test", false)]
    [InlineData("Staging", true, "p520d-20261028-e2e01-c1@example.test", false)]
    [InlineData("Development", true, "p520d-other-c1@example.test", false)]
    [InlineData("Development", true, "p520d-20261028-e2e01-c1@real.example", false)]
    [InlineData("Development", true, "p520d-20261028-e2e01-../c1@example.test", false)]
    [InlineData("Development", true, "p520d-20261028-e2e01-c1@evil@example.test", false)]
    public void CaptureRequiresDevelopmentOptInAndExactSafeRunIdentity(
        string environment, bool enabled, string recipient, bool expected) =>
        Assert.Equal(expected, P520DevelopmentMailbox.CanCapture(environment, enabled, recipient));
}
