using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.API.Controllers;
using PointOfSale.API.Models;
using PointOfSale.API.Services.Auth;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

/// <summary>One-shot, primary-only normal registration/verification. No financial seeding.</summary>
public sealed class AffiliateFinalIdentityPreparationTests
{
    [PrimaryAffiliateFact]
    public async Task PrepareExplicitTaskIdentity_ConsumeRealVerificationToken_NoFinancialFixtureWrites()
    {
        if (Environment.GetEnvironmentVariable("AFFILIATE_FINAL_PREPARE_IDENTITY") != "1") return;
        var manifest = Path.Combine(TestConfiguration.GetSolutionRoot(), ".ai/reports/affiliate-final-identity.json");
        Assert.False(File.Exists(manifest), "Task identity already retained; never reseed.");
        var password = Environment.GetEnvironmentVariable("AFFILIATE_FINAL_TEST_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        await using var db = await AffiliateP53ePrimarySupport.Open("Affiliate-final-identity-primary-only");
        const string email = "affiliate.final.20261026@example.test";
        Assert.False(await db.AffiliateUsers.IgnoreQueryFilters().AnyAsync(u => u.Email == email));
        var mail = new LocalMail();
        var controller = new AffiliateAuthController(db, null!, Options.Create(new AffiliateJwtSettings()),
            new PasswordHasher<AffiliateUser>(), mail, AffiliateP53ePrimarySupport.Config, NullLogger<AffiliateAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var registered = Assert.IsType<OkObjectResult>(await controller.Register(new AffiliateRegisterDto
        { Email = email, Password = password!, DisplayName = "AFFILIATE FINAL 850 SIMULATED NO TRANSFER" }, default));
        var response = Assert.IsType<AffiliateAuthResponse>(registered.Value);
        var token = Uri.UnescapeDataString(Regex.Match(mail.Text!, @"token=([^\s<>]+)").Groups[1].Value);
        Assert.NotEmpty(token);
        Assert.IsType<OkObjectResult>(await controller.VerifyEmail(new AffiliateVerifyEmailDto { Token = token }, default));
        var identity = await db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.True(identity.EmailVerified);
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { email, affiliateId = response.AffiliateAccountId,
            userId = identity.Id, referralCode = response.ReferralCode, simulation = "NO TRANSFER / NO PHYSICAL DELIVERY" }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class LocalMail : IEmailSender
    {
        internal string? Text;
        public Task SendEmailAsync(string to, string subject, string htmlBody, string textBody)
        {
            Assert.Equal("affiliate.final.20261026@example.test", to);
            Text = textBody; return Task.CompletedTask;
        }
    }
}
