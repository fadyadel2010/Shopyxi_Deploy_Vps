using System.Reflection;
using PointOfSale.API.Services.Storefront;

// Never place this in a namespace that introduces a new member name shadowing a
// domain entity used bare by sibling tests: PointOfSale.Tests.Domain shadowed the
// PointOfSale.Core.Entities.Domain entity, and PointOfSale.Tests.DomainVerification
// would shadow the DomainVerification entity (both observed as CS0118). Sharing the
// existing PointOfSale.Tests.CustomDomains namespace introduces no new member.
namespace PointOfSale.Tests.CustomDomains;

public sealed class CanonicalDomainVerificationTests
{
    [Theory]
    [InlineData("shopixy-domain-verify-abc123", "verify-abc123.domains.shopyxi.com")]
    [InlineData("SHOPIXY-DOMAIN-VERIFY-CNAME-TOKEN", "verify-cname-token.domains.shopyxi.com")]
    [InlineData("cname-token", "verify-cname-token.domains.shopyxi.com")]
    [InlineData(" Verify-Abc.Domains.Shopyxi.Com. ", "verify-abc.domains.shopyxi.com")]
    [InlineData("verify-token.domains.shopixy.com", "verify-token.domains.shopixy.com")]
    [InlineData("", "verify-token.domains.shopyxi.com")]
    public void InstructionsAndVerifierUseIdenticalTargets(string token, string expected)
    {
        Assert.Equal(expected, Target(typeof(DomainReadService), token));
        Assert.Equal(expected, Target(typeof(DomainVerificationService), token));
    }

    [Fact]
    public void GeneratedDnsLabelsStayWithinDnsLimits()
    {
        var token = "shopixy-domain-verify-" + new string('a', 120);
        var target = Target(typeof(DomainReadService), token);
        Assert.Equal(target, Target(typeof(DomainVerificationService), token));
        Assert.InRange(target.Split('.')[0].Length, 1, 63);
        Assert.EndsWith(".domains.shopyxi.com", target);
    }

    [Theory]
    [InlineData("shopyxi.com", true)]
    [InlineData("api.shopyxi.com", true)]
    [InlineData("commercehub.shopyxi.com", true)]
    [InlineData("tenant.shopyxi.com", true)]
    [InlineData("shopixy.com", true)]
    [InlineData("shopyxi.com.evil.test", false)]
    [InlineData("merchant.example.test", false)]
    public void CustomDomainsCannotClaimPlatformAuthority(string hostname, bool expected)
    {
        var method = typeof(DomainWriteService).GetMethod("IsReservedHost", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (bool)method.Invoke(null, [hostname])!);
    }

    private static string Target(Type service, string token) =>
        (string)service.GetMethod("BuildExpectedCnameTarget", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [token])!;
}
