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

namespace PointOfSale.Tests.Affiliate;

/// <summary>One-shot identity preparation only; no invented financial eligibility.</summary>
public sealed class AffiliateP53eIdentityPreparationTests
{
    [PrimaryAffiliateFact]
    public async Task PrepareOnly_WhenExplicitlyRequested_CanonicalRegistrationAndTokenConsumption()
    {
        if (Environment.GetEnvironmentVariable("P53E_PREPARE_IDENTITIES") != "1") return;
        var manifest = Path.Combine(AffiliateP53ePrimarySupport.EvidenceDirectory, "identities.json");
        Assert.False(File.Exists(manifest), "Retained task identities already exist; never reseed.");
        var result = new List<object>();
        foreach (var name in new[] { "rollback", "race" })
        {
            await using var db = await AffiliateP53ePrimarySupport.Open();
            var mail = new LocalMail();
            var config = AffiliateP53ePrimarySupport.Config;
            var controller = new AffiliateAuthController(db, null!, Options.Create(new AffiliateJwtSettings()),
                new PasswordHasher<AffiliateUser>(), mail, config, NullLogger<AffiliateAuthController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            var email = $"affiliate.p53e.{name}.20261023@example.test";
            var registered = Assert.IsType<OkObjectResult>(await controller.Register(new AffiliateRegisterDto
            { Email = email, Password = "P53eNoTransfer!2026", DisplayName = $"P53E {name} SIMULATED NO TRANSFER" }, default));
            var response = Assert.IsType<AffiliateAuthResponse>(registered.Value);
            var token = Uri.UnescapeDataString(Regex.Match(mail.Text!, @"token=([^\s<>]+)").Groups[1].Value);
            Assert.NotEmpty(token);
            Assert.IsType<OkObjectResult>(await controller.VerifyEmail(new AffiliateVerifyEmailDto { Token = token }, default));
            var identity = await db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Email == email);
            Assert.True(identity.EmailVerified);
            result.Add(new { name, email, affiliateId = response.AffiliateAccountId, userId = identity.Id, referralCode = response.ReferralCode });
        }
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class LocalMail : IEmailSender
    {
        internal string? Text;
        public Task SendEmailAsync(string to, string subject, string htmlBody, string textBody)
        {
            Assert.StartsWith("affiliate.p53e.", to); Assert.EndsWith(".20261023@example.test", to);
            Text = textBody; return Task.CompletedTask;
        }
    }
}
