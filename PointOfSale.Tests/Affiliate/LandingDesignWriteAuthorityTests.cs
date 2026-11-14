using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Tests.Affiliate;

public sealed class LandingDesignWriteAuthorityTests
{
    [Theory]
    [InlineData("anonymous")]
    [InlineData("missing-user")]
    [InlineData("foreign-account")]
    [InlineData("missing-session")]
    [InlineData("invalid-time")]
    [InlineData("negative-time")]
    [InlineData("overflow-time")]
    public async Task WriteRejectsUnboundActorBeforeAnyProviderCommand(string violation)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var account = Guid.NewGuid();
        var claims = new List<Claim> {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("affiliate_account_id", account.ToString()), new("affiliate_session", "fixture"),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };
        if (violation == "missing-user") claims.RemoveAll(c => c.Type == ClaimTypes.NameIdentifier);
        if (violation == "foreign-account") account = Guid.NewGuid();
        if (violation == "missing-session") claims.RemoveAll(c => c.Type == "affiliate_session");
        if (violation.EndsWith("time", StringComparison.Ordinal))
        {
            claims.RemoveAll(c => c.Type == "iat");
            claims.Add(new("iat", violation == "negative-time" ? "-1" : violation == "overflow-time" ? long.MaxValue.ToString() : "not-a-time"));
        }
        var actor = new ClaimsPrincipal(new ClaimsIdentity(claims, violation == "anonymous" ? null : "AffiliateJwt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AffiliateLandingPageDesignStore.SaveAsync(
            db, account, Guid.NewGuid(), Guid.NewGuid(), "Classic:v1", actor, default));
        Assert.Empty(await db.AffiliateLandingPageDesigns.IgnoreQueryFilters().ToListAsync());
    }
}
