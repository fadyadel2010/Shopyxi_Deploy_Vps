#if DEBUG
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Moq;
using PointOfSale.API.Development;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Affiliate;

public class AffiliateP53dPayoutEvidenceTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    [InlineData("Development")]
    public async Task DisabledAdapterRejectsBeforeDatabaseOrExternalAccess(string runtime)
    {
        var env = Mock.Of<IWebHostEnvironment>(e => e.EnvironmentName == runtime &&
            e.ContentRootPath == "C:/Users/fadya/AppData/Local/Temp/opencode/p53d-nonexistent/API");
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().Options);
        var verifier = new AffiliateP53dPayoutEvidence(db, env);
        var fact = AffiliatePayoutTransferEvidence.Create(AffiliateP53dPayoutEvidence.StoreId,
            "AFFILIATE_PAYOUT", AffiliateP53dPayoutEvidence.Reference(Guid.NewGuid()), 100m, "EGP", DateTime.UtcNow,
            AffiliateP53dPayoutEvidence.Source, new string('A', 64));
        Assert.False(verifier.IsEnabled);
        Assert.False(await verifier.VerifyAsync(fact, Guid.NewGuid(), AffiliateP53dPayoutEvidence.StoreId, 100m, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.RegisterLocalFactAsync(Guid.NewGuid(), default));
        // No provider is configured: any attempted database access above would fail.
    }
}
#endif
