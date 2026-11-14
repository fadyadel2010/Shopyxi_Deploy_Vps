using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateP54LandingDesignTests
{
    [Theory]
    [InlineData("Classic:v1")]
    [InlineData("Storytelling:v1")]
    [InlineData("MinimalFastCheckout:v1")]
    public void Domain_AllThreeDesignsAreRealPreferences(string template)
    {
        var design = new AffiliateLandingPageDesign();
        design.SelectTemplate(template);
        Assert.Equal(template, design.TemplateType);
        Assert.True(design.UpdatedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        Assert.Throws<ArgumentException>(() => design.SelectTemplate("Invented:v1"));
        Assert.Equal(template, design.TemplateType);
    }

    [Fact]
    public async Task PrimaryPostgres_SavesAllTemplates_ReadsAfterTrackerReset_IsolatesScope_AndRollsBack()
    {
        // Existing primary records only. Preference changes are rolled back. No business lifecycle is executed.
        await using var db = await AffiliateP53ePrimarySupport.Open("P54-design-preference-only");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var product = await db.Products.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == Guid.Parse("15e044bc-eaa1-c905-31a4-658aac4ad14b"));
        var accountId = await db.AffiliateAccounts.IgnoreQueryFilters().Where(a => a.ReferralCode == "MEMOXP155504").Select(a => a.Id).SingleAsync();
        var user = await db.AffiliateUsers.IgnoreQueryFilters().SingleAsync(u => u.AffiliateAccountId == accountId);
        var actor = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim("affiliate_account_id", accountId.ToString()),
            new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()), new Claim("affiliate_session", user.RefreshToken ?? "")
        }, "AffiliateJwt"));
        var originalDefault = await db.AffiliateProductPolicies.IgnoreQueryFilters().AsNoTracking().Where(p => p.ProductId == product.Id).Select(p => p.TemplateType).SingleAsync();
        foreach (var template in LandingTemplates.Allowed)
        {
            await AffiliateLandingPageDesignStore.SaveAsync(db, accountId, product.TenantId, product.Id, template, actor, default);
            db.ChangeTracker.Clear();
            Assert.Equal(template, await AffiliateLandingPageDesignStore.ReadAsync(db, accountId, product.TenantId, product.Id, default));
            Assert.Null(await AffiliateLandingPageDesignStore.ReadAsync(db, Guid.NewGuid(), product.TenantId, product.Id, default));
            Assert.Null(await AffiliateLandingPageDesignStore.ReadAsync(db, accountId, Guid.NewGuid(), product.Id, default));
            Assert.Null(await AffiliateLandingPageDesignStore.ReadAsync(db, accountId, product.TenantId, Guid.NewGuid(), default));
        }
        Assert.Equal(originalDefault, await db.AffiliateProductPolicies.IgnoreQueryFilters().AsNoTracking().Where(p => p.ProductId == product.Id).Select(p => p.TemplateType).SingleAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => AffiliateLandingPageDesignStore.SaveAsync(db, accountId, product.TenantId, product.Id, "unknown", actor, default));
        await transaction.RollbackAsync();
    }
}
