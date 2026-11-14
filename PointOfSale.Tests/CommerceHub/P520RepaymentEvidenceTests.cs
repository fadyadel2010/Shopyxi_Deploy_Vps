using PointOfSale.API.Services.CommerceHub.Finance;
using Xunit;

namespace PointOfSale.Tests.CommerceHub;

public class P520RepaymentEvidenceTests
{
    [Theory]
    [InlineData("Production", true, "P520D-20261028-E2E01-V1", false)]
    [InlineData("Development", false, "P520D-20261028-E2E01-V1", false)]
    [InlineData("Development", true, "Another vendor", false)]
    [InlineData("Development", true, "P520D-20261028-E2E01-V1", true)]
    public void SyntheticRepaymentRequiresEnabledDevelopmentAndRunOwnership(string environment, bool enabled, string vendor, bool accepted) =>
        Assert.Equal(accepted, DevelopmentRepaymentEvidencePolicy.CanAccept(environment, enabled, vendor, "DEV-SIM:P520D-20261028-E2E01-J05"));
}
