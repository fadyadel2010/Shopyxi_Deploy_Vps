using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PointOfSale.API.DTOs.Users;
using PointOfSale.API.Services.Users;
using PointOfSale.API.Security;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Actual service commands, disposable PostgreSQL only; no host or sender.
public sealed class PosUserCommandAuthorityProviderTests
{
    public static IEnumerable<object[]> RevokedCommands =>
        from command in Commands from revocation in new[] { "disabled", "demoted", "deleted" }
        select new object[] { command, revocation };

    private static readonly string[] Commands = ["invite", "resend", "revoke-invitation", "profile", "role", "status", "password", "session"];

    [Theory]
    [MemberData(nameof(RevokedCommands))]
    public async Task EveryAdministrativeCommandRejectsRevokedTrackedActor(string command, string revocation)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, target) = await Seed(f, command);
        await using var db = f.ScopedDb(f.VendorId);
        _ = await db.Users.SingleAsync(u => u.Id == actor.Id); // Deliberately stale identity map.
        if (revocation == "disabled")
            Assert.Equal(1, await f.Db.Users.IgnoreQueryFilters().Where(u => u.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)));
        else if (revocation == "demoted")
            Assert.Equal(1, await f.Db.Users.IgnoreQueryFilters().Where(u => u.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Cashier)));
        else
            Assert.Equal(1, await f.Db.Users.IgnoreQueryFilters().Where(u => u.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true)));

        var error = await Assert.ThrowsAsync<PosUserManagementException>(() => Execute(Service(db, actor), command, actor, target));
        Assert.Equal("POS_PERMISSION_FORBIDDEN", error.Code);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await f.Db.PosUserSecurityTokens.IgnoreQueryFilters().ToListAsync());
        f.Db.ChangeTracker.Clear();
        Assert.Equal(UserRole.Cashier, (await f.Db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == target.Id)).Role);
        Assert.Equal(2, await f.Db.Users.IgnoreQueryFilters().CountAsync());
    }

    [Theory]
    [InlineData("invite")]
    [InlineData("resend")]
    [InlineData("profile")]
    [InlineData("role")]
    [InlineData("status")]
    [InlineData("password")]
    [InlineData("session")]
    public async Task ActorLockLivesThroughCommandAuditAndFailureRollsBack(string command)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, target) = await Seed(f, command);
        var probe = new AuditLockProbe(f.ConnectionString, actor.Id);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(probe).Options,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(Service(db, actor), command, actor, target));
        Assert.True(probe.ProvedLock);
        f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await f.Db.PosUserSecurityTokens.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(2, await f.Db.Users.IgnoreQueryFilters().CountAsync());
        var current = await f.Db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == target.Id);
        Assert.Equal(target.IsActive, current.IsActive);
        Assert.Equal(target.FirstName, current.FirstName);
        Assert.Equal(target.RefreshToken, current.RefreshToken);
        Assert.Equal(UserRole.Cashier, current.Role);
    }

    [Fact]
    public async Task ParallelRoleChangesWithOneVersionHaveExactlyOneWinner()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, target) = await Seed(f, "role");
        var gate = new TwoLockEntrants();
        async Task<string> Change(string role)
        {
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(f.ConnectionString).AddInterceptors(gate).Options,
                new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
            try
            {
                await Service(db, actor).ChangeRoleAsync(actor.Id, target.Id, new ChangePosUserRoleRequest { Role = role },
                    target.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), default);
                return "committed";
            }
            catch (PosUserManagementException error) { return error.Code; }
        }
        var results = await Task.WhenAll(Change("Manager"), Change("Warehouse")).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, r => r == "committed");
        Assert.Single(results, r => r != "committed");
        Assert.Single(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task OwnershipTransferFailureAfterDemotionPreservesLastOwner()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, target) = await Seed(f, "role");
        actor.Role = UserRole.Owner;
        await f.Db.SaveChangesAsync();
        await f.Db.Entry(actor).ReloadAsync();
        var probe = new AuditLockProbe(f.ConnectionString, actor.Id);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(probe).Options,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db, actor).TransferOwnershipAsync(actor.Id, target.Id,
            actor.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            target.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), default));
        Assert.True(probe.ProvedLock);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(actor.Id, (await f.Db.Users.IgnoreQueryFilters().SingleAsync(u => u.Role == UserRole.Owner)).Id);
        Assert.Equal(UserRole.Cashier, (await f.Db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == target.Id)).Role);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvitationRevocationHoldsActorAndTargetAndAtomicallyCommitsOrRollsBackTokenSql(bool failAfterSqlWrites)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, target) = await Seed(f, "revoke-invitation");
        var token = new PosUserSecurityToken
        {
            TenantId = f.VendorId, UserId = target.Id, Purpose = PosUserSecurityTokenPurpose.Invitation,
            TokenHash = new string('c', 64), ExpiresAtUtc = DateTime.UtcNow.AddHours(1), CreatedByUserId = actor.Id
        };
        f.Db.Add(token); await f.Db.SaveChangesAsync();
        var before = await f.Db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == target.Id);
        var probe = new InvitationSqlWriteProbe(f.ConnectionString, actor.Id, target.Id, token.Id, failAfterSqlWrites);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(probe).Options,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        Task Revoke() => Service(db, actor).RevokeInvitationAsync(actor.Id, target.Id, "Disposable contract revocation",
            before.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), default);
        if (failAfterSqlWrites) await Assert.ThrowsAsync<InvalidOperationException>(Revoke);
        else await Revoke();
        Assert.True(probe.ObservedTokenSqlWrite); Assert.True(probe.ProvedActorLock); Assert.True(probe.ProvedTargetLock);
        f.Db.ChangeTracker.Clear();
        var persistedToken = await f.Db.PosUserSecurityTokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == token.Id);
        var persistedUser = await f.Db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == target.Id);
        Assert.Null(persistedToken.ConsumedAtUtc);
        if (failAfterSqlWrites)
        {
            Assert.Null(persistedToken.RevokedAtUtc); Assert.Equal(before.UpdatedAt, persistedUser.UpdatedAt);
            Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
        }
        else
        {
            Assert.NotNull(persistedToken.RevokedAtUtc);
            Assert.Single(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
        }
        Assert.False(persistedUser.IsActive); Assert.Equal(before.Role, persistedUser.Role);
        Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed class InvitationSqlWriteProbe(string connection, Guid actorId, Guid targetId, Guid tokenId, bool fail)
        : SaveChangesInterceptor
    {
        internal bool ObservedTokenSqlWrite { get; private set; }
        internal bool ProvedActorLock { get; private set; }
        internal bool ProvedTargetLock { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            Assert.NotNull(db.Database.CurrentTransaction);
            var token = await db.Set<PosUserSecurityToken>().IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(t => t.Id == tokenId, cancellationToken);
            Assert.NotNull(token.RevokedAtUtc); ObservedTokenSqlWrite = true;
            async Task ProveLocked(Guid id)
            {
                await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync(cancellationToken);
                await using var tx = await observer.BeginTransactionAsync(cancellationToken);
                await using var command = new NpgsqlCommand("SELECT \"Id\" FROM \"Users\" WHERE \"Id\"=@id FOR UPDATE NOWAIT", observer, tx);
                command.Parameters.AddWithValue("id", id);
                var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync(cancellationToken));
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
                await tx.RollbackAsync(cancellationToken);
            }
            await ProveLocked(actorId); ProvedActorLock = true;
            await ProveLocked(targetId); ProvedTargetLock = true;
            if (fail) throw new InvalidOperationException("Controlled invitation failure after actual token/user/audit SQL writes before commit");
            return result;
        }
    }

    private static async Task<(User Actor, User Target)> Seed(ProductionCoordinationAuthorityTests.Fixture f, string command)
    {
        var pending = command is "resend" or "revoke-invitation";
        User Make(string name, UserRole role) => new() { Id = Guid.NewGuid(), TenantId = f.VendorId,
            Username = name, NormalizedUsername = name.ToUpperInvariant(), Email = name + "@example.invalid",
            NormalizedEmail = name.ToUpperInvariant() + "@EXAMPLE.INVALID", PasswordHash = "not-a-login",
            FirstName = name, LastName = "Fixture", Role = role, IsActive = true, RefreshToken = "fixture-session", RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(1) };
        var actor = Make("app17f-admin", UserRole.Admin);
        var target = Make("app17f-target", UserRole.Cashier);
        if (pending) { target.IsActive = false; target.InvitedAtUtc = DateTime.UtcNow; }
        f.Db.AddRange(actor, target); await f.Db.SaveChangesAsync();
        await f.Db.Entry(actor).ReloadAsync(); await f.Db.Entry(target).ReloadAsync();
        return (actor, target);
    }

    private static PosUserManagementService Service(ApplicationDbContext db, User actor) => new(db,
        new PointOfSale.Tests.Infrastructure.TestTenantContext(db.Users.IgnoreQueryFilters().Select(u => u.TenantId).First()),
        new PasswordHasher<User>(), new PointOfSale.Tests.Shipping.GeographicTestHttpContextAccessor { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, actor.Id.ToString()), new(ClaimTypes.Role, actor.Role.ToString()),
                new("tenant_id", actor.TenantId.ToString()), new(PosSecurityConstants.SessionBindingClaim, "fixture-session"),
                new(PosSecurityConstants.SqlAuthorityClaim, PosSecurityConstants.SqlAuthorityValue)], "fixture-pos")) } }, Options.Create(new PosUserManagementOptions()),
        NullLogger<PosUserManagementService>.Instance);

    private static async Task Execute(PosUserManagementService service, string command, User actor, User target)
    {
        var version = target.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (command)
        {
            case "invite": await service.InviteAsync(actor.Id, new InvitePosUserRequest { Username = "app17f-new", Email = "new@example.invalid", FirstName = "New", LastName = "Fixture", Role = "Cashier" }, default); break;
            case "resend": await service.ResendInvitationAsync(actor.Id, target.Id, version, default); break;
            case "revoke-invitation": await service.RevokeInvitationAsync(actor.Id, target.Id, "fixture", version, default); break;
            case "profile": await service.UpdateProfileAsync(actor.Id, target.Id, new UpdatePosUserProfileRequest { FirstName = "Changed", LastName = "Fixture", Email = target.Email }, version, default); break;
            case "role": await service.ChangeRoleAsync(actor.Id, target.Id, new ChangePosUserRoleRequest { Role = "Manager" }, version, default); break;
            case "status": await service.ChangeStatusAsync(actor.Id, target.Id, new ChangePosUserStatusRequest { IsActive = false, Reason = "fixture" }, version, default); break;
            case "password": await service.IssuePasswordResetAsync(actor.Id, target.Id, version, default); break;
            case "session": await service.RevokeSessionAsync(actor.Id, target.Id, "fixture", version, default); break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private sealed class AuditLockProbe(string connection, Guid actorId) : SaveChangesInterceptor
    {
        public bool ProvedLock { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!data.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added)) return result;
            await using var sql = new NpgsqlConnection(connection); await sql.OpenAsync(cancellationToken);
            await using var tx = await sql.BeginTransactionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT \"Id\" FROM \"Users\" WHERE \"Id\"=@id FOR UPDATE NOWAIT", sql, tx);
            command.Parameters.AddWithValue("id", actorId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync(cancellationToken));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            ProvedLock = true;
            throw new InvalidOperationException("APP17F controlled audit rollback");
        }
    }

    private sealed class TwoLockEntrants : DbCommandInterceptor
    {
        private int _arrivals;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT 1 FROM \"Users\"", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _both.TrySetResult();
                await _both.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
}
