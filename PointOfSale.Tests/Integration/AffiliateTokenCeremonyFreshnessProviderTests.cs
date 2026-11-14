using System.Data.Common;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services.Auth;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Actual token controllers plus isolated guarded PostgreSQL18. Email is an
// observing no-network adapter, not a replacement for database concurrency.
public sealed class AffiliateTokenCeremonyFreshnessProviderTests
{
    private const string Credential = "Fixture-only-Login-527!";
    private const string NewPassword = "Replacement-Fixture-Password-832!";
    private const string RawReset = "fictional-fixture-reset-capability";

    private static async Task<(AffiliateUser User, AffiliateAccount Account, AffiliatePasswordResetToken Token)> SeedAsync(ApplicationDbContext db, bool emptySession = false)
    {
        var (user, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(db);
        user.SetPasswordHash(new PasswordHasher<AffiliateUser>().HashPassword(user, Credential));
        if (emptySession) user.ClearRefreshToken();
        var token = AffiliatePasswordResetToken.Create(user.Id, RawReset, DateTime.UtcNow.AddHours(1));
        db.Add(token); await db.SaveChangesAsync(); return (user, account, token);
    }

    [Theory]
    [InlineData("new-session-after-empty")]
    [InlineData("newer-session-cutoff")]
    [InlineData("expired-after-read")]
    public async Task ResetMustUseCurrentSessionCutoffAndTokenExpiryAfterItsIdentityLock(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, _, token) = await SeedAsync(f.Db, emptySession: state == "new-session-after-empty");
        var originalPassword = user.PasswordHash;
        var pause = new PauseRead("AffiliatePasswordResetTokens");
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var controller = AffiliateRefreshAuthorityProviderTests.Controller(db);
        var pending = controller.ResetPassword(new AffiliateResetPasswordDto { Token = RawReset, NewPassword = NewPassword }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        DateTime minimumCutoff = pause.ObservedUtc;
        try
        {
            if (state == "new-session-after-empty")
            {
                await using var loginDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
                Assert.IsType<OkObjectResult>(await AffiliateRefreshAuthorityProviderTests.Controller(loginDb)
                    .Login(new AffiliateLoginDto { Email = user.Email, Password = Credential }, default));
                Assert.NotNull((await f.Db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync()).RefreshToken);
            }
            else if (state == "newer-session-cutoff")
            {
                user.RevokeSessions(DateTime.UtcNow); await f.Db.SaveChangesAsync();
                minimumCutoff = (await f.Db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync()).TokensValidAfterUtc!.Value;
            }
            else
                Assert.Equal(1, await f.Db.AffiliatePasswordResetTokens.IgnoreQueryFilters().Where(t => t.Id == token.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(t => t.ExpiresAtUtc, pause.ObservedUtc)));
        }
        finally { pause.Release.TrySetResult(); }
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        if (state == "expired-after-read") Assert.IsType<UnauthorizedObjectResult>(response);
        else Assert.IsType<OkObjectResult>(response);
        f.Db.ChangeTracker.Clear();
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        var persistedToken = await f.Db.AffiliatePasswordResetTokens.IgnoreQueryFilters().SingleAsync();
        if (state == "expired-after-read")
        {
            Assert.Equal(originalPassword, persisted.PasswordHash); Assert.Null(persistedToken.ConsumedAtUtc);
            Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
            Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
        }
        else
        {
            Assert.Null(persisted.RefreshToken); Assert.Null(persisted.PreviousRefreshToken);
            Assert.NotNull(persisted.TokensValidAfterUtc); Assert.True(persisted.TokensValidAfterUtc.Value >= minimumCutoff);
            Assert.NotNull(persistedToken.ConsumedAtUtc);
            Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<AffiliateUser>().VerifyHashedPassword(persisted, persisted.PasswordHash, NewPassword));
        }
    }

    [Fact]
    public async Task ResetCannotConsumeAnInitialTokenSnapshotAfterAnotherResetCommits()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await SeedAsync(f.Db); var pause = new PauseRead("AffiliatePasswordResetTokens");
        await using var firstDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var firstController = AffiliateRefreshAuthorityProviderTests.Controller(firstDb);
        var request = new AffiliateResetPasswordDto { Token = RawReset, NewPassword = NewPassword };
        var pending = firstController.ResetPassword(request, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await using var secondDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
            Assert.IsType<OkObjectResult>(await AffiliateRefreshAuthorityProviderTests.Controller(secondDb).ResetPassword(request, default));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<UnauthorizedObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.False(firstController.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal(1, await f.Db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.ActionType == "PASSWORD_RESET_COMPLETED"));
    }

    [Fact]
    public async Task ResetClearsCookiesOnlyAfterIndependentConnectionsObserveCommittedTokenSessionAndAudit()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, _, _) = await SeedAsync(f.Db);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        var controller = AffiliateRefreshAuthorityProviderTests.Controller(db); var observations = 0;
        controller.HttpContext.Features.Set<IResponseCookiesFeature>(new AffiliateRefreshAuthorityProviderTests.ObservedCookies(controller.Response.Cookies, () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            using var observer = new NpgsqlConnection(f.ConnectionString); observer.Open();
            using var command = new NpgsqlCommand("SELECT COUNT(*) FROM \"AffiliateUsers\" u WHERE u.\"Id\"=@id AND u.\"RefreshToken\" IS NULL AND u.\"PreviousRefreshToken\" IS NULL AND u.\"TokensValidAfterUtc\" IS NOT NULL AND EXISTS(SELECT 1 FROM \"AffiliatePasswordResetTokens\" t WHERE t.\"AffiliateUserId\"=u.\"Id\" AND t.\"ConsumedAtUtc\" IS NOT NULL) AND EXISTS(SELECT 1 FROM \"AuditLogs\" a WHERE a.\"UserId\"=u.\"Id\" AND a.\"ActionType\"='PASSWORD_RESET_COMPLETED')", observer);
            command.Parameters.AddWithValue("id", user.Id); Assert.Equal(1L, command.ExecuteScalar()); observations++;
        }));
        Assert.IsType<OkObjectResult>(await controller.ResetPassword(new AffiliateResetPasswordDto { Token = RawReset, NewPassword = NewPassword }, default));
        Assert.True(observations >= 2);
    }

    [Fact]
    public async Task VerificationCannotUseAStillFutureInitialExpiryAfterTheLockedTokenHasExpired()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, _) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db); user.SetEmail(user.Email);
        var raw = AffiliateEmailVerificationToken.NewRawToken();
        var token = AffiliateEmailVerificationToken.Create(user.Id, raw, DateTime.UtcNow.AddHours(1));
        f.Db.Add(token); await f.Db.SaveChangesAsync(); var pause = new PauseRead("AffiliateEmailVerificationTokens");
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = AffiliateRefreshAuthorityProviderTests.Controller(db).VerifyEmail(new AffiliateVerifyEmailDto { Token = raw }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.Equal(1, await f.Db.AffiliateEmailVerificationTokens.IgnoreQueryFilters().Where(t => t.Id == token.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(t => t.ExpiresAtUtc, pause.ObservedUtc)));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<UnauthorizedObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        f.Db.ChangeTracker.Clear();
        Assert.False((await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync()).EmailVerified);
        Assert.Null((await f.Db.AffiliateEmailVerificationTokens.IgnoreQueryFilters().SingleAsync()).ConsumedAtUtc);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("email-changed")]
    [InlineData("user-deleted")]
    [InlineData("account-deleted")]
    public async Task ResetIssuanceCannotUseAnOldRecipientOrDeletedIdentityAfterItsInitialLookup(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account, _) = await SeedAsync(f.Db); var email = user.Email;
        var adapter = new Mock<IEmailSender>(MockBehavior.Strict);
        adapter.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var pause = new PauseRead("AffiliateUsers");
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = AffiliateRefreshAuthorityProviderTests.Controller(db, adapter.Object).ForgotPassword(new AffiliateForgotPasswordDto { Email = email }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            if (state == "email-changed") user.SetEmail("new-recipient@example.invalid");
            else if (state == "user-deleted") user.IsDeleted = true;
            else account.IsDeleted = true;
            await f.Db.SaveChangesAsync();
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        adapter.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        f.Db.ChangeTracker.Clear();
        Assert.Null((await f.Db.AffiliatePasswordResetTokens.IgnoreQueryFilters().SingleAsync()).ConsumedAtUtc);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("account")]
    public async Task ResetIssuanceHoldsExactCurrentUserAndAccountBeforeTokenSave(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account, _) = await SeedAsync(f.Db);
        var adapter = new Mock<IEmailSender>(MockBehavior.Strict);
        adapter.Setup(e => e.SendEmailAsync(user.Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var pause = new PauseIssuance();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = AffiliateRefreshAuthorityProviderTests.Controller(db, adapter.Object).ForgotPassword(new AffiliateForgotPasswordDto { Email = user.Email }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); string? observed = null;
        try
        {
            await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync();
            await using var transaction = await observer.BeginTransactionAsync();
            var sql = identity == "user" ? "SELECT \"Id\" FROM \"AffiliateUsers\" WHERE \"Id\"=@id FOR UPDATE NOWAIT" :
                "SELECT \"Id\" FROM \"AffiliateAccounts\" WHERE \"Id\"=@id FOR UPDATE NOWAIT";
            await using var command = new NpgsqlCommand(sql, observer, transaction);
            command.Parameters.AddWithValue("id", identity == "user" ? user.Id : account.Id);
            try { await command.ExecuteScalarAsync(); }
            catch (PostgresException error) { observed = error.SqlState; }
            await transaction.RollbackAsync();
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        adapter.Verify(e => e.SendEmailAsync(user.Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once());
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, observed);
    }

    private sealed class PauseRead(string table) : DbCommandInterceptor
    {
        private bool _paused;
        internal DateTime ObservedUtc { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_paused && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains('"' + table + '"', StringComparison.Ordinal))
            {
                _paused = true; ObservedUtc = DateTime.UtcNow; Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class PauseIssuance : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AffiliatePasswordResetToken>().Any(e => e.State == EntityState.Added))
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
