using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Models;
using PointOfSale.API.Services.Auth;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// No host,Mongo,JWT session or real email. Production controller + disposable PostgreSQL.
public sealed class AffiliateRegistrationProviderTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("duplicate")]
    [InlineData("invalid-email")]
    [InlineData("weak-password")]
    [InlineData("missing-name")]
    [InlineData("mail-failure")]
    [InlineData("save-failure")]
    public async Task RegistrationCommitsGlobalVerificationOnlyOrRollsBackBeforeAnyMail(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(new FailVerificationTokenSave(state == "save-failure")).Options);
        var email = new Mock<IEmailSender>(MockBehavior.Strict); var sent = 0;
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (string recipient, string subject, string html, string plain) => {
                Assert.Null(db.Database.CurrentTransaction);
                await using var observer = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
                var user = Assert.Single(await observer.AffiliateUsers.IgnoreQueryFilters().ToListAsync());
                var account = Assert.Single(await observer.AffiliateAccounts.IgnoreQueryFilters().ToListAsync());
                var token = Assert.Single(await observer.AffiliateEmailVerificationTokens.IgnoreQueryFilters().ToListAsync());
                Assert.Equal(account.Id, user.AffiliateAccountId); Assert.Equal(user.Id, account.UserId); Assert.Equal(user.Id, token.AffiliateUserId);
                Assert.Equal("enrollment@example.invalid", recipient); Assert.False(user.EmailVerified);
                Assert.Null(user.RefreshToken); Assert.Null(token.ConsumedAtUtc); sent++;
                if (state == "mail-failure") throw new InvalidOperationException("Controlled delivery failure");
            });
        var controller = new AffiliateAuthController(db, null!, Options.Create(new AffiliateJwtSettings()), new PasswordHasher<AffiliateUser>(),
            email.Object, new ConfigurationBuilder().Build(), NullLogger<AffiliateAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var request = new AffiliateRegisterDto { Email = state == "invalid-email" ? "invalid" : "  Enrollment@Example.Invalid  ",
            DisplayName = state == "missing-name" ? " " : "Disposable Enrollment", Password = state == "weak-password" ? "weak" : "Disposable-Password!9" };
        if (state == "save-failure")
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Register(request, default));
        else
        {
            var result = await controller.Register(request, default);
            if (state is "invalid-email" or "weak-password" or "missing-name") Assert.IsType<BadRequestObjectResult>(result);
            else if (state == "mail-failure") Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
            else
            {
                var response = Assert.IsType<AffiliateAuthResponse>(Assert.IsType<OkObjectResult>(result).Value);
                Assert.Empty(response.AccessToken); Assert.Empty(response.RefreshToken); Assert.False(response.EmailVerified);
                if (state == "duplicate") Assert.IsType<ConflictObjectResult>(await controller.Register(request, default));
            }
        }
        f.Db.ChangeTracker.Clear();
        var committed = state is "valid" or "duplicate" or "mail-failure";
        Assert.Equal(committed ? 1 : 0, await f.Db.AffiliateUsers.IgnoreQueryFilters().CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.AffiliateAccounts.IgnoreQueryFilters().CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.AffiliateEmailVerificationTokens.IgnoreQueryFilters().CountAsync());
        Assert.Equal(committed ? 1 : 0, sent);
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
    }

    private sealed class FailVerificationTokenSave(bool enabled) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (enabled && eventData.Context!.ChangeTracker.Entries<AffiliateEmailVerificationToken>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("Controlled registration verification-token failure");
            return ValueTask.FromResult(result);
        }
    }
}
