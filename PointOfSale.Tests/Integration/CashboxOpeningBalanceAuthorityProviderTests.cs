using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs;
using PointOfSale.API.Services.Audit;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Integration;

// Zero-only opening metadata on isolated PostgreSQL18, never a cash posting,
// application host, real transport or certification of all treasury writers.
public sealed class CashboxOpeningBalanceAuthorityProviderTests
{
    private static SetOpeningBalanceRequest Request() => new() { Amount = 0m, Date = DateTime.UtcNow.Date };

    private static CashboxController Controller(ApplicationDbContext db, Guid tenant, Guid actor) => new(
        db, new PointOfSale.Tests.Infrastructure.TestTenantContext(tenant), NullLogger<CashboxController>.Instance,
        Mock.Of<ITreasuryOperationService>(MockBehavior.Strict), Mock.Of<ITreasuryReadService>(MockBehavior.Strict),
        Mock.Of<ICashboxLedgerAccountService>(), Mock.Of<IAuditService>(MockBehavior.Strict))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
    };

    private static async Task<(User Actor, Cashbox Box)> SeedAsync(ProductionCoordinationAuthorityTests.Fixture f)
    {
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "opening-authority",
            Email = "opening-authority@example.invalid", PasswordHash = "fixture-not-a-login", Role = UserRole.Admin, IsActive = true };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Persisted opening fixture", IsActive = false };
        f.Db.AddRange(actor, box); await f.Db.SaveChangesAsync(); return (actor, box);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("owner")]
    [InlineData("manager")]
    [InlineData("cashier")]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("audit-failure")]
    public async Task ZeroOpeningRequiresCurrentOwnerOrAdminAndAtomicDurableAudit(string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, box) = await SeedAsync(f);
        if (state == "owner") actor.Role = UserRole.Owner;
        if (state == "manager") actor.Role = UserRole.Manager;
        if (state == "cashier") actor.Role = UserRole.Cashier;
        if (state == "inactive") actor.IsActive = false;
        if (state == "deleted") actor.IsDeleted = true;
        await f.Db.SaveChangesAsync();
        var tenant = state == "foreign" ? Guid.NewGuid() : f.VendorId;
        var boundary = new AuditBoundary(fail: state == "audit-failure", pause: false);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(boundary).Options, new PointOfSale.Tests.Infrastructure.TestTenantContext(tenant));
        var response = (await Controller(db, tenant, state == "missing" ? Guid.NewGuid() : actor.Id)
            .SetOpeningBalance(box.Id, Request())).Result;
        var saved = await f.Db.Cashboxes.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var audits = await f.Db.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        await ReceiptAsync(new { state, Result = response?.GetType().Name, saved.OpeningBalanceAmount, saved.IsActive,
            AuditCount = audits.Count, boundary.SawAuditInTransaction, TransactionDisposed = db.Database.CurrentTransaction == null });
        if (state is "admin" or "owner")
        {
            Assert.IsType<OkObjectResult>(response); Assert.Equal(0m, saved.OpeningBalanceAmount); Assert.True(saved.IsActive);
            var audit = Assert.Single(audits); Assert.Equal("CashboxSetOpeningBalance", audit.ActionType);
            Assert.Equal(actor.Id, audit.UserId); Assert.Equal(f.VendorId, audit.TenantId); Assert.Equal(box.Id.ToString(), audit.EntityId);
            Assert.True(boundary.SawAuditInTransaction);
        }
        else
        {
            if (state == "audit-failure") Assert.Equal(500, Assert.IsType<ObjectResult>(response).StatusCode);
            else Assert.IsType<ForbidResult>(response);
            Assert.Null(saved.OpeningBalanceAmount); Assert.False(saved.IsActive); Assert.Empty(audits);
        }
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(await f.Db.LedgerEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await f.Db.JournalEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await f.Db.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task OpeningPreservesPendingCallerSubjectWithoutCommittingIt(EntityState state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, box) = await SeedAsync(f);
        await using var db = f.ScopedDb(f.VendorId);
        var tracked = await db.Cashboxes.SingleAsync(); tracked.Name = "Caller pending opening name";
        db.Entry(tracked).State = state;
        var result = (await Controller(db, f.VendorId, actor.Id).SetOpeningBalance(box.Id, Request())).Result;
        var saved = await f.Db.Cashboxes.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        await ReceiptAsync(new { RequestedState = state.ToString(), RetainedState = db.Entry(tracked).State.ToString(),
            tracked.Name, PersistedName = saved.Name, saved.OpeningBalanceAmount, Result = result?.GetType().Name });
        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(state, db.Entry(tracked).State); Assert.Equal("Caller pending opening name", tracked.Name);
        Assert.Equal(box.Name, saved.Name); Assert.Null(saved.OpeningBalanceAmount); Assert.False(saved.IsActive);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task OpeningCannotFlushAnUnrelatedPendingCallerIdentity(EntityState state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, box) = await SeedAsync(f);
        await using var db = f.ScopedDb(f.VendorId);
        var tracked = await db.Users.SingleAsync(); tracked.Username = "Caller pending identity";
        db.Entry(tracked).State = state;
        var result = (await Controller(db, f.VendorId, actor.Id).SetOpeningBalance(box.Id, Request())).Result;
        var saved = await f.Db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(state, db.Entry(tracked).State); Assert.Equal("Caller pending identity", tracked.Username);
        Assert.Equal(actor.Username, saved.Username); Assert.False(saved.IsDeleted);
        await using var persisted = f.ScopedDb(f.VendorId);
        Assert.Null((await persisted.Cashboxes.AsNoTracking().SingleAsync(c => c.Id == box.Id)).OpeningBalanceAmount);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync()); Assert.Null(db.Database.CurrentTransaction);
        await ReceiptAsync(new { scope = "Unrelated caller identity preservation, not role/session workflow certification", requestedState = state.ToString(),
            retainedState = db.Entry(tracked).State.ToString(), saved.Username, saved.IsDeleted });
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("subject")]
    public async Task OpeningHoldsCurrentActorAndSubjectUntilItsAuditAndMetadataCommit(string identity)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, box) = await SeedAsync(f);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        var pause = new AuditBoundary(fail: false, pause: true);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        var pending = Controller(db, f.VendorId, actor.Id).SetOpeningBalance(box.Id, Request(), ct);
        string? sqlState = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await using var other = new NpgsqlConnection(f.ConnectionString); await other.OpenAsync(ct);
            await using var tx = await other.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand(identity == "actor"
                ? "SELECT \"Id\" FROM \"Users\" WHERE \"Id\"=@id FOR UPDATE NOWAIT"
                : "SELECT \"Id\" FROM \"Cashboxes\" WHERE \"Id\"=@id FOR UPDATE NOWAIT", other, tx);
            command.Parameters.AddWithValue("id", identity == "actor" ? actor.Id : box.Id);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync(ct)); sqlState = error.SqlState;
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, sqlState);
            // An independent connection cannot observe either uncommitted success fact.
            Assert.Null((await f.Db.Cashboxes.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct)).OpeningBalanceAmount);
            Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync(ct));
            await tx.RollbackAsync(ct);
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await pending; } catch { /* Drain owned work without replacing the original failure. */ }
        }
        Assert.IsType<OkObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(10), ct)).Result);
        await ReceiptAsync(new { identity, SqlState = sqlState, pause.SawAuditInTransaction, CommittedAuditCount = await f.Db.AuditLogs.IgnoreQueryFilters().CountAsync(ct) });
        Assert.Single(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync(ct)); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("defined")]
    [InlineData("deleted")]
    [InlineData("foreign")]
    public async Task OpeningRevalidatesCommittedPostWaitSubjectInsteadOfCachedEligibility(string turnover)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, box) = await SeedAsync(f); var otherTenant = Guid.NewGuid();
        f.Db.Tenants.Add(new Tenant { Id = otherTenant, Name = "Fictional opening turnover", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        await f.Db.SaveChangesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        await using var db = f.ScopedDb(f.VendorId); await db.Cashboxes.SingleAsync(ct); await db.Database.OpenConnectionAsync(ct);
        await using var holder = new NpgsqlConnection(f.ConnectionString); await holder.OpenAsync(ct);
        await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync(ct);
        await using var tx = await holder.BeginTransactionAsync(ct);
        var sql = turnover switch {
            "defined" => "UPDATE \"Cashboxes\" SET \"OpeningBalanceAmount\"=0 WHERE \"Id\"=@id",
            "deleted" => "UPDATE \"Cashboxes\" SET \"IsDeleted\"=TRUE WHERE \"Id\"=@id",
            _ => "UPDATE \"Cashboxes\" SET \"TenantId\"=@tenant WHERE \"Id\"=@id" };
        await using (var update = new NpgsqlCommand(sql, holder, tx))
        {
            update.Parameters.AddWithValue("id", box.Id);
            if (turnover == "foreign") update.Parameters.AddWithValue("tenant", otherTenant);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(ct));
        }
        var pending = Controller(db, f.VendorId, actor.Id).SetOpeningBalance(box.Id, Request(), ct); var committed = false; var blocked = false;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !pending.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", observer);
                probe.Parameters.AddWithValue("holder", holder.ProcessID); probe.Parameters.AddWithValue("waiter", ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            Assert.True(blocked, "Opening must wait on the exact independent subject owner before revalidating eligibility.");
            await tx.CommitAsync(ct); committed = true;
            var result = (await pending.WaitAsync(TimeSpan.FromSeconds(10), ct)).Result;
            if (turnover == "defined") Assert.IsType<BadRequestObjectResult>(result); else Assert.IsType<NotFoundObjectResult>(result);
            var saved = await f.Db.Cashboxes.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct);
            await ReceiptAsync(new { turnover, ExactBackendBlockerObserved = blocked, Result = result?.GetType().Name, saved.OpeningBalanceAmount, saved.TenantId, saved.IsDeleted });
            Assert.False(saved.IsActive); Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync(ct)); Assert.Null(db.Database.CurrentTransaction);
        }
        finally
        {
            if (!committed) await tx.RollbackAsync(CancellationToken.None);
            try { await pending; } catch { /* Drain owned work without replacing the original failure. */ }
        }
    }

    private sealed class AuditBoundary(bool fail, bool pause) : SaveChangesInterceptor
    {
        internal bool SawAuditInTransaction { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added && e.Entity.ActionType == "CashboxSetOpeningBalance"))
            {
                Assert.NotNull(data.Context.Database.CurrentTransaction); SawAuditInTransaction = true; Entered.TrySetResult();
                if (fail) throw new InvalidOperationException("Controlled opening-balance audit persistence failure");
                if (pause) await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private static async Task ReceiptAsync(object receipt)
    {
        var directory = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await using var stream = new FileStream(Path.Combine(directory, "opening-balance-authority-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, receipt);
    }
}
