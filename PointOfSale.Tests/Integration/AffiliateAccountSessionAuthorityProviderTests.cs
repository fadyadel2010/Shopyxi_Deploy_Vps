using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Tests.Integration;

// Only account/session lifecycle methods on real disposable PostgreSQL18.
// Strict financial/event collaborators cannot post, settle, pay or send anything.
public sealed class AffiliateAccountSessionAuthorityProviderTests
{
    private const string Credential = "Fixture-only-Account-Session-842!";
    private static readonly AffiliateTelemetry Telemetry = new();
    internal static AffiliateService Service(ApplicationDbContext db) => new(db,
        Mock.Of<ITenantContext>(MockBehavior.Strict), Mock.Of<IEventPublisher>(MockBehavior.Strict), Telemetry,
        NullLogger<AffiliateService>.Instance, Mock.Of<IAffiliatePayoutService>(MockBehavior.Strict),
        Mock.Of<IAffiliateSettlementOrchestrator>(MockBehavior.Strict), Options.Create(new AffiliateSettlementOptions()));
    private static Task Revoke(AffiliateService service, Guid id, string operation) => operation == "suspend"
        ? service.SuspendAffiliateAsync(id, "fixture suspension") : service.CloseAffiliateAsync(id, "fixture closure");

    [Theory]
    [InlineData("suspend")]
    [InlineData("close")]
    public async Task AccountRevocationCannotLeaveASessionCreatedAfterItsInitiallyEmptyUserSnapshot(string operation)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db);
        user.SetPasswordHash(new PasswordHasher<AffiliateUser>().HashPassword(user, Credential));
        user.ClearRefreshToken(); await f.Db.SaveChangesAsync();
        var pause = new PauseUserRead();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = Revoke(Service(db), account.Id, operation);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await using var loginDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        Task<IActionResult>? login = null; var userLocked = false;
        try
        {
            userLocked = await IsLockedAsync(f.ConnectionString, "user", user.Id);
            login = AffiliateRefreshAuthorityProviderTests.Controller(loginDb)
                .Login(new AffiliateLoginDto { Email = user.Email, Password = Credential }, default);
            if (!userLocked)
                Assert.IsType<OkObjectResult>(await login.WaitAsync(TimeSpan.FromSeconds(20)));
            // If real user authority is already held, login must instead observe
            // revocation after release. No fake lock or forced stale ordering.
        }
        finally { pause.Release.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(login);
        var loginResult = await login.WaitAsync(TimeSpan.FromSeconds(20));
        if (userLocked) Assert.IsType<UnauthorizedObjectResult>(loginResult);
        else Assert.IsType<OkObjectResult>(loginResult);
        f.Db.ChangeTracker.Clear();
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Null(persisted.RefreshToken); Assert.Null(persisted.PreviousRefreshToken);
        Assert.NotNull(persisted.TokensValidAfterUtc);
        Assert.Equal(operation == "suspend" ? AffiliateStatus.Suspended : AffiliateStatus.Closed,
            (await f.Db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("suspend", "user")]
    [InlineData("suspend", "account")]
    [InlineData("close", "user")]
    [InlineData("close", "account")]
    public async Task AccountRevocationHoldsExactUserThenAccountAuthorityThroughItsStateAndSessionSave(string operation, string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db);
        var pause = new AccountSave(pause: true);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = Revoke(Service(db), account.Id, operation);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var locked = false;
        try { locked = await IsLockedAsync(f.ConnectionString, identity, identity == "user" ? user.Id : account.Id); }
        finally { pause.Release.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(locked);
    }

    [Fact]
    public async Task ActivationCannotReuseATrackedSuspendedAccountAfterACommittedClosure()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (_, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db);
        account.Suspend("fixture initial suspension"); await f.Db.SaveChangesAsync();
        await using var staleDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        var tracked = await staleDb.AffiliateAccounts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AffiliateStatus.Suspended, tracked.Status);
        // Actual domain service transitions on an independent context, not an
        // invented database Closed state or financial operation.
        await Service(f.Db).ActivateAffiliateAsync(account.Id);
        await Service(f.Db).CloseAffiliateAsync(account.Id, "fixture committed closure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(staleDb).ActivateAffiliateAsync(account.Id));
        Assert.Equal(AffiliateStatus.Closed, (await f.Db.AffiliateAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("close")]
    public async Task FailedAccountStateSavePreservesOriginalAccountAndSessionAtomically(string operation)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db); var original = user.RefreshToken;
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(new AccountSave(fail: true)).Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Revoke(Service(db), account.Id, operation));
        f.Db.ChangeTracker.Clear();
        Assert.Equal(AffiliateStatus.Active, (await f.Db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync()).Status);
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(original, persisted.RefreshToken); Assert.Null(persisted.TokensValidAfterUtc);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("close")]
    public async Task AccountRevocationPreservesAFutureCommittedCutoffAndClearsBothSessionSlots(string operation)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db);
        var future = DateTime.UtcNow.AddDays(1); user.RevokeSessions(future);
        user.SetRefreshToken("fictional-first-hash", DateTime.UtcNow.AddHours(1));
        user.SetRefreshToken("fictional-second-hash", DateTime.UtcNow.AddHours(1));
        await f.Db.SaveChangesAsync();
        var before = await f.Db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.NotNull(before.PreviousRefreshToken);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await Revoke(Service(db), account.Id, operation);
        var after = await f.Db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(before.TokensValidAfterUtc, after.TokensValidAfterUtc);
        Assert.Null(after.RefreshToken); Assert.Null(after.PreviousRefreshToken); Assert.Null(after.RefreshTokenExpiryTime);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("close")]
    public async Task FailureAfterAccountAndSessionSqlWritesRollsBackBothPersistedRows(string operation)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db);
        var interceptor = new FailAfterLifecycleWrite();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(interceptor).Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Revoke(Service(db), account.Id, operation));
        Assert.True(interceptor.ObservedWrittenRows);
        Assert.Null(db.Database.CurrentTransaction);
        f.Db.ChangeTracker.Clear();
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(user.RefreshToken, persisted.RefreshToken); Assert.Null(persisted.TokensValidAfterUtc);
        Assert.Equal(AffiliateStatus.Active, (await f.Db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("close")]
    public async Task AccountRevocationWaitsForWinningLoginThenClearsItsCommittedSession(string operation)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db);
        user.SetPasswordHash(new PasswordHasher<AffiliateUser>().HashPassword(user, Credential));
        user.ClearRefreshToken(); await f.Db.SaveChangesAsync();
        var pause = new PauseLoginSave();
        await using var loginDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var login = AffiliateRefreshAuthorityProviderTests.Controller(loginDb)
            .Login(new AffiliateLoginDto { Email = user.Email, Password = Credential }, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        await db.Database.OpenConnectionAsync();
        var revocation = Revoke(Service(db), account.Id, operation);
        try { await RequireBlockedAsync(f.ConnectionString, db, revocation); }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await login.WaitAsync(TimeSpan.FromSeconds(20)));
        await revocation.WaitAsync(TimeSpan.FromSeconds(20));
        f.Db.ChangeTracker.Clear();
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Null(persisted.RefreshToken); Assert.Null(persisted.PreviousRefreshToken); Assert.NotNull(persisted.TokensValidAfterUtc);
        if (operation == "suspend")
        {
            await Service(f.Db).ActivateAffiliateAsync(account.Id);
            var reactivated = await f.Db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Null(reactivated.RefreshToken); Assert.Equal(persisted.TokensValidAfterUtc, reactivated.TokensValidAfterUtc);
        }
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("close")]
    public async Task AccountLifecycleRefusesAnIdentityMembershipChangedAfterDiscovery(string operation)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (user, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db);
        var other = AffiliateAccount.Create(Guid.Empty, Guid.NewGuid(), "MEMBERSHIP", "Disposable membership refusal");
        f.Db.Add(other); await f.Db.SaveChangesAsync();
        var pause = new PauseUserRead();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options);
        var pending = Revoke(Service(db), account.Id, operation);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            // Controlled maintenance-style reassignment is a refusal probe, not
            // a new supported business endpoint or external-writer approval.
            Assert.Equal(1, await f.Db.AffiliateUsers.IgnoreQueryFilters().Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(u => u.AffiliateAccountId, other.Id)));
        }
        finally { pause.Release.TrySetResult(); }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("membership changed", error.Message, StringComparison.Ordinal);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(AffiliateStatus.Active, (await f.Db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == account.Id)).Status);
        var persisted = await f.Db.AffiliateUsers.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(other.Id, persisted.AffiliateAccountId); Assert.Equal(user.RefreshToken, persisted.RefreshToken);
    }

    [Theory]
    [InlineData("pending-legacy")]
    [InlineData("active")]
    [InlineData("suspended")]
    [InlineData("closed")]
    [InlineData("deleted")]
    public async Task CurrentAccountActivationPreservesExistingAdmissionAndTerminalRules(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (_, account) = await AffiliateRefreshAuthorityProviderTests.SeedAsync(f.Db);
        if (state == "pending-legacy")
        {
            account = AffiliateAccount.Create(Guid.Empty, Guid.NewGuid(), "LEGACYPEND", "Disposable no-user legacy account");
            f.Db.Add(account);
        }
        if (state == "suspended") account.Suspend("fixture suspension");
        if (state == "closed") account.Close("fixture closure");
        if (state == "deleted") account.IsDeleted = true;
        await f.Db.SaveChangesAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString).Options);
        if (state is "closed" or "deleted")
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).ActivateAffiliateAsync(account.Id));
        else await Service(db).ActivateAffiliateAsync(account.Id);
        var persisted = await f.Db.AffiliateAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.Equal(state == "closed" ? AffiliateStatus.Closed : AffiliateStatus.Active, persisted.Status);
        Assert.Equal(state == "deleted", persisted.IsDeleted);
    }

    private static async Task RequireBlockedAsync(string connectionString, ApplicationDbContext db, Task pending)
    {
        await using var observer = new NpgsqlConnection(connectionString); await observer.OpenAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew(); var blocked = false;
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event_type='Lock')", observer);
            query.Parameters.AddWithValue("pid", ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
            if ((bool)(await query.ExecuteScalarAsync())!) { blocked = true; break; }
            if (pending.IsCompleted) break;
            await Task.Delay(20);
        }
        Assert.True(blocked, "Revocation must observably wait for the winning login's real PostgreSQL lock.");
    }

    private sealed class FailAfterLifecycleWrite : SaveChangesInterceptor
    {
        internal bool ObservedWrittenRows { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            var user = await db.Set<AffiliateUser>().IgnoreQueryFilters().AsNoTracking().SingleAsync(cancellationToken);
            var account = await db.Set<AffiliateAccount>().IgnoreQueryFilters().AsNoTracking().SingleAsync(cancellationToken);
            Assert.Null(user.RefreshToken); Assert.NotNull(user.TokensValidAfterUtc); Assert.False(account.IsActive);
            Assert.NotNull(db.Database.CurrentTransaction); ObservedWrittenRows = true;
            throw new InvalidOperationException("Controlled failure after actual lifecycle SQL writes before commit");
        }
    }

    private sealed class PauseLoginSave : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AffiliateUser>().Any(e => e.State == EntityState.Modified && e.Property(u => u.RefreshToken).IsModified))
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private static async Task<bool> IsLockedAsync(string connectionString, string identity, Guid id)
    {
        await using var observer = new NpgsqlConnection(connectionString); await observer.OpenAsync();
        await using var transaction = await observer.BeginTransactionAsync();
        var sql = identity == "user" ? "SELECT \"Id\" FROM \"AffiliateUsers\" WHERE \"Id\"=@id FOR UPDATE NOWAIT" :
            "SELECT \"Id\" FROM \"AffiliateAccounts\" WHERE \"Id\"=@id FOR UPDATE NOWAIT";
        await using var command = new NpgsqlCommand(sql, observer, transaction); command.Parameters.AddWithValue("id", id);
        var locked = false;
        try { await command.ExecuteScalarAsync(); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable) { locked = true; }
        await transaction.RollbackAsync(); return locked;
    }

    private sealed class PauseUserRead : DbCommandInterceptor
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

    private sealed class AccountSave(bool fail = false, bool pause = false) : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AffiliateAccount>().Any(e => e.State == EntityState.Modified && e.Property(a => a.Status).IsModified))
            {
                if (fail) throw new InvalidOperationException("Controlled account lifecycle save failure");
                if (pause) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            }
            return result;
        }
    }
}
