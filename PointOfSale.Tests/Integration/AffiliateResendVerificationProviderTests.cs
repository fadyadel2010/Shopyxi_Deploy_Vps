using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Models;
using PointOfSale.API.Services.Auth;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Real disposable PostgreSQL. Only outbound mail is recorded; no SMTP/external call.
public sealed class AffiliateResendVerificationProviderTests
{
    [Theory]
    [InlineData("user")]
    [InlineData("account")]
    public async Task ResendRetainsCurrentIdentityLocksThroughTokenCommit(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "APP2MAILLOCK", "Disposable mail lock"); account.Id = Guid.NewGuid();
        var user = AffiliateUser.Create(Guid.Empty, "mail-lock@example.invalid", "fixture-not-a-login", account.Id); user.Id = userId;
        f.Db.AddRange(account, user); await f.Db.SaveChangesAsync();
        var pause = new PauseOwnedMutation(false);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var email = new Mock<IEmailSender>(MockBehavior.Strict);
        email.Setup(e => e.SendEmailAsync(user.Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var controller = new AffiliateAuthController(db, null!, Options.Create(new AffiliateJwtSettings()), new PasswordHasher<AffiliateUser>(),
            email.Object, new ConfigurationBuilder().Build(), NullLogger<AffiliateAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var pending = controller.ResendVerification(new AffiliateForgotPasswordDto { Email = user.Email }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        string? observedSqlState = null;
        try
        {
            await using var independent = new NpgsqlConnection(f.ConnectionString); await independent.OpenAsync();
            await using var tx = await independent.BeginTransactionAsync();
            // Fixed internal alternatives,never caller-controlled SQL identifiers.
            var sql = identity == "user" ? "SELECT \"Id\" FROM \"AffiliateUsers\" WHERE \"Id\"=@id FOR UPDATE NOWAIT" :
                "SELECT \"Id\" FROM \"AffiliateAccounts\" WHERE \"Id\"=@id FOR UPDATE NOWAIT";
            await using var probe = new NpgsqlCommand(sql, independent, tx);
            probe.Parameters.AddWithValue("id", identity == "user" ? userId : account.Id);
            try { await probe.ExecuteScalarAsync(); }
            catch (PostgresException error) { observedSqlState = error.SqlState; }
            await tx.RollbackAsync();
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, observedSqlState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerificationAndResendArbitrateOnTheSameUserBeforeTokenMutation(bool verificationWins)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "APP2ARBITER", "Disposable verification arbitration");
        account.Id = Guid.NewGuid();
        var user = AffiliateUser.Create(Guid.Empty, "arbitration@example.invalid", "fixture-not-a-login", account.Id); user.Id = userId;
        var raw = AffiliateEmailVerificationToken.NewRawToken();
        var original = AffiliateEmailVerificationToken.Create(userId, raw, DateTime.UtcNow.AddHours(1));
        f.Db.AddRange(account, user, original); await f.Db.SaveChangesAsync();
        var pause = new PauseOwnedMutation(verificationWins);
        await using var firstDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        await using var secondDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await secondDb.Database.OpenConnectionAsync();
        var secondPid = ((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
        var email = new Mock<IEmailSender>(MockBehavior.Strict);
        if (!verificationWins)
            email.Setup(e => e.SendEmailAsync(user.Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        AffiliateAuthController Controller(ApplicationDbContext db) => new(db, null!, Options.Create(new AffiliateJwtSettings()),
            new PasswordHasher<AffiliateUser>(), email.Object, new ConfigurationBuilder().Build(), NullLogger<AffiliateAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var verify = new AffiliateVerifyEmailDto { Token = raw };
        var resend = new AffiliateForgotPasswordDto { Email = user.Email };
        var first = verificationWins ? Controller(firstDb).VerifyEmail(verify, default) : Controller(firstDb).ResendVerification(resend, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = verificationWins ? Controller(secondDb).ResendVerification(resend, default) : Controller(secondDb).VerifyEmail(verify, default);
        try
        {
            await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew(); var blocked = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await using var inspect = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event_type='Lock')", observer);
                inspect.Parameters.AddWithValue("pid", secondPid);
                if ((bool)(await inspect.ExecuteScalarAsync())!) { blocked = true; break; }
                if (second.IsCompleted) break;
                await Task.Delay(20);
            }
            Assert.True(blocked, "Verification/resend must observably contend on a real PostgreSQL row lock.");
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await first.WaitAsync(TimeSpan.FromSeconds(20)));
        var competingResponse = await second.WaitAsync(TimeSpan.FromSeconds(20));
        if (verificationWins) Assert.IsType<OkObjectResult>(competingResponse);
        else Assert.IsType<UnauthorizedObjectResult>(competingResponse);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(verificationWins, (await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync()).EmailVerified);
        var current = Assert.Single(await f.Db.AffiliateEmailVerificationTokens.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(verificationWins, current.Id == original.Id);
        Assert.Equal(verificationWins, current.ConsumedAtUtc.HasValue);
        Assert.Equal(verificationWins ? 1 : 0, await f.Db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.ActionType == "EMAIL_VERIFIED"));
        email.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            verificationWins ? Times.Never() : Times.Once());
    }

    private sealed class PauseOwnedMutation(bool verification) : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            if (verification ? db.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added && e.Entity.ActionType == "EMAIL_VERIFIED") :
                db.ChangeTracker.Entries<AffiliateEmailVerificationToken>().Any(e => e.State == EntityState.Added))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("verified")]
    [InlineData("deleted-user")]
    [InlineData("deleted-account")]
    [InlineData("unknown")]
    [InlineData("delivery-failure")]
    [InlineData("save-failure")]
    public async Task ResendReplacesOnlyEligibleTokensCommitsBeforeMailAndPreservesRollback(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "APP2RESEND", "Disposable resend");
        account.Id = Guid.NewGuid();
        var user = AffiliateUser.Create(Guid.Empty, "resend@example.invalid", "fixture-not-a-login", account.Id);
        user.Id = userId;
        if (state == "verified") user.VerifyEmail();
        var originalRaw = AffiliateEmailVerificationToken.NewRawToken();
        var original = AffiliateEmailVerificationToken.Create(userId, originalRaw, DateTime.UtcNow.AddHours(1));
        f.Db.AddRange(account, user, original); await f.Db.SaveChangesAsync();
        if (state == "deleted-user")
            await f.Db.AffiliateUsers.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(v => v.IsDeleted, true));
        if (state == "deleted-account")
            await f.Db.AffiliateAccounts.IgnoreQueryFilters().Where(a => a.Id == account.Id).ExecuteUpdateAsync(a => a.SetProperty(v => v.IsDeleted, true));
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(new FailTokenSave(state == "save-failure")).Options);
        var email = new Mock<IEmailSender>(MockBehavior.Strict);
        var sent = 0;
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (string recipient, string subject, string html, string plain) => {
                // Verify durable state from an independent provider connection at
                // the exact external boundary, not just a same-context tracked row.
                Assert.Null(db.Database.CurrentTransaction);
                await using var observer = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
                var committed = Assert.Single(await observer.AffiliateEmailVerificationTokens.IgnoreQueryFilters().ToListAsync());
                Assert.NotEqual(original.Id, committed.Id);
                Assert.NotEqual(original.TokenHash, committed.TokenHash);
                Assert.DoesNotContain(originalRaw, html, StringComparison.Ordinal);
                Assert.DoesNotContain(originalRaw, plain, StringComparison.Ordinal);
                Assert.Equal(user.Email, recipient);
                sent++;
                if (state == "delivery-failure") throw new InvalidOperationException("Controlled mail failure");
            });
        var controller = new AffiliateAuthController(db, null!, Options.Create(new AffiliateJwtSettings()),
            new PasswordHasher<AffiliateUser>(), email.Object, new ConfigurationBuilder().Build(), NullLogger<AffiliateAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var request = new AffiliateForgotPasswordDto { Email = state == "unknown" ? "unknown@example.invalid" : user.Email.ToUpperInvariant() };
        if (state == "save-failure")
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ResendVerification(request, default));
        else
        {
            var response = await controller.ResendVerification(request, default);
            if (state == "delivery-failure") Assert.Equal(503, Assert.IsType<ObjectResult>(response).StatusCode);
            else Assert.IsType<OkObjectResult>(response);
        }
        f.Db.ChangeTracker.Clear();
        var token = Assert.Single(await f.Db.AffiliateEmailVerificationTokens.IgnoreQueryFilters().ToListAsync());
        var replaced = state is "valid" or "delivery-failure";
        Assert.Equal(replaced ? 1 : 0, sent);
        Assert.Equal(!replaced, token.Id == original.Id);
        Assert.Null(token.ConsumedAtUtc);
        Assert.Equal(state == "verified", (await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync()).EmailVerified);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
    }

    private sealed class FailTokenSave(bool enabled) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (enabled && eventData.Context!.ChangeTracker.Entries<AffiliateEmailVerificationToken>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("Controlled token-save failure");
            return ValueTask.FromResult(result);
        }
    }
}
