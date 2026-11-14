using PointOfSale.API.Services.CommerceHub;
using Xunit;

namespace PointOfSale.Tests.CommerceHub;

public class P520PayoutSyntheticEvidenceTests
{
    [Theory]
    [InlineData("DEV-SIM:SETTLEMENT:P520D-20261028-E2E01")]
    [InlineData(" dev-sim:ONLINE:payment ")]
    [InlineData("DEV-SIM:manual-attestation")]
    public void NormalExternalPayoutAdmissionRejectsSyntheticEvidence(string reference)
    {
        var exception = Assert.Throws<CommerceHubCheckoutException>(() => PayoutSyntheticEvidencePolicy.RejectExternalEvidence(reference));
        Assert.Equal("PAYOUT_DEVELOPMENT_SIMULATION_NOT_TRANSFERABLE", exception.Code);
    }

    [Fact]
    public void OrdinaryReferenceIsNotSyntheticCertification() => PayoutSyntheticEvidencePolicy.RejectExternalEvidence(null, "provider-reference-123");
}
