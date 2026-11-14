using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;

namespace PointOfSale.Tests.Checkout;

public sealed class FixtureResumeSafetyTests
{
    private static Tenant Fixture()
    {
        var id = Guid.NewGuid();
        return new Tenant { Id = id, Slug = "p515-" + id.ToString("N"),
            Name = "P515 INTERNAL NO DELIVERY NO EXTERNAL PAYMENT " + id.ToString("N"),
            Status = TenantStatus.Active, IsActive = true };
    }

    [Fact]
    public void ActiveOwnedFixture_CanResume()
    {
        var tenant = Fixture();
        P515PublicCheckoutTests.ValidateResumeTenant(tenant, tenant.Id, tenant.Slug);
    }

    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Deleted)]
    [InlineData(TenantStatus.PendingSetup)]
    public void NonOperationalFixture_CannotResume_EvenWithStaleActiveMirror(TenantStatus status)
    {
        var tenant = Fixture();
        tenant.Status = status;
        Assert.Throws<InvalidOperationException>(() => P515PublicCheckoutTests.ValidateResumeTenant(tenant, tenant.Id, tenant.Slug));
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("deleted-at")]
    [InlineData("wrong-label")]
    [InlineData("wrong-slug")]
    [InlineData("wrong-id")]
    public void RevokedOrForeignFixture_CannotResume(string scenario)
    {
        var tenant = Fixture();
        var id = tenant.Id;
        var slug = tenant.Slug;
        switch (scenario)
        {
            case "inactive": tenant.IsActive = false; break;
            case "deleted-at": tenant.DeletedAtUtc = DateTime.UtcNow; break;
            case "wrong-label": tenant.Name = "Ordinary customer tenant"; break;
            case "wrong-slug": tenant.Slug = "ordinary-store"; break;
            case "wrong-id": tenant.Id = Guid.NewGuid(); break;
        }
        Assert.Throws<InvalidOperationException>(() => P515PublicCheckoutTests.ValidateResumeTenant(tenant, id, slug));
    }
}
