using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.API.DTOs.Users;
using PointOfSale.API.Security;
using PointOfSale.API.Services.Users;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Direct canonical administrative service, exact owned PostgreSQL and fictional
// identity records only. No host, email sender, external client or real identity.
public sealed class PosUserTenantSessionAuthorityProviderTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("tenant-inactive")]
    [InlineData("tenant-suspended")]
    [InlineData("tenant-deleted")]
    [InlineData("tenant-banned")]
    [InlineData("session-expired")]
    [InlineData("session-rotated")]
    [InlineData("wrong-subject")]
    [InlineData("wrong-tenant-claim")]
    [InlineData("wrong-role-claim")]
    public async Task CanonicalRoleCommandRequiresCurrentTenantAndExactPresentedSession(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "fictional-admin",
            Email = "fictional-admin@example.invalid", PasswordHash = "not-a-login", Role = UserRole.Admin, IsActive = true,
            RefreshToken = "fictional-presented-session", RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(1) };
        var target = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "fictional-target",
            Email = "fictional-target@example.invalid", PasswordHash = "not-a-login", Role = UserRole.Cashier, IsActive = true };
        f.Db.AddRange(actor, target); await f.Db.SaveChangesAsync(); await f.Db.Entry(target).ReloadAsync();
        var tenant = await f.Db.Tenants.SingleAsync(t => t.Id == f.VendorId);
        if (state == "tenant-inactive") tenant.IsActive = false;
        if (state == "tenant-suspended") tenant.Status = TenantStatus.Suspended;
        if (state == "tenant-deleted") tenant.DeletedAtUtc = DateTime.UtcNow;
        if (state == "tenant-banned") f.Db.UserGovernanceProfiles.Add(new UserGovernanceProfile {
            Id = Guid.NewGuid(), SubjectId = f.VendorId, Role = UserGovernanceRole.Vendor,
            Status = UserGovernanceStatus.Banned, CreatedAtUtc = DateTime.UtcNow });
        if (state == "session-expired") actor.RefreshTokenExpiryTime = DateTime.UtcNow.AddMinutes(-1);
        if (state == "session-rotated") actor.RefreshToken = "fictional-new-session-not-presented";
        await f.Db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, (state == "wrong-subject" ? Guid.NewGuid() : actor.Id).ToString()),
            new(ClaimTypes.Role, state == "wrong-role-claim" ? "Owner" : "Admin"),
            new("tenant_id", (state == "wrong-tenant-claim" ? Guid.NewGuid() : f.VendorId).ToString()),
            new(PosSecurityConstants.SessionBindingClaim, "fictional-presented-session"),
            new(PosSecurityConstants.SqlAuthorityClaim, PosSecurityConstants.SqlAuthorityValue)], "fictional-pos"));
        await using var db = f.ScopedDb(f.VendorId);
        var http = new PointOfSale.Tests.Shipping.GeographicTestHttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        var service = new PosUserManagementService(db, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId),
            new PasswordHasher<User>(), http, Options.Create(new PosUserManagementOptions()), NullLogger<PosUserManagementService>.Instance);
        Task Act() => service.ChangeRoleAsync(actor.Id, target.Id, new ChangePosUserRoleRequest { Role = "Manager" },
            target.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), default);
        if (state == "valid") await Act();
        else
        {
            var error = await Assert.ThrowsAsync<PosUserManagementException>(Act);
            Assert.Equal("POS_PERMISSION_FORBIDDEN", error.Code);
        }
        Assert.Null(db.Database.CurrentTransaction);
        await using var fresh = f.ScopedDb(f.VendorId);
        Assert.Equal(state == "valid" ? UserRole.Manager : UserRole.Cashier,
            (await fresh.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == target.Id)).Role);
        Assert.Equal(state == "valid" ? 1 : 0, await fresh.AuditLogs.IgnoreQueryFilters().CountAsync());
        var profiles = await fresh.UserGovernanceProfiles.AsNoTracking().ToListAsync();
        Assert.Equal(state is "valid" or "tenant-banned" ? 1 : 0, profiles.Count);
        if (profiles.Count == 1)
        {
            Assert.Equal(state == "tenant-banned" ? UserGovernanceStatus.Banned : UserGovernanceStatus.Active, profiles[0].Status);
            Assert.False(profiles[0].IsKycVerified); Assert.False(profiles[0].IsMarketplaceApproved);
        }
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("existing-governance")]
    [InlineData("first-governance")]
    public async Task CanonicalRoleAndAuditCommitRetainsTenantAndFirstOrExistingGovernanceAuthority(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "fictional-lock-admin",
            Email = "lock-admin@example.invalid", PasswordHash = "not-a-login", Role = UserRole.Admin, IsActive = true,
            RefreshToken = "fictional-lock-session", RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(1) };
        var target = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "fictional-lock-target",
            Email = "lock-target@example.invalid", PasswordHash = "not-a-login", Role = UserRole.Cashier, IsActive = true };
        f.Db.AddRange(actor, target);
        var existingId = Guid.NewGuid();
        if (identity == "existing-governance") f.Db.UserGovernanceProfiles.Add(new() {
            Id = existingId, SubjectId = f.VendorId, Role = UserGovernanceRole.Vendor, Status = UserGovernanceStatus.Active,
            IsKycVerified = true, IsMarketplaceApproved = true, CreatedAtUtc = DateTime.UtcNow });
        await f.Db.SaveChangesAsync(); await f.Db.Entry(target).ReloadAsync();
        var pause = new PauseAudit();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString)
            .AddInterceptors(pause).Options, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        var http = new PointOfSale.Tests.Shipping.GeographicTestHttpContextAccessor { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, actor.Id.ToString()), new(ClaimTypes.Role, "Admin"), new("tenant_id", f.VendorId.ToString()),
                new(PosSecurityConstants.SessionBindingClaim, actor.RefreshToken)], "fixture-pos")) } };
        var service = new PosUserManagementService(db, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId),
            new PasswordHasher<User>(), http, Options.Create(new PosUserManagementOptions()), NullLogger<PosUserManagementService>.Instance);
        var pending = service.ChangeRoleAsync(actor.Id, target.Id, new ChangePosUserRoleRequest { Role = "Manager" },
            target.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), default);
        string? observed = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var probe = new NpgsqlConnection(f.ConnectionString); await probe.OpenAsync();
            await using var transaction = await probe.BeginTransactionAsync();
            await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout='300ms'", probe, transaction)) await timeout.ExecuteNonQueryAsync();
            var sql = identity == "tenant" ? "SELECT 1 FROM \"Tenants\" WHERE \"Id\"=@tenant FOR UPDATE NOWAIT" : """
                INSERT INTO "UserGovernanceProfiles"
                    ("Id","SubjectId","Role","Status","RiskLevel","IsKycVerified","IsMarketplaceApproved","SuspiciousFlags","CreatedAtUtc")
                VALUES (@id,@tenant,@role,@banned,0,FALSE,FALSE,0,clock_timestamp())
                ON CONFLICT ("Role","SubjectId") DO UPDATE SET "Status"=EXCLUDED."Status"
                """;
            await using var change = new NpgsqlCommand(sql, probe, transaction);
            change.Parameters.AddWithValue("tenant", f.VendorId);
            if (identity != "tenant")
            {
                change.Parameters.AddWithValue("id", Guid.NewGuid()); change.Parameters.AddWithValue("role", (int)UserGovernanceRole.Vendor);
                change.Parameters.AddWithValue("banned", (int)UserGovernanceStatus.Banned);
            }
            try { await change.ExecuteNonQueryAsync(); }
            catch (PostgresException error) { observed = error.SqlState; }
        }
        finally { pause.Release.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, observed);
        Assert.Null(db.Database.CurrentTransaction);
        await using var fresh = f.ScopedDb(f.VendorId);
        var profile = await fresh.UserGovernanceProfiles.AsNoTracking().SingleAsync();
        Assert.Equal(f.VendorId, profile.SubjectId); Assert.Equal(UserGovernanceRole.Vendor, profile.Role);
        Assert.Equal(UserGovernanceStatus.Active, profile.Status);
        Assert.Equal(identity == "existing-governance", profile.IsKycVerified);
        Assert.Equal(identity == "existing-governance", profile.IsMarketplaceApproved);
        if (identity == "existing-governance") Assert.Equal(existingId, profile.Id);
        Assert.Equal(UserRole.Manager, (await fresh.Users.SingleAsync(u => u.Id == target.Id)).Role);
        Assert.Single(await fresh.AuditLogs.ToListAsync());
    }

    private sealed class PauseAudit : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            return result;
        }
    }
}
