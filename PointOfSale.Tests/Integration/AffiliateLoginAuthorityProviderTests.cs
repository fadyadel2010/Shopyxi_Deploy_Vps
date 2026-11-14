using System.Data.Common;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Real credential verifier/controller and guarded disposable PostgreSQL18.
// Fictional identity/key/password only; no host, mail or external session.
public sealed class AffiliateLoginAuthorityProviderTests
{
    private const string Password = "Fixture-only-Credential-927!";

    private static async Task<(AffiliateUser User, AffiliateAccount Account)> SeedAsync(ApplicationDbContext db)
    {
        var seeded = await AffiliateRefreshAuthorityProviderTests.SeedAsync(db);
        seeded.User.SetPasswordHash(new PasswordHasher<AffiliateUser>().HashPassword(seeded.User, Password));
        await db.SaveChangesAsync(); return seeded;
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("bad-password")]
    [InlineData("unknown-email")]
    [InlineData("unverified")]
    [InlineData("user-deleted")]
    [InlineData("account-deleted")]
    [InlineData("account-suspended")]
    [InlineData("save-failure")]
    public async Task LoginRequiresCurrentCredentialsAndIdentityAndCommitsBeforeCookies(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await SeedAsync(f.Db);
        if (state == "unverified") user.SetEmail(user.Email);
        if (state == "user-deleted") user.IsDeleted = true;
        if (state == "account-deleted") account.IsDeleted = true;
        if (state == "account-suspended") account.Suspend("fixture revocation");
        await f.Db.SaveChangesAsync(); var originalHash = user.RefreshToken;
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(new LoginSave(fail: state == "save-failure")).Options);
        var controller = AffiliateRefreshAuthorityProviderTests.Controller(db); var observations = 0;
        if (state == "valid")
        {
            controller.HttpContext.Features.Set<IResponseCookiesFeature>(new AffiliateRefreshAuthorityProviderTests.ObservedCookies(controller.Response.Cookies, () =>
            {
                Assert.Null(db.Database.CurrentTransaction);
                using var observer = new NpgsqlConnection(f.ConnectionString); observer.Open();
                using var query = new NpgsqlCommand("SELECT \"RefreshToken\" FROM \"AffiliateUsers\" WHERE \"Id\"=@id", observer);
                query.Parameters.AddWithValue("id", user.Id);
                Assert.NotEqual(originalHash, Assert.IsType<string>(query.ExecuteScalar())); observations++;
            }));
        }
        var request = new AffiliateLoginDto
        {
            Email = state == "unknown-email" ? "unknown@example.invalid" : user.Email,
            Password = state == "bad-password" ? "incorrect-fixture-password" : Password
        };
        if (state == "save-failure")
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Login(request, default));
        else
        {
            var result = await controller.Login(request, default);
            if (state == "valid") Assert.IsType<OkObjectResult>(result);
            else Assert.IsType<UnauthorizedObjectResult>(result);
        }
        f.Db.ChangeTracker.Clear();
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        if (state == "valid")
        {
            Assert.NotNull(persisted.RefreshToken); Assert.NotEqual(originalHash, persisted.RefreshToken);
            Assert.Equal(originalHash, persisted.PreviousRefreshToken); Assert.True(observations >= 2);
            Assert.True(controller.Response.Headers.ContainsKey("Set-Cookie"));
        }
        else
        {
            Assert.Equal(originalHash, persisted.RefreshToken);
            Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        }
        Assert.Empty(await f.Db.AffiliateRefreshTokenReplayEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("password-changed")]
    [InlineData("email-changed")]
    [InlineData("user-deleted")]
    [InlineData("account-deleted")]
    [InlineData("account-suspended")]
    public async Task LoginCannotIssueASessionFromCredentialsOrEligibilityRevokedAfterItsInitialRead(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await SeedAsync(f.Db); var email = user.Email;
        var pause = new CredentialRead();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var controller = AffiliateRefreshAuthorityProviderTests.Controller(db);
        var pending = controller.Login(new AffiliateLoginDto { Email = email, Password = Password }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            if (state == "password-changed")
            {
                user.SetPasswordHash(new PasswordHasher<AffiliateUser>().HashPassword(user, "Replacement-fixture-Password-831!"));
                user.RevokeSessions(DateTime.UtcNow);
            }
            else if (state == "email-changed") user.SetEmail("changed@example.invalid");
            else if (state == "user-deleted") user.IsDeleted = true;
            else if (state == "account-deleted") account.IsDeleted = true;
            else account.Suspend("fixture concurrent revocation");
            await f.Db.SaveChangesAsync();
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<UnauthorizedObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        var expectedHash = user.RefreshToken; var expectedPassword = user.PasswordHash;
        f.Db.ChangeTracker.Clear();
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(expectedHash, persisted.RefreshToken); Assert.Equal(expectedPassword, persisted.PasswordHash);
        Assert.Empty(await f.Db.AffiliateRefreshTokenReplayEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("account")]
    public async Task LoginHoldsExactUserAndAccountLocksThroughSessionCommit(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await SeedAsync(f.Db); var pause = new LoginSave(pause: true);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = AffiliateRefreshAuthorityProviderTests.Controller(db).Login(new AffiliateLoginDto { Email = user.Email, Password = Password }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); string? observed = null;
        try
        {
            await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync();
            await using var transaction = await observer.BeginTransactionAsync();
            var sql = identity == "user" ? "SELECT \"Id\" FROM \"AffiliateUsers\" WHERE \"Id\"=@id FOR UPDATE NOWAIT" :
                "SELECT \"Id\" FROM \"AffiliateAccounts\" WHERE \"Id\"=@id FOR UPDATE NOWAIT";
            await using var query = new NpgsqlCommand(sql, observer, transaction);
            query.Parameters.AddWithValue("id", identity == "user" ? user.Id : account.Id);
            try { await query.ExecuteScalarAsync(); }
            catch (PostgresException error) { observed = error.SqlState; }
            await transaction.RollbackAsync();
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, observed);
    }

    private sealed class CredentialRead : DbCommandInterceptor
    {
        private bool _paused;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_paused && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains("\"AffiliateUsers\"", StringComparison.Ordinal))
            {
                _paused = true; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class LoginSave(bool fail = false, bool pause = false) : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AffiliateUser>().Any(e => e.State == EntityState.Modified && e.Property(u => u.RefreshToken).IsModified))
            {
                if (fail) throw new InvalidOperationException("Controlled login session save failure");
                if (pause) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            }
            return result;
        }
    }
}
