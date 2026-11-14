using PointOfSale.Core.Tenancy;

namespace PointOfSale.Tests.Tenancy;

public sealed class StorefrontUrlAuthorityTests
{
    [Fact]
    public void ResolveOrigin_PrefersAndNormalizesTenantBaseUrl()
    {
        var result = StorefrontUrlAuthority.ResolveOrigin(
            " https://custom.example.test/ ",
            "ignored-slug",
            "https://{slug}.example.test");

        Assert.Equal("https://custom.example.test", result);
    }

    [Fact]
    public void ResolveOrigin_UsesCanonicalSlugTemplateFallback()
    {
        var result = StorefrontUrlAuthority.ResolveOrigin(
            null,
            "tenant-store",
            "https://{slug}.example.test/");

        Assert.Equal("https://tenant-store.example.test", result);
    }

    [Theory]
    [InlineData("http://{slug}.localhost:5174", "http://ford.localhost:5174")]
    [InlineData("http://{slug}.192.168.1.5.nip.io:5174", "http://ford.192.168.1.5.nip.io:5174")]
    public void EnvironmentTemplate_PreservesOwningMerchant_NotPortalBrand(string template, string expected)
    {
        Assert.Equal(expected, StorefrontUrlAuthority.ResolveOrigin(null, "ford", template));
        Assert.Equal("https://merchant.example.test", StorefrontUrlAuthority.ResolveOrigin("https://merchant.example.test", "ford", template));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user@example.test")]
    [InlineData("https://example.test/path")]
    [InlineData("https://example.test?query=true")]
    public void NormalizeOrigin_RejectsNonOriginAuthority(string value)
    {
        Assert.Throws<InvalidOperationException>(() => StorefrontUrlAuthority.NormalizeOrigin(value));
    }

    [Fact]
    public void ResolveOrigin_RejectsTemplateWithoutSlugPlaceholder()
    {
        Assert.Throws<InvalidOperationException>(() => StorefrontUrlAuthority.ResolveOrigin(
            null,
            "tenant-store",
            "https://example.test"));
    }

    [Fact]
    public void BuildUrl_ValidatesOriginAndEncodesEachPathSegment()
    {
        var result = StorefrontUrlAuthority.BuildUrl(
            "https://store.example.test/",
            "products",
            "blue shoe/limited");

        Assert.Equal("https://store.example.test/products/blue%20shoe%2Flimited", result);
    }
}
