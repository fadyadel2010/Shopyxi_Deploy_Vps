using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.API.Models;
using PointOfSale.API.Services.Auth;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Actual controller and guarded disposable PostgreSQL18. No application host,
// Mongo, real email, financial client, or external session is created.
public sealed class AffiliateRefreshAuthorityProviderTests
{
    private const string RawToken = "fixture-only-refresh-not-an-external-credential";
    private static string TokenHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RawToken)));

    internal static async Task<(AffiliateUser User, AffiliateAccount Account)> SeedAsync(ApplicationDbContext db)
    {
        var id = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, id, "APP2REFRESH", "Disposable refresh");
        account.Id = Guid.NewGuid(); account.Activate();
        var user = AffiliateUser.Create(Guid.Empty, "refresh@example.invalid", "fixture-not-a-login", account.Id);
        user.Id = id; user.VerifyEmail(); user.SetRefreshToken(TokenHash, DateTime.UtcNow.AddHours(1));
        db.AddRange(account, user); await db.SaveChangesAsync();
        return (user, account);
    }

    internal static AffiliateAuthController Controller(ApplicationDbContext db, IEmailSender? emailSender = null) => new(db, null!,
        Options.Create(new AffiliateJwtSettings { Secret = new string('x', 64), Issuer = "APP2FixtureAffiliate", Audience = "APP2FixturePortal" }),
        new PasswordHasher<AffiliateUser>(), emailSender ?? Mock.Of<IEmailSender>(MockBehavior.Strict),
        new ConfigurationBuilder().Build(), NullLogger<AffiliateAuthController>.Instance)
    { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    [Theory]
    [InlineData("valid")]
    [InlineData("unverified")]
    [InlineData("account-suspended")]
    [InlineData("account-deleted")]
    [InlineData("user-deleted")]
    [InlineData("expired")]
    [InlineData("unknown-token")]
    [InlineData("save-failure")]
    public async Task RefreshUsesCurrentIdentityAndAtomicallyRotatesOrPreservesRollback(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await SeedAsync(f.Db);
        if (state == "unverified") user.SetEmail(user.Email);
        if (state == "account-suspended") account.Suspend("fixture authority revoked");
        if (state == "account-deleted") account.IsDeleted = true;
        if (state == "user-deleted") user.IsDeleted = true;
        if (state == "expired") user.RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(-1);
        await f.Db.SaveChangesAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(new RotationSave(fail: state == "save-failure")).Options);
        var controller = Controller(db); var cookieObservations = 0;
        if (state == "valid")
        {
            var cookies = controller.Response.Cookies;
            controller.HttpContext.Features.Set<IResponseCookiesFeature>(new ObservedCookies(cookies, () =>
            {
                // This executes at the actual cookie-append boundary, not after
                // return. Only an independent connection sees durable commit.
                Assert.Null(db.Database.CurrentTransaction);
                using var observer = new NpgsqlConnection(f.ConnectionString); observer.Open();
                using var command = new NpgsqlCommand("SELECT u.\"RefreshToken\" FROM \"AffiliateUsers\" u WHERE u.\"Id\"=@id AND EXISTS (SELECT 1 FROM \"AffiliateRefreshTokenReplayEntries\" r WHERE r.\"AffiliateUserId\"=u.\"Id\" AND r.\"TokenHash\"=@hash)", observer);
                command.Parameters.AddWithValue("id", user.Id); command.Parameters.AddWithValue("hash", TokenHash);
                Assert.NotEqual(TokenHash, Assert.IsType<string>(command.ExecuteScalar())); cookieObservations++;
            }));
        }
        var request = new AffiliateRefreshDto { RefreshToken = state == "unknown-token" ? "fixture-unknown" : RawToken };
        if (state == "save-failure")
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RefreshToken(request, default));
        else
        {
            var result = await controller.RefreshToken(request, default);
            if (state == "valid") Assert.IsType<OkObjectResult>(result);
            else Assert.IsType<UnauthorizedObjectResult>(result);
        }
        f.Db.ChangeTracker.Clear();
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        var ledger = await f.Db.AffiliateRefreshTokenReplayEntries.IgnoreQueryFilters().ToListAsync();
        if (state == "valid")
        {
            Assert.NotNull(persisted.RefreshToken); Assert.NotEqual(TokenHash, persisted.RefreshToken);
            Assert.Equal(TokenHash, persisted.PreviousRefreshToken);
            Assert.Equal(TokenHash, Assert.Single(ledger).TokenHash);
            Assert.True(controller.Response.Headers.ContainsKey("Set-Cookie")); Assert.True(cookieObservations >= 2);
        }
        else
        {
            Assert.Empty(ledger);
            Assert.Equal(state is "expired" or "unverified" ? null : TokenHash, persisted.RefreshToken);
            if (state != "expired") Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        }
        Assert.DoesNotContain(RawToken, persisted.RefreshToken ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayingRotatedTokenRevokesDurableCurrentSessionAndRecordsAudit()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await SeedAsync(f.Db);
        await using (var firstDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options))
            Assert.IsType<OkObjectResult>(await Controller(firstDb).RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default));
        await using var replayDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        var controller = Controller(replayDb);
        Assert.IsType<UnauthorizedObjectResult>(await controller.RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default));
        f.Db.ChangeTracker.Clear();
        var user = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Null(user.RefreshToken); Assert.Null(user.PreviousRefreshToken); Assert.NotNull(user.TokensValidAfterUtc);
        Assert.NotNull((await f.Db.AffiliateRefreshTokenReplayEntries.IgnoreQueryFilters().SingleAsync()).ReplayedAtUtc);
        Assert.Equal(1, await f.Db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.ActionType == "REFRESH_TOKEN_REPLAY_DETECTED"));
        Assert.True(controller.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Theory]
    [InlineData("user-revoked")]
    [InlineData("user-deleted")]
    [InlineData("account-suspended")]
    [InlineData("account-deleted")]
    public async Task RefreshCannotRotateFromAnEligibilitySnapshotAfterConcurrentRevocation(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await SeedAsync(f.Db);
        var pause = new PauseInitialRefreshRead();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var controller = Controller(db);
        var pending = controller.RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            if (state == "user-revoked")
                Assert.Equal(1, await f.Db.AffiliateUsers.IgnoreQueryFilters().Where(u => u.Id == user.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(u => u.RefreshToken, (string?)null)
                        .SetProperty(u => u.RefreshTokenExpiryTime, (DateTime?)null)));
            else if (state == "user-deleted")
                Assert.Equal(1, await f.Db.AffiliateUsers.IgnoreQueryFilters().Where(u => u.Id == user.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(u => u.IsDeleted, true)));
            else if (state == "account-deleted")
                Assert.Equal(1, await f.Db.AffiliateAccounts.IgnoreQueryFilters().Where(a => a.Id == account.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(a => a.IsDeleted, true)));
            else
                Assert.Equal(1, await f.Db.AffiliateAccounts.IgnoreQueryFilters().Where(a => a.Id == account.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(a => a.Status, AffiliateStatus.Suspended)));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<UnauthorizedObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        f.Db.ChangeTracker.Clear();
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(state == "user-revoked" ? null : TokenHash, persisted.RefreshToken);
        Assert.Empty(await f.Db.AffiliateRefreshTokenReplayEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("account")]
    public async Task RefreshRetainsExactIdentityLocksThroughRotationCommit(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await SeedAsync(f.Db);
        var pause = new RotationSave(pause: true);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = Controller(db).RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); string? observed = null;
        try
        {
            await using var connection = new NpgsqlConnection(f.ConnectionString); await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var sql = identity == "user" ? "SELECT \"Id\" FROM \"AffiliateUsers\" WHERE \"Id\"=@id FOR UPDATE NOWAIT" :
                "SELECT \"Id\" FROM \"AffiliateAccounts\" WHERE \"Id\"=@id FOR UPDATE NOWAIT";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("id", identity == "user" ? user.Id : account.Id);
            try { await command.ExecuteScalarAsync(); }
            catch (PostgresException error) { observed = error.SqlState; }
            await transaction.RollbackAsync();
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, observed);
    }

    [Fact]
    public async Task ConcurrentDuplicateRotationWaitsForCommitThenRevokesTheWinningSessionAsReplay()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await SeedAsync(f.Db);
        var pause = new RotationSave(pause: true);
        await using var firstDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        await using var secondDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await secondDb.Database.OpenConnectionAsync();
        var first = Controller(firstDb).RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var secondController = Controller(secondDb);
        var second = secondController.RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default);
        try { await RequireBlockedAsync(f.ConnectionString, secondDb, second); }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await first.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.IsType<UnauthorizedObjectResult>(await second.WaitAsync(TimeSpan.FromSeconds(20)));
        f.Db.ChangeTracker.Clear();
        var user = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Null(user.RefreshToken); Assert.Null(user.PreviousRefreshToken); Assert.NotNull(user.TokensValidAfterUtc);
        Assert.NotNull((await f.Db.AffiliateRefreshTokenReplayEntries.IgnoreQueryFilters().SingleAsync()).ReplayedAtUtc);
        Assert.Equal(1, await f.Db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.ActionType == "REFRESH_TOKEN_REPLAY_DETECTED"));
        Assert.True(secondController.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplayAndCurrentRotationSerializeOnUserBeforeMutatingEitherSession(bool replayWins)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        await SeedAsync(f.Db);
        var currentRaw = await RotateOnceAsync(f.ConnectionString);
        var rotationPause = new RotationSave(pause: !replayWins);
        var replayPause = new RevocationSave(pause: replayWins);
        await using var firstDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(rotationPause, replayPause).Options);
        await using var secondDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await secondDb.Database.OpenConnectionAsync();
        var first = Controller(firstDb).RefreshToken(new AffiliateRefreshDto { RefreshToken = replayWins ? RawToken : currentRaw }, default);
        await (replayWins ? replayPause.Entered.Task : rotationPause.Entered.Task).WaitAsync(TimeSpan.FromSeconds(20));
        var second = Controller(secondDb).RefreshToken(new AffiliateRefreshDto { RefreshToken = replayWins ? currentRaw : RawToken }, default);
        try { await RequireBlockedAsync(f.ConnectionString, secondDb, second); }
        finally { rotationPause.Release.TrySetResult(); replayPause.Release.TrySetResult(); }
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(20));
        if (replayWins) Assert.IsType<UnauthorizedObjectResult>(firstResult);
        else Assert.IsType<OkObjectResult>(firstResult);
        Assert.IsType<UnauthorizedObjectResult>(await second.WaitAsync(TimeSpan.FromSeconds(20)));
        f.Db.ChangeTracker.Clear();
        var user = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Null(user.RefreshToken); Assert.Null(user.PreviousRefreshToken); Assert.NotNull(user.TokensValidAfterUtc);
        var ledger = await f.Db.AffiliateRefreshTokenReplayEntries.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(replayWins ? 1 : 2, ledger.Count);
        Assert.NotNull(ledger.Single(r => r.TokenHash == TokenHash).ReplayedAtUtc);
        Assert.Equal(1, await f.Db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.ActionType == "REFRESH_TOKEN_REPLAY_DETECTED"));
    }

    [Fact]
    public async Task ExpiredSnapshotCannotRevokeAnUnrelatedNewSessionInstalledBeforeItsClaim()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, _) = await SeedAsync(f.Db);
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(-1); await f.Db.SaveChangesAsync();
        var pause = new PauseInitialRefreshRead();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var controller = Controller(db);
        var pending = controller.RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var newHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("independent-fixture-session")));
        var cutoff = DateTime.UtcNow;
        try
        {
            Assert.Equal(1, await f.Db.AffiliateUsers.IgnoreQueryFilters().Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(u => u.RefreshToken, newHash)
                    .SetProperty(u => u.PreviousRefreshToken, (string?)null)
                    .SetProperty(u => u.RefreshTokenExpiryTime, DateTime.UtcNow.AddHours(1))
                    .SetProperty(u => u.TokensValidAfterUtc, cutoff)));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<UnauthorizedObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        f.Db.ChangeTracker.Clear();
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(newHash, persisted.RefreshToken); Assert.Null(persisted.PreviousRefreshToken);
        Assert.True(Math.Abs((persisted.TokensValidAfterUtc!.Value - cutoff).TotalMilliseconds) < 1);
        Assert.Empty(await f.Db.AffiliateRefreshTokenReplayEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("replay")]
    public async Task RevocationAuditFailureRollsBackCurrentSessionAndReplayMarkerWithoutCookieEffects(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (seeded, _) = await SeedAsync(f.Db);
        if (state == "expired") { seeded.RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(-1); await f.Db.SaveChangesAsync(); }
        else await RotateOnceAsync(f.ConnectionString);
        f.Db.ChangeTracker.Clear();
        var before = await f.Db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(new RevocationSave(fail: true)).Options);
        var controller = Controller(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default));
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        var after = await f.Db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(before.RefreshToken, after.RefreshToken); Assert.Equal(before.PreviousRefreshToken, after.PreviousRefreshToken);
        Assert.Equal(before.TokensValidAfterUtc, after.TokensValidAfterUtc);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
        var ledger = await f.Db.AffiliateRefreshTokenReplayEntries.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(state == "replay" ? 1 : 0, ledger.Count); Assert.All(ledger, row => Assert.Null(row.ReplayedAtUtc));
    }

    [Theory]
    [InlineData("expired", "user")]
    [InlineData("expired", "account")]
    [InlineData("replay", "user")]
    [InlineData("replay", "account")]
    public async Task RevocationHoldsExactIdentityLocksUntilSessionAndAuditCommit(string state, string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await SeedAsync(f.Db);
        if (state == "expired") { user.RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(-1); await f.Db.SaveChangesAsync(); }
        else await RotateOnceAsync(f.ConnectionString);
        var pause = new RevocationSave(pause: true);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = Controller(db).RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default);
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
        Assert.IsType<UnauthorizedObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, observed);
    }

    private static async Task<string> RotateOnceAsync(string connectionString)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
        var controller = Controller(db);
        Assert.IsType<OkObjectResult>(await controller.RefreshToken(new AffiliateRefreshDto { RefreshToken = RawToken }, default));
        const string prefix = "affiliate_refresh_token=";
        var cookie = Assert.Single(controller.Response.Headers.SetCookie, value => value!.StartsWith(prefix, StringComparison.Ordinal))!;
        return Uri.UnescapeDataString(cookie[prefix.Length..].Split(';')[0]);
    }

    private static async Task RequireBlockedAsync(string connectionString, ApplicationDbContext db, Task pending)
    {
        var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        await using var observer = new NpgsqlConnection(connectionString); await observer.OpenAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew(); var blocked = false;
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var inspect = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event_type='Lock')", observer);
            inspect.Parameters.AddWithValue("pid", pid);
            if ((bool)(await inspect.ExecuteScalarAsync())!) { blocked = true; break; }
            if (pending.IsCompleted) break;
            await Task.Delay(20);
        }
        Assert.True(blocked, "Competing refresh/replay must observably wait for a real PostgreSQL row lock.");
    }

    private sealed class RevocationSave(bool fail = false, bool pause = false) : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added &&
                e.Entity.ActionType is "REFRESH_TOKEN_REPLAY_DETECTED" or "REFRESH_TOKEN_REPLAY_OR_INVALID"))
            {
                if (fail) throw new InvalidOperationException("Controlled refresh revocation audit failure");
                if (pause) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            }
            return result;
        }
    }

    private sealed class PauseInitialRefreshRead : DbCommandInterceptor
    {
        private bool _paused;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_paused && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"AffiliateUsers\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"RefreshToken\"", StringComparison.Ordinal))
            {
                _paused = true; Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    internal sealed class ObservedCookies(IResponseCookies inner, Action observe) : IResponseCookiesFeature, IResponseCookies
    {
        public IResponseCookies Cookies => this;
        public void Append(string key, string value) { observe(); inner.Append(key, value); }
        public void Append(string key, string value, CookieOptions options) { observe(); inner.Append(key, value, options); }
        public void Append(ReadOnlySpan<KeyValuePair<string, string>> values, CookieOptions options)
        { foreach (var value in values) Append(value.Key, value.Value, options); }
        public void Delete(string key) { observe(); inner.Delete(key); }
        public void Delete(string key, CookieOptions options) { observe(); inner.Delete(key, options); }
    }

    private sealed class RotationSave(bool fail = false, bool pause = false) : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AffiliateRefreshTokenReplayEntry>().Any(e => e.State == EntityState.Added))
            {
                if (fail) throw new InvalidOperationException("Controlled refresh rotation save failure");
                if (pause) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            }
            return result;
        }
    }
}
