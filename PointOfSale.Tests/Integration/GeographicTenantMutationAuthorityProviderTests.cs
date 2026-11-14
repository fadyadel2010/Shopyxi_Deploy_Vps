using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PointOfSale.API.DTOs.GeographicAdministration;
using PointOfSale.API.Security;
using PointOfSale.API.Services.Admin;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Geographic;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Fictional geography metadata only. No carrier/network client or host exists.
public sealed class GeographicTenantMutationAuthorityProviderTests
{
    public static IEnumerable<object[]> CommandsAndActors() =>
        from command in new[] { "draft", "sync", "override" }
        from state in new[] { "valid", "inactive", "deleted", "demoted", "expired-session", "wrong-tenant", "wrong-subject", "forged-audit-role" }
        select new object[] { command, state };

    private static async Task<(User Actor, CarrierProvider Provider, GeographicCatalogRelease Catalog, GeographicPlace Place, IHttpContextAccessor Http)> SeedAsync(
        ProductionCoordinationAuthorityTests.Fixture f)
    {
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "geo-owned-actor",
            Email = "geo-owned@example.invalid", PasswordHash = "fixture-not-a-login", Role = UserRole.Admin, IsActive = true,
            RefreshToken = "fictional-pos-geographic-session", RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(1) };
        var provider = new CarrierProvider { Id = Guid.NewGuid(), CarrierCode = "fictional", DisplayName = "No remote carrier",
            IsActive = true, SupportsGeographicMappings = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        var catalog = new GeographicCatalogRelease { Id = Guid.NewGuid(), ReleaseNumber = 1, ReleaseKey = "fictional-authority",
            SourceSummary = "Fictional authority fixture", ChecksumSha256 = new string('a', 64), CreatedByUserId = actor.Id,
            CreatedAtUtc = DateTime.UtcNow, Status = GeographicReleaseStatus.Draft };
        var place = new GeographicPlace { Id = Guid.NewGuid(), IntroducedCatalogReleaseId = catalog.Id,
            PlaceKind = GeographicPlaceKind.Country, CanonicalCode = "ZZ", Status = GeographicLifecycleStatus.Active,
            CreatedAtUtc = DateTime.UtcNow };
        f.Db.AddRange(actor, provider, catalog, place); await f.Db.SaveChangesAsync();
        IHttpContextAccessor http = new PointOfSale.Tests.Shipping.GeographicTestHttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin"),
            new Claim("tenant_id", f.VendorId.ToString()), new Claim(PosSecurityConstants.SessionBindingClaim, actor.RefreshToken),
            new Claim(PosSecurityConstants.SqlAuthorityClaim, PosSecurityConstants.SqlAuthorityValue)], "fixture-pos")) } };
        return (actor, provider, catalog, place, http);
    }

    private static Task<GeographicOperationResultDto> ExecuteAsync(GeographicAdministrationService service, string command,
        Guid tenant, Guid actor, Guid provider, Guid catalog, Guid place, string actorRole = "Admin") => command switch
    {
        "draft" => service.CreateMappingDraftAsync(new(provider, 1, "fictional-draft", CarrierGeographicEnvironment.Test,
            CarrierGeographicAccountScopeKind.TenantProviderAccount, tenant, tenant, catalog, "Fictional fixture",
            DateTime.UtcNow, null, null, "SQL authority regression"), actor, actorRole),
        "sync" => service.RecordSyncRunAsync(new(provider, CarrierGeographicEnvironment.Test,
            CarrierGeographicAccountScopeKind.TenantProviderAccount, tenant, tenant, CarrierGeographicSyncSourceKind.ManualImport,
            "https://example.invalid/no-network", "fictional", new string('b', 64), null, "SQL authority regression"), actor, actorRole),
        "override" => service.CreateOverrideAsync(new(provider, CarrierGeographicEnvironment.Test,
            CarrierGeographicAccountScopeKind.TenantProviderAccount, tenant, tenant, place, "country", "fixture-ZZ", null,
            "Fictional override", DateTime.UtcNow, null, "SQL authority regression"), actor, actorRole),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    [Theory]
    [MemberData(nameof(CommandsAndActors))]
    public async Task EveryRootWriterRequiresCurrentSqlTenantActorAndSession(string command, string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, provider, catalog, place, http) = await SeedAsync(f);
        if (state == "inactive") actor.IsActive = false;
        if (state == "deleted") actor.IsDeleted = true;
        if (state == "demoted") actor.Role = UserRole.Cashier;
        if (state == "expired-session") actor.RefreshTokenExpiryTime = DateTime.UtcNow.AddMinutes(-1);
        if (state == "wrong-tenant")
        {
            var other = new Tenant { Id = Guid.NewGuid(), Name = "Fictional foreign tenant", Currency = "EGP", IsActive = true, Status = TenantStatus.Active };
            f.Db.Add(other); actor.TenantId = other.Id;
        }
        await f.Db.SaveChangesAsync();
        await using var db = f.ScopedDb(f.VendorId);
        var service = new GeographicAdministrationService(db, http);
        Task<GeographicOperationResultDto> Action() => ExecuteAsync(service, command, f.VendorId,
            state == "wrong-subject" ? Guid.NewGuid() : actor.Id, provider.Id, catalog.Id, place.Id,
            state == "forged-audit-role" ? "Owner" : "Admin");
        if (state == "valid") Assert.True((await Action()).Succeeded);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(Action);
        Assert.Null(db.Database.CurrentTransaction);
        await using var fresh = f.ScopedDb(f.VendorId);
        Assert.Equal(state == "valid" && command == "draft" ? 1 : 0, await fresh.CarrierGeographicMappingReleases.CountAsync());
        Assert.Equal(state == "valid" && command == "sync" ? 1 : 0, await fresh.CarrierGeographicSyncRuns.CountAsync());
        Assert.Equal(state == "valid" && command == "override" ? 1 : 0, await fresh.CarrierGeographicOverrides.CountAsync());
        var audits = await fresh.GeographicAudit.ToListAsync();
        if (state == "valid")
        {
            var audit = Assert.Single(audits); Assert.Equal(f.VendorId, audit.TenantId);
            Assert.Equal(actor.Id, audit.ActorId); Assert.Equal(GeographicAuditActorKind.PosAdmin, audit.ActorKind);
        }
        else Assert.Empty(audits);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    public async Task MetadataAndAuditCommitRetainsTheExactCurrentAuthorityLocks(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, provider, catalog, place, http) = await SeedAsync(f);
        var pause = new PauseAudit();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        var pending = ExecuteAsync(new GeographicAdministrationService(db, http), "draft", f.VendorId,
            actor.Id, provider.Id, catalog.Id, place.Id);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        string? observed = null;
        try
        {
            await using var other = new NpgsqlConnection(f.ConnectionString); await other.OpenAsync();
            await using var tx = await other.BeginTransactionAsync();
            await using var probe = new NpgsqlCommand(identity == "actor"
                ? "SELECT 1 FROM \"Users\" WHERE \"Id\"=@id FOR UPDATE NOWAIT"
                : "SELECT 1 FROM \"Tenants\" WHERE \"Id\"=@id FOR UPDATE NOWAIT", other, tx);
            probe.Parameters.AddWithValue("id", identity == "actor" ? actor.Id : f.VendorId);
            try { await probe.ExecuteScalarAsync(); }
            catch (PostgresException error) { observed = error.SqlState; }
        }
        finally { pause.Release.TrySetResult(); }
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Succeeded);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, observed);
        Assert.Single(await f.Db.GeographicAudit.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("sync")]
    [InlineData("override")]
    public async Task AuditFailureRollsBackTheCanonicalMetadataWriter(string command)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, provider, catalog, place, http) = await SeedAsync(f);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(new FailAudit()).Options,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            new GeographicAdministrationService(db, http), command, f.VendorId, actor.Id, provider.Id, catalog.Id, place.Id));
        Assert.Equal("GEOGRAPHIC-AUDIT-ROLLBACK", error.Message);
        Assert.Null(db.Database.CurrentTransaction);
        await using var fresh = f.ScopedDb(f.VendorId);
        Assert.Empty(await fresh.CarrierGeographicMappingReleases.ToListAsync());
        Assert.Empty(await fresh.CarrierGeographicSyncRuns.ToListAsync());
        Assert.Empty(await fresh.CarrierGeographicOverrides.ToListAsync());
        Assert.Empty(await fresh.GeographicAudit.ToListAsync());
    }

    [Fact]
    public async Task RevocationCommittedWhileActorAdmissionWaitsCannotAuthorizeMetadata()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, provider, catalog, place, http) = await SeedAsync(f);
        await using var holder = new NpgsqlConnection(f.ConnectionString); await holder.OpenAsync();
        await using var held = await holder.BeginTransactionAsync();
        await using var revoke = new NpgsqlCommand("UPDATE \"Users\" SET \"IsActive\"=false WHERE \"Id\"=@id", holder, held);
        revoke.Parameters.AddWithValue("id", actor.Id); Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        await using var db = f.ScopedDb(f.VendorId); await db.Database.OpenConnectionAsync();
        var pending = ExecuteAsync(new GeographicAdministrationService(db, http), "draft", f.VendorId,
            actor.Id, provider.Id, catalog.Id, place.Id);
        var observed = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!observed && !pending.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", holder, held);
                probe.Parameters.AddWithValue("holder", holder.ProcessID);
                probe.Parameters.AddWithValue("waiter", ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
                observed = (bool)(await probe.ExecuteScalarAsync(deadline.Token))!;
                if (!observed) await Task.Delay(20, deadline.Token);
            }
        }
        finally { await held.CommitAsync(); }
        var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(observed, "The actual service must wait on this exact independently revoked actor.");
        Assert.True(error is UnauthorizedAccessException || error is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure },
            "A revoked post-wait actor must be denied or force serializable retry, never commit metadata.");
        Assert.Null(db.Database.CurrentTransaction);
        await using var fresh = f.ScopedDb(f.VendorId);
        Assert.Empty(await fresh.CarrierGeographicMappingReleases.ToListAsync()); Assert.Empty(await fresh.GeographicAudit.ToListAsync());
    }

    public static IEnumerable<object[]> SecondaryCommandsAndScopes() =>
        from command in new[] { "mapping", "candidate", "review", "approve-override" }
        from state in new[] { "valid", "inactive", "foreign-target" }
        select new object[] { command, state };

    [Theory]
    [MemberData(nameof(SecondaryCommandsAndScopes))]
    public async Task SecondaryWritersBindCurrentAuthorityAndPersistedTargetScope(string command, string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, provider, catalog, place, http) = await SeedAsync(f);
        Guid releaseId, runId, overrideId, candidateId;
        await using (var setup = f.ScopedDb(f.VendorId))
        {
            var service = new GeographicAdministrationService(setup, http);
            releaseId = (await ExecuteAsync(service, "draft", f.VendorId, actor.Id, provider.Id, catalog.Id, place.Id)).ResourceId!.Value;
            runId = (await ExecuteAsync(service, "sync", f.VendorId, actor.Id, provider.Id, catalog.Id, place.Id)).ResourceId!.Value;
            overrideId = (await ExecuteAsync(service, "override", f.VendorId, actor.Id, provider.Id, catalog.Id, place.Id)).ResourceId!.Value;
            candidateId = (await service.AddSyncCandidateAsync(new(runId, provider.Id, "country", "existing", null,
                "Fictional existing candidate", "FICTIONAL", null, place.Id, 100, "Fictional setup"), actor.Id, "Admin")).ResourceId!.Value;
        }
        if (state == "inactive") { actor.IsActive = false; await f.Db.SaveChangesAsync(); }
        if (state == "foreign-target")
        {
            var foreign = new Tenant { Id = Guid.NewGuid(), Name = "Fictional foreign metadata", Currency = "EGP", IsActive = true, Status = TenantStatus.Active };
            f.Db.Tenants.Add(foreign);
            var release = await f.Db.CarrierGeographicMappingReleases.SingleAsync(r => r.Id == releaseId);
            var run = await f.Db.CarrierGeographicSyncRuns.SingleAsync(r => r.Id == runId);
            var item = await f.Db.CarrierGeographicOverrides.SingleAsync(r => r.Id == overrideId);
            release.TenantId = release.AccountScopeId = foreign.Id;
            run.TenantId = run.AccountScopeId = foreign.Id;
            item.TenantId = item.AccountScopeId = foreign.Id;
            await f.Db.SaveChangesAsync();
        }
        var before = await MetadataSnapshotAsync(f);
        await using var db = f.ScopedDb(f.VendorId);
        var current = new GeographicAdministrationService(db, http);
        Task<GeographicOperationResultDto> Act() => command switch
        {
            "mapping" => current.AddMappingAsync(new(releaseId, null, provider.Id, place.Id, place.Id,
                GeographicPlaceKind.Country, "country", "fixture-mapping", null, "Fictional mapping", "FICTIONAL", null,
                "ShipmentRegistration", CarrierGeographicMappingStatus.Active, CarrierGeographicVerificationStatus.Reviewed,
                "fictional", DateTime.UtcNow, null, "Fictional direct writer"), actor.Id, "Admin"),
            "candidate" => current.AddSyncCandidateAsync(new(runId, provider.Id, "country", "new-candidate", null,
                "Fictional new candidate", "FICTIONAL", null, place.Id, 100, "Fictional direct writer"), actor.Id, "Admin"),
            "review" => current.ReviewSyncCandidateAsync(candidateId, CarrierGeographicSyncCandidateStatus.Approved,
                new("Fictional direct review"), actor.Id, "Admin"),
            "approve-override" => current.ApproveOverrideAsync(overrideId, new("Fictional direct approval"), actor.Id, "Admin"),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        if (state == "inactive") await Assert.ThrowsAsync<UnauthorizedAccessException>(Act);
        else
        {
            var result = await Act();
            Assert.Equal(state == "valid", result.Succeeded);
            if (state == "foreign-target") Assert.Equal(command switch {
                "mapping" => "MappingDraftRequired", "candidate" => "SyncRunNotFound", "review" => "SyncCandidateNotFound",
                _ => "CarrierOverrideNotFound" }, result.FailureCode);
        }
        Assert.Null(db.Database.CurrentTransaction);
        var after = await MetadataSnapshotAsync(f);
        if (state == "valid")
        {
            Assert.NotEqual(before, after);
            await using var readback = f.ScopedDb(f.VendorId);
            var audit = await readback.GeographicAudit.OrderByDescending(a => a.OccurredAtUtc).FirstAsync();
            Assert.Equal(actor.Id, audit.ActorId); Assert.Equal(f.VendorId, audit.TenantId); Assert.Equal("Admin", audit.ActorRole);
            if (command == "mapping") Assert.Single(await readback.CarrierGeographicMappings.ToListAsync());
            if (command == "candidate") Assert.Equal(2, await readback.CarrierGeographicSyncCandidates.CountAsync());
            if (command == "review") Assert.Equal(CarrierGeographicSyncCandidateStatus.Approved, (await readback.CarrierGeographicSyncCandidates.SingleAsync()).CandidateStatus);
            if (command == "approve-override") Assert.Equal(GeographicOverrideStatus.Approved, (await readback.CarrierGeographicOverrides.SingleAsync()).Status);
        }
        else Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerPendingStateAndCallerTransactionCannotBeAdopted(bool callerTransaction)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, provider, catalog, place, http) = await SeedAsync(f);
        await using var db = f.ScopedDb(f.VendorId);
        await using var outer = callerTransaction ? await db.Database.BeginTransactionAsync() : null;
        if (!callerTransaction)
        {
            var tracked = await db.Users.SingleAsync(u => u.Id == actor.Id);
            tracked.Username = "pending-caller-change-must-not-be-saved";
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(new GeographicAdministrationService(db, http),
            "draft", f.VendorId, actor.Id, provider.Id, catalog.Id, place.Id));
        Assert.Same(outer, db.Database.CurrentTransaction);
        if (outer != null) await outer.RollbackAsync();
        await using var readback = f.ScopedDb(f.VendorId);
        Assert.Equal(actor.Username, (await readback.Users.SingleAsync(u => u.Id == actor.Id)).Username);
        Assert.Empty(await readback.CarrierGeographicMappingReleases.ToListAsync()); Assert.Empty(await readback.GeographicAudit.ToListAsync());
    }

    private static async Task<string> MetadataSnapshotAsync(ProductionCoordinationAuthorityTests.Fixture f)
    {
        await using var db = f.ScopedDb(f.VendorId);
        return System.Text.Json.JsonSerializer.Serialize(new {
            releases = await db.CarrierGeographicMappingReleases.AsNoTracking().OrderBy(r => r.Id).ToListAsync(),
            mappings = await db.CarrierGeographicMappings.AsNoTracking().OrderBy(r => r.Id).ToListAsync(),
            runs = await db.CarrierGeographicSyncRuns.AsNoTracking().OrderBy(r => r.Id).ToListAsync(),
            candidates = await db.CarrierGeographicSyncCandidates.AsNoTracking().OrderBy(r => r.Id).ToListAsync(),
            overrides = await db.CarrierGeographicOverrides.AsNoTracking().OrderBy(r => r.Id).ToListAsync(),
            audits = await db.GeographicAudit.AsNoTracking().OrderBy(r => r.Id).ToListAsync()
        });
    }

    public static IEnumerable<object[]> MemoizedTenantRevocations() =>
        from command in new[] { "draft", "sync", "override" }
        from revocation in new[] { "inactive", "suspended", "deleted" }
        select new object[] { command, revocation };

    [Theory]
    [MemberData(nameof(MemoizedTenantRevocations))]
    public async Task EarlierRequestTenantMemoCannotAuthorizeARevokedTenantMutation(string command, string revocation)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, provider, catalog, place, http) = await SeedAsync(f);
        await using var db = f.ScopedDb(f.VendorId);
        Assert.True(await PointOfSale.Infrastructure.Reliability.TenantLineageAuthority.IsActiveTenantAsync(db, f.VendorId));
        var tenant = await f.Db.Tenants.SingleAsync(t => t.Id == f.VendorId);
        if (revocation == "inactive") tenant.IsActive = false;
        if (revocation == "suspended") tenant.Status = TenantStatus.Suspended;
        if (revocation == "deleted") tenant.DeletedAtUtc = DateTime.UtcNow;
        await f.Db.SaveChangesAsync();
        // Exercise the actual request-scoped cache, not an injected fake verdict.
        Assert.True(await PointOfSale.Infrastructure.Reliability.TenantLineageAuthority.IsActiveTenantAsync(db, f.VendorId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ExecuteAsync(new GeographicAdministrationService(db, http),
            command, f.VendorId, actor.Id, provider.Id, catalog.Id, place.Id));
        Assert.Null(db.Database.CurrentTransaction);
        await using var fresh = f.ScopedDb(f.VendorId);
        Assert.Empty(await fresh.CarrierGeographicMappingReleases.ToListAsync());
        Assert.Empty(await fresh.CarrierGeographicSyncRuns.ToListAsync()); Assert.Empty(await fresh.CarrierGeographicOverrides.ToListAsync());
        Assert.Empty(await fresh.GeographicAudit.ToListAsync());
    }

    private sealed class FailAudit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<GeographicAudit>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("GEOGRAPHIC-AUDIT-ROLLBACK");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PauseAudit : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<GeographicAudit>().Any(e => e.State == EntityState.Added))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            return result;
        }
    }
}
