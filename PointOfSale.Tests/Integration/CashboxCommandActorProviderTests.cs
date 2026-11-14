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

// Actual PostgreSQL command authority; no HTTP host, middleware certification,
// treasury posting, cash transfer, account-mapping or external audit delivery.
public sealed class CashboxCommandActorProviderTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("open")]
    public async Task EachCommandHoldsItsActualPostgresActorRowUntilTheMetadataCommit(string command)
        => await ExecuteHeldRowAsync(command, subjectRow: false);

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("open")]
    public async Task ExistingCashboxSubjectOwnershipSurvivesTheAdmissionToMetadataAndAuditCommitBoundary(string command)
        => await ExecuteHeldRowAsync(command, subjectRow: true);

    private static async Task ExecuteHeldRowAsync(string command, bool subjectRow)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "held-actor", Email = "held-actor@example.invalid",
            PasswordHash = "fixture-not-a-login", IsActive = true, Role = UserRole.Admin };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Original fixture box", IsActive = false };
        f.Db.Users.Add(actor); if (command != "create") f.Db.Cashboxes.Add(box); await f.Db.SaveChangesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        var tenant = new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId);
        var pause = new BeforeMetadataSave();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(pause).Options, tenant);
        await db.Database.OpenConnectionAsync(ct);
        var treasury = new Mock<ITreasuryOperationService>(MockBehavior.Strict);
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        var controller = new CashboxController(db, tenant, NullLogger<CashboxController>.Instance, treasury.Object, reads.Object,
            Mock.Of<ICashboxLedgerAccountService>(), Mock.Of<IAuditService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
        };
        async Task<IActionResult?> Execute() => command switch
        {
            "create" => (await controller.CreateCashbox(new CreateCashboxRequest { Name = "New fixture box" }, ct)).Result,
            "update" => (await controller.UpdateCashbox(box.Id, new UpdateCashboxRequest { Name = "Changed fixture box" }, ct)).Result,
            "delete" => await controller.DeleteCashbox(box.Id, ct),
            "open" => (await controller.OpenCashbox(box.Id, new OpenCashboxRequest { OpeningBalance = 0m }, ct)).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        await using var revoker = new NpgsqlConnection(f.ConnectionString); await revoker.OpenAsync(ct);
        await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync(ct);
        await using var update = new NpgsqlCommand(subjectRow
            ? "UPDATE \"Cashboxes\" SET \"Name\"='Independently committed fixture name' WHERE \"Id\"=@id AND \"TenantId\"=@tenant"
            : "UPDATE \"Users\" SET \"IsActive\"=FALSE WHERE \"Id\"=@id AND \"TenantId\"=@tenant", revoker);
        update.Parameters.AddWithValue("id", subjectRow ? box.Id : actor.Id); update.Parameters.AddWithValue("tenant", f.VendorId);
        var pending = Execute(); Task<int>? revocation = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            revocation = update.ExecuteNonQueryAsync(ct);
            var blocked = false; var watch = System.Diagnostics.Stopwatch.StartNew();
            var holder = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !revocation.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", observer);
                probe.Parameters.AddWithValue("holder", holder); probe.Parameters.AddWithValue("waiter", revoker.ProcessID);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
            await File.WriteAllTextAsync(Path.Combine(output, (subjectRow ? "cashbox-command-held-subject-" : "cashbox-command-held-actor-") + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(new { command, subjectRow, Holder = holder, Waiter = revoker.ProcessID, ExactBackendBlockerObserved = blocked,
                    CommandPending = !pending.IsCompleted, RevocationPending = !revocation.IsCompleted,
                    Scope = "Actual independently connected PostgreSQL " + (subjectRow ? "cashbox rename" : "user revocation") + " must block before metadata save; no mock concurrency or remote operation" }), ct);
            Assert.True(blocked, "The independent mutation must wait on the exact admitted command's PostgreSQL row owner.");
            Assert.False(pending.IsCompleted); Assert.False(revocation.IsCompleted);
            pause.Release.TrySetResult(); var result = await pending;
            if (command == "create") Assert.IsType<CreatedAtActionResult>(result);
            else if (command == "delete") Assert.IsType<NoContentResult>(result);
            else Assert.IsType<OkObjectResult>(result);
            Assert.Equal(1, await revocation);
            await using var fresh = f.ScopedDb(f.VendorId);
            Assert.Equal(subjectRow, (await fresh.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct)).IsActive);
            var changed = await fresh.Cashboxes.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct);
            Assert.Equal(command == "open", changed.IsActive); Assert.Equal(command == "delete", changed.IsDeleted);
            Assert.Equal(subjectRow ? "Independently committed fixture name" : command == "create" ? "New fixture box" : command == "update" ? "Changed fixture box" : box.Name, changed.Name);
            var audit = Assert.Single(await fresh.AuditLogs.IgnoreQueryFilters().ToListAsync(ct));
            Assert.Equal(actor.Id, audit.UserId); Assert.Equal(f.VendorId, audit.TenantId); Assert.Equal(changed.Id.ToString(), audit.EntityId);
            treasury.VerifyNoOtherCalls(); reads.VerifyNoOtherCalls();
            Assert.Empty(await fresh.JournalEntries.IgnoreQueryFilters().ToListAsync(ct)); Assert.Empty(await fresh.OutboxMessages.ToListAsync(ct));
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await pending; } catch { /* Drain exact owned command; original failure remains. */ }
            if (revocation != null) { try { await revocation; } catch { /* Drain exact owned revoker. */ } }
        }
    }

    private sealed class BeforeMetadataSave : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<Cashbox>().Any(e => e.State is EntityState.Added or EntityState.Modified))
            {
                Assert.NotNull(data.Context.Database.CurrentTransaction);
                Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("update", "deleted", true)]
    [InlineData("delete", "deleted", true)]
    [InlineData("open", "deleted", true)]
    [InlineData("update", "foreign", true)]
    [InlineData("delete", "foreign", true)]
    [InlineData("open", "foreign", true)]
    [InlineData("delete", "active", false)]
    [InlineData("open", "active", false)]
    [InlineData("delete", "active", true)]
    [InlineData("open", "active", true)]
    [InlineData("update", "renamed", true)]
    [InlineData("delete", "renamed", true)]
    [InlineData("open", "renamed", true)]
    public async Task SubjectEligibilityAndAuditUseTheCommittedPostWaitRowEvenWhenPreviouslyTracked(string command, string turnover, bool tracked)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "turnover-actor", Email = "turnover@example.invalid",
            PasswordHash = "fixture-not-a-login", IsActive = true, Role = UserRole.Admin };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Cached fixture name", IsActive = false };
        var otherTenant = Guid.NewGuid();
        f.Db.Tenants.Add(new Tenant { Id = otherTenant, Name = "Fictional turnover tenant", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        f.Db.Users.Add(actor); f.Db.Cashboxes.Add(box); await f.Db.SaveChangesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        await using var db = f.ScopedDb(f.VendorId);
        if (tracked) Assert.Equal(box.Name, (await db.Cashboxes.SingleAsync(ct)).Name);
        await db.Database.OpenConnectionAsync(ct);
        var treasury = new Mock<ITreasuryOperationService>(MockBehavior.Strict);
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        var mapping = new Mock<ICashboxLedgerAccountService>();
        var controller = new CashboxController(db, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId),
            NullLogger<CashboxController>.Instance, treasury.Object, reads.Object, mapping.Object, Mock.Of<IAuditService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
        };
        await using var holder = new NpgsqlConnection(f.ConnectionString); await holder.OpenAsync(ct);
        await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync(ct);
        await using var transaction = await holder.BeginTransactionAsync(ct);
        var sql = turnover switch
        {
            "deleted" => "UPDATE \"Cashboxes\" SET \"IsDeleted\"=TRUE WHERE \"Id\"=@id",
            "foreign" => "UPDATE \"Cashboxes\" SET \"TenantId\"=@tenant WHERE \"Id\"=@id",
            "active" => "UPDATE \"Cashboxes\" SET \"IsActive\"=TRUE WHERE \"Id\"=@id",
            "renamed" => "UPDATE \"Cashboxes\" SET \"Name\"='Current committed fixture name' WHERE \"Id\"=@id",
            _ => throw new ArgumentOutOfRangeException(nameof(turnover))
        };
        await using (var mutate = new NpgsqlCommand(sql, holder, transaction))
        {
            mutate.Parameters.AddWithValue("id", box.Id);
            if (turnover == "foreign") mutate.Parameters.AddWithValue("tenant", otherTenant);
            Assert.Equal(1, await mutate.ExecuteNonQueryAsync(ct));
        }
        async Task<IActionResult?> Execute() => command switch
        {
            "update" => (await controller.UpdateCashbox(box.Id, new UpdateCashboxRequest { Name = "Command fixture name" }, ct)).Result,
            "delete" => await controller.DeleteCashbox(box.Id, ct),
            "open" => (await controller.OpenCashbox(box.Id, new OpenCashboxRequest { OpeningBalance = 0m }, ct)).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        var pending = Execute(); var committed = false;
        try
        {
            var waiter = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
            var blocked = false; var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !pending.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", observer);
                probe.Parameters.AddWithValue("holder", holder.ProcessID); probe.Parameters.AddWithValue("waiter", waiter);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            Assert.True(blocked, "The metadata command must wait on the exact independent subject owner.");
            Assert.False(pending.IsCompleted);
            await transaction.CommitAsync(ct); committed = true;
            var result = await pending;
            await using var fresh = f.ScopedDb(f.VendorId);
            var saved = await fresh.Cashboxes.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct);
            var audits = await fresh.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
            var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
            await File.WriteAllTextAsync(Path.Combine(output, "cashbox-subject-turnover-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(new { command, turnover, tracked, Holder = holder.ProcessID, Waiter = waiter, ExactBackendBlockerObserved = blocked,
                    Result = result?.GetType().Name, saved.Id, saved.TenantId, saved.Name, saved.IsActive, saved.IsDeleted, saved.OpenedAt, saved.OpenedByUserId,
                    Audits = audits.Select(a => new { a.UserId, a.TenantId, a.EntityId, a.BeforeDataJson, a.AfterDataJson }),
                    Scope = "Actual committed subject turnover before command ownership, including EF identity map; no treasury posting or middleware certification" }), ct);
            if (turnover is "deleted" or "foreign")
            {
                Assert.IsType<NotFoundObjectResult>(result); Assert.Empty(audits); mapping.VerifyNoOtherCalls();
                Assert.Equal(box.Name, saved.Name); Assert.False(saved.IsActive);
                Assert.Equal(turnover == "deleted", saved.IsDeleted);
                Assert.Equal(turnover == "foreign" ? otherTenant : f.VendorId, saved.TenantId);
            }
            else if (turnover == "active")
            {
                Assert.IsType<BadRequestObjectResult>(result); Assert.Empty(audits); mapping.VerifyNoOtherCalls();
                Assert.True(saved.IsActive); Assert.False(saved.IsDeleted); Assert.Equal(box.Name, saved.Name);
                Assert.Null(saved.OpenedAt); Assert.Null(saved.OpenedByUserId);
            }
            else
            {
                if (command == "delete") Assert.IsType<NoContentResult>(result); else Assert.IsType<OkObjectResult>(result);
                var audit = Assert.Single(audits); Assert.Equal(actor.Id, audit.UserId); Assert.Equal(f.VendorId, audit.TenantId);
                Assert.Equal(box.Id.ToString(), audit.EntityId);
                Assert.Equal(command == "update" ? "Command fixture name" : "Current committed fixture name", saved.Name);
                if (command is "update" or "delete") Assert.Contains("Current committed fixture name", audit.BeforeDataJson!);
            }
            treasury.VerifyNoOtherCalls(); reads.VerifyNoOtherCalls();
            Assert.Empty(await fresh.JournalEntries.IgnoreQueryFilters().ToListAsync(ct)); Assert.Empty(await fresh.LedgerEntries.IgnoreQueryFilters().ToListAsync(ct));
            Assert.Empty(await fresh.AccountingOutboxMessages.ToListAsync(ct)); Assert.Empty(await fresh.OutboxMessages.ToListAsync(ct));
        }
        finally
        {
            if (!committed) await transaction.RollbackAsync(CancellationToken.None);
            try { await pending; } catch { /* Drain exact owned command without replacing original failure. */ }
        }
    }

    [Theory]
    [InlineData("create", "active")]
    [InlineData("create", "inactive")]
    [InlineData("create", "deleted")]
    [InlineData("create", "foreign")]
    [InlineData("create", "missing")]
    [InlineData("update", "active")]
    [InlineData("update", "inactive")]
    [InlineData("update", "deleted")]
    [InlineData("update", "foreign")]
    [InlineData("update", "missing")]
    [InlineData("delete", "active")]
    [InlineData("delete", "inactive")]
    [InlineData("delete", "deleted")]
    [InlineData("delete", "foreign")]
    [InlineData("delete", "missing")]
    [InlineData("open", "active")]
    [InlineData("open", "inactive")]
    [InlineData("open", "deleted")]
    [InlineData("open", "foreign")]
    [InlineData("open", "missing")]
    public async Task EveryMetadataCommandRequiresThePersistedCurrentTenantActorBeforeMutation(string command, string state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actorId = Guid.NewGuid();
        var actorTenant = state == "foreign" ? Guid.NewGuid() : f.VendorId;
        if (state == "foreign") f.Db.Tenants.Add(new Tenant { Id = actorTenant, Name = "Fictional other store", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        if (state != "missing") f.Db.Users.Add(new User { Id = actorId, TenantId = actorTenant,
            Username = "fixture-cashbox-actor", Email = "cashbox-actor@example.invalid", PasswordHash = "fixture-not-a-login",
            IsActive = state != "inactive", IsDeleted = state == "deleted", Role = UserRole.Admin });
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Original fixture box", IsActive = false };
        if (command != "create") f.Db.Cashboxes.Add(box);
        await f.Db.SaveChangesAsync();
        var tenant = new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId);
        await using var db = f.ScopedDb(f.VendorId);
        var treasury = new Mock<ITreasuryOperationService>(MockBehavior.Strict);
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        var mapping = new Mock<ICashboxLedgerAccountService>();
        var auditCalls = 0;
        var audit = new Mock<IAuditService>(MockBehavior.Strict);
        audit.Setup(a => a.LogAction(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<object?>(), It.IsAny<object?>(), It.IsAny<string?>(), true, nameof(CashboxController)))
            .Callback(() => auditCalls++).Returns(Task.CompletedTask);
        var controller = new CashboxController(db, tenant, NullLogger<CashboxController>.Instance,
            treasury.Object, reads.Object, mapping.Object, audit.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
        };
        IActionResult? result = command switch
        {
            "create" => (await controller.CreateCashbox(new CreateCashboxRequest { Name = "New fixture box" })).Result,
            "update" => (await controller.UpdateCashbox(box.Id, new UpdateCashboxRequest { Name = "Changed fixture box" })).Result,
            "delete" => await controller.DeleteCashbox(box.Id),
            "open" => (await controller.OpenCashbox(box.Id, new OpenCashboxRequest { OpeningBalance = 0m })).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        await using var observer = f.ScopedDb(f.VendorId);
        var rows = await observer.Cashboxes.IgnoreQueryFilters().AsNoTracking().OrderBy(b => b.Id).ToListAsync();
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await File.WriteAllTextAsync(Path.Combine(output, "cashbox-command-actor-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new { command, state, ActorId = actorId, ActorTenant = actorTenant, CurrentTenant = f.VendorId,
                Result = result?.GetType().Name, auditCalls, Rows = rows.Select(b => new { b.Id, b.TenantId, b.Name, b.IsActive, b.IsDeleted, b.OpenedByUserId }),
                Scope = "Actual SQL metadata command, recorded mapping/audit collaborators; no canonical audit-delivery or treasury posting proof" }));
        if (state != "active")
        {
            Assert.IsType<ForbidResult>(result);
            Assert.Equal(0, auditCalls); mapping.VerifyNoOtherCalls();
            if (command == "create") Assert.Empty(rows);
            else
            {
                var unchanged = Assert.Single(rows); Assert.Equal(box.Id, unchanged.Id); Assert.Equal(box.Name, unchanged.Name);
                Assert.False(unchanged.IsActive); Assert.False(unchanged.IsDeleted); Assert.Null(unchanged.OpenedByUserId);
            }
        }
        else
        {
            if (command == "create") Assert.IsType<CreatedAtActionResult>(result);
            else if (command == "delete") Assert.IsType<NoContentResult>(result);
            else Assert.IsType<OkObjectResult>(result);
            var changed = Assert.Single(rows);
            Assert.Equal(command == "update" ? "Changed fixture box" : command == "create" ? "New fixture box" : box.Name, changed.Name);
            Assert.Equal(command == "open", changed.IsActive); Assert.Equal(command == "delete", changed.IsDeleted);
        }
        treasury.VerifyNoOtherCalls(); reads.VerifyNoOtherCalls();
        Assert.Empty(await observer.JournalEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await observer.LedgerEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await observer.AccountingOutboxMessages.ToListAsync()); Assert.Empty(await observer.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("valid", false)]
    [InlineData("valid", true)]
    [InlineData("foreign", false)]
    [InlineData("foreign", true)]
    [InlineData("deleted", false)]
    [InlineData("deleted", true)]
    public async Task CreateWarehouseAdmissionCannotUseATrackedForeignOrDeletedWarehouse(string warehouseState, bool tracked)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var otherTenant = Guid.NewGuid();
        f.Db.Tenants.Add(new Tenant { Id = otherTenant, Name = "Fictional warehouse tenant", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "warehouse-actor", Email = "warehouse@example.invalid",
            PasswordHash = "fixture-not-a-login", IsActive = true, Role = UserRole.Admin };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = warehouseState == "foreign" ? otherTenant : f.VendorId,
            Name = "Fictional warehouse", IsDeleted = warehouseState == "deleted" };
        f.Db.Users.Add(actor); f.Db.Warehouses.Add(warehouse); await f.Db.SaveChangesAsync();
        await using var db = f.ScopedDb(f.VendorId);
        if (tracked) Assert.Equal(warehouse.Id, (await db.Warehouses.IgnoreQueryFilters().SingleAsync()).Id);
        var treasury = new Mock<ITreasuryOperationService>(MockBehavior.Strict);
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        var mapping = new Mock<ICashboxLedgerAccountService>();
        var controller = new CashboxController(db, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId),
            NullLogger<CashboxController>.Instance, treasury.Object, reads.Object, mapping.Object, Mock.Of<IAuditService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
        };
        var result = (await controller.CreateCashbox(new CreateCashboxRequest { Name = "Warehouse fixture cashbox", WarehouseId = warehouse.Id })).Result;
        await using var fresh = f.ScopedDb(f.VendorId);
        var boxes = await fresh.Cashboxes.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var audits = await fresh.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await File.WriteAllTextAsync(Path.Combine(output, "cashbox-create-warehouse-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new { warehouseState, tracked, WarehouseTenant = warehouse.TenantId, CurrentTenant = f.VendorId,
                Result = result?.GetType().Name, Rows = boxes.Select(b => new { b.Id, b.TenantId, b.WarehouseId }), AuditCount = audits.Count,
                Scope = "Actual guarded SQL command warehouse isolation; preloaded identity is not HTTP/middleware exploit or all warehouse turnover proof" }));
        if (warehouseState == "valid")
        {
            Assert.IsType<CreatedAtActionResult>(result); var saved = Assert.Single(boxes);
            Assert.Equal(f.VendorId, saved.TenantId); Assert.Equal(warehouse.Id, saved.WarehouseId);
            var audit = Assert.Single(audits); Assert.Equal(actor.Id, audit.UserId); Assert.Equal(f.VendorId, audit.TenantId);
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(result); Assert.Empty(boxes); Assert.Empty(audits); mapping.VerifyNoOtherCalls();
        }
        treasury.VerifyNoOtherCalls(); reads.VerifyNoOtherCalls();
        Assert.Empty(await fresh.JournalEntries.IgnoreQueryFilters().ToListAsync()); Assert.Empty(await fresh.LedgerEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await fresh.AccountingOutboxMessages.ToListAsync()); Assert.Empty(await fresh.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("open")]
    public async Task SubjectAdmissionRefusesPendingTrackedChangesWithoutDiscardingThem(string command)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "pending-actor", Email = "pending@example.invalid",
            PasswordHash = "fixture-not-a-login", IsActive = true, Role = UserRole.Admin };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Persisted fixture name", IsActive = false };
        f.Db.Users.Add(actor); f.Db.Cashboxes.Add(box); await f.Db.SaveChangesAsync();
        await using var db = f.ScopedDb(f.VendorId); var cached = await db.Cashboxes.SingleAsync();
        cached.Name = "Caller pending fixture name";
        var treasury = new Mock<ITreasuryOperationService>(MockBehavior.Strict);
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        var mapping = new Mock<ICashboxLedgerAccountService>(MockBehavior.Strict);
        var controller = new CashboxController(db, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId),
            NullLogger<CashboxController>.Instance, treasury.Object, reads.Object, mapping.Object, Mock.Of<IAuditService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
        };
        IActionResult? result = null;
        var error = await Record.ExceptionAsync(async () =>
        {
            result = command switch
            {
                "update" => (await controller.UpdateCashbox(box.Id, new UpdateCashboxRequest { Name = "Command fixture name" })).Result,
                "delete" => await controller.DeleteCashbox(box.Id),
                "open" => (await controller.OpenCashbox(box.Id, new OpenCashboxRequest { OpeningBalance = 0m })).Result,
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
        });
        await using var fresh = f.ScopedDb(f.VendorId); var saved = await fresh.Cashboxes.AsNoTracking().SingleAsync();
        var audits = await fresh.AuditLogs.IgnoreQueryFilters().ToListAsync();
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await File.WriteAllTextAsync(Path.Combine(output, "cashbox-pending-subject-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new { command, Result = result?.GetType().Name, Error = error?.GetType().Name,
                PendingName = cached.Name, PendingState = db.Entry(cached).State.ToString(), PersistedName = saved.Name,
                saved.IsActive, saved.IsDeleted, AuditCount = audits.Count, TransactionDisposed = db.Database.CurrentTransaction == null }));
        Assert.True(error is InvalidOperationException || result is ObjectResult { StatusCode: 500 });
        Assert.Equal("Caller pending fixture name", cached.Name); Assert.Equal(EntityState.Modified, db.Entry(cached).State);
        Assert.Equal(box.Name, saved.Name); Assert.False(saved.IsActive); Assert.False(saved.IsDeleted); Assert.Empty(audits);
        Assert.Null(db.Database.CurrentTransaction); treasury.VerifyNoOtherCalls(); reads.VerifyNoOtherCalls(); mapping.VerifyNoOtherCalls();
        Assert.Empty(await fresh.JournalEntries.IgnoreQueryFilters().ToListAsync()); Assert.Empty(await fresh.LedgerEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await fresh.AccountingOutboxMessages.ToListAsync()); Assert.Empty(await fresh.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("subject", "update", EntityState.Added)]
    [InlineData("subject", "update", EntityState.Deleted)]
    [InlineData("subject", "delete", EntityState.Added)]
    [InlineData("subject", "delete", EntityState.Deleted)]
    [InlineData("subject", "open", EntityState.Added)]
    [InlineData("subject", "open", EntityState.Deleted)]
    [InlineData("subject", "close", EntityState.Added)]
    [InlineData("subject", "close", EntityState.Deleted)]
    [InlineData("subject", "close", EntityState.Modified)]
    [InlineData("warehouse", "create", EntityState.Added)]
    [InlineData("warehouse", "create", EntityState.Deleted)]
    [InlineData("warehouse", "update", EntityState.Added)]
    [InlineData("warehouse", "update", EntityState.Deleted)]
    public async Task CommandsPreserveCallerPendingAddedDeletedAndCloseModifiedState(string subject, string command, EntityState pendingState)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "pending-state-actor", Email = "pending-state@example.invalid",
            PasswordHash = "fixture-not-a-login", IsActive = true, Role = UserRole.Admin };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Persisted fixture warehouse" };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Persisted fixture cashbox", IsActive = command == "close", WarehouseId = warehouse.Id };
        f.Db.Users.Add(actor); f.Db.Warehouses.Add(warehouse); f.Db.Cashboxes.Add(box); await f.Db.SaveChangesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
        await using var db = f.ScopedDb(f.VendorId);
        var cachedBox = await db.Cashboxes.SingleAsync(ct);
        var cachedWarehouse = await db.Warehouses.SingleAsync(ct);
        var pendingEntry = subject == "subject" ? db.Entry((object)cachedBox) : db.Entry((object)cachedWarehouse);
        // These are deliberate caller-owned states for a persisted identity.
        // Do not flush them, detach them, or replace them to make admission pass.
        if (subject == "subject") cachedBox.Name = "Caller pending cashbox value";
        else cachedWarehouse.Name = "Caller pending warehouse value";
        pendingEntry.State = pendingState;
        var treasury = new Mock<ITreasuryOperationService>(MockBehavior.Strict);
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        var mapping = new Mock<ICashboxLedgerAccountService>(MockBehavior.Strict);
        if (command == "close")
            mapping.Setup(m => m.GetCashboxAccountIfExistsAsync(f.VendorId, box.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync((CashboxLedgerAccountInfo?)null);
        var controller = new CashboxController(db, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId),
            NullLogger<CashboxController>.Instance, treasury.Object, reads.Object, mapping.Object, Mock.Of<IAuditService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
        };
        IActionResult? result = null;
        var error = await Record.ExceptionAsync(async () =>
        {
            result = command switch
            {
                "create" => (await controller.CreateCashbox(new CreateCashboxRequest { Name = "Command fixture cashbox", WarehouseId = warehouse.Id }, ct)).Result,
                "update" => (await controller.UpdateCashbox(box.Id, new UpdateCashboxRequest { Name = "Command fixture cashbox" }, ct)).Result,
                "delete" => await controller.DeleteCashbox(box.Id, ct),
                "open" => (await controller.OpenCashbox(box.Id, new OpenCashboxRequest { OpeningBalance = 0m }, ct)).Result,
                "close" => await controller.CloseCashbox(box.Id, new CloseCashboxRequest { ActualCash = 0m }, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
        });
        await using var fresh = f.ScopedDb(f.VendorId);
        var saved = Assert.Single(await fresh.Cashboxes.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct));
        var savedWarehouse = Assert.Single(await fresh.Warehouses.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct));
        var audits = await fresh.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await File.WriteAllTextAsync(Path.Combine(output, "cashbox-caller-pending-state-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new { subject, command, RequestedState = pendingState.ToString(), RetainedState = pendingEntry.State.ToString(),
                PendingName = subject == "subject" ? cachedBox.Name : cachedWarehouse.Name,
                Result = result?.GetType().Name, StatusCode = (result as ObjectResult)?.StatusCode, Error = error?.GetType().Name,
                ReadOnlyMappingLookupCount = mapping.Invocations.Count(i => i.Method.Name == nameof(ICashboxLedgerAccountService.GetCashboxAccountIfExistsAsync)),
                PersistedName = saved.Name, saved.IsActive, saved.IsDeleted, PersistedWarehouseName = savedWarehouse.Name,
                AuditCount = audits.Count, TransactionDisposed = db.Database.CurrentTransaction == null,
                Scope = "Actual controller refusal/preservation of caller pending states; no financial posting, HTTP host, deployed role or every-writer proof" }), ct);
        Assert.True(error is InvalidOperationException || result is ObjectResult { StatusCode: 500 }, "Pending caller work must refuse admission, not succeed or be discarded.");
        Assert.Equal(pendingState, pendingEntry.State);
        Assert.Equal(subject == "subject" ? "Caller pending cashbox value" : "Caller pending warehouse value", subject == "subject" ? cachedBox.Name : cachedWarehouse.Name);
        Assert.Equal(box.Name, saved.Name); Assert.Equal(box.IsActive, saved.IsActive); Assert.False(saved.IsDeleted);
        Assert.Equal(warehouse.Name, savedWarehouse.Name); Assert.False(savedWarehouse.IsDeleted); Assert.Empty(audits);
        Assert.Null(db.Database.CurrentTransaction); treasury.VerifyNoOtherCalls(); reads.VerifyNoOtherCalls();
        if (command == "close")
            mapping.Verify(m => m.GetCashboxAccountIfExistsAsync(f.VendorId, box.Id, It.IsAny<CancellationToken>()), Times.AtMostOnce());
        mapping.VerifyNoOtherCalls();
        Assert.Empty(await fresh.JournalEntries.IgnoreQueryFilters().ToListAsync(ct)); Assert.Empty(await fresh.LedgerEntries.IgnoreQueryFilters().ToListAsync(ct));
        Assert.Empty(await fresh.AccountingOutboxMessages.ToListAsync(ct)); Assert.Empty(await fresh.OutboxMessages.ToListAsync(ct));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task AdmittedWarehouseMetadataRemainsOwnedThroughCashboxAndAuditCommit(string command)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "held-warehouse-actor", Email = "held-warehouse@example.invalid",
            PasswordHash = "fixture-not-a-login", IsActive = true, Role = UserRole.Admin };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Admitted fixture warehouse" };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Original fixture box", WarehouseId = warehouse.Id, IsActive = false };
        f.Db.Users.Add(actor); f.Db.Warehouses.Add(warehouse); if (command == "update") f.Db.Cashboxes.Add(box); await f.Db.SaveChangesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        var pause = new BeforeMetadataSave(); var tenant = new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString)
            .AddInterceptors(pause).Options, tenant);
        await db.Database.OpenConnectionAsync(ct);
        var treasury = new Mock<ITreasuryOperationService>(MockBehavior.Strict);
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        var mapping = new Mock<ICashboxLedgerAccountService>(); string? admittedMappingName = null;
        mapping.Setup(m => m.EnsureCashboxAccountAsync(It.IsAny<Cashbox>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<Cashbox, string?, CancellationToken>((_, name, _) => admittedMappingName = name)
            .ReturnsAsync(new CashboxLedgerAccountInfo(box.Id, Guid.Empty, "fixture-no-posting", "Fixture mapping recorder", false, warehouse.Id, warehouse.Name));
        var controller = new CashboxController(db, tenant, NullLogger<CashboxController>.Instance, treasury.Object, reads.Object, mapping.Object, Mock.Of<IAuditService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
        };
        await using var renamer = new NpgsqlConnection(f.ConnectionString); await renamer.OpenAsync(ct);
        await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync(ct);
        await using var rename = new NpgsqlCommand("UPDATE \"Warehouses\" SET \"Name\"='Later committed fixture warehouse' WHERE \"Id\"=@id AND \"TenantId\"=@tenant", renamer);
        rename.Parameters.AddWithValue("id", warehouse.Id); rename.Parameters.AddWithValue("tenant", f.VendorId);
        async Task<IActionResult?> Execute() => command == "create"
            ? (await controller.CreateCashbox(new CreateCashboxRequest { Name = "Created fixture box", WarehouseId = warehouse.Id }, ct)).Result
            : (await controller.UpdateCashbox(box.Id, new UpdateCashboxRequest { Name = "Updated fixture box" }, ct)).Result;
        var pending = Execute(); Task<int>? mutation = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            mutation = rename.ExecuteNonQueryAsync(ct); var blocked = false; var watch = System.Diagnostics.Stopwatch.StartNew();
            var holder = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !mutation.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", observer);
                probe.Parameters.AddWithValue("holder", holder); probe.Parameters.AddWithValue("waiter", renamer.ProcessID);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
            await File.WriteAllTextAsync(Path.Combine(output, "cashbox-held-warehouse-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(new { command, Holder = holder, Waiter = renamer.ProcessID, ExactBackendBlockerObserved = blocked,
                    CommandPending = !pending.IsCompleted, RenamePending = !mutation.IsCompleted,
                    Scope = "Actual warehouse row ownership through metadata+audit commit; mapping name recorder, no financial posting" }), ct);
            Assert.True(blocked, "The independent warehouse metadata update must wait for the admitted cashbox command.");
            Assert.False(pending.IsCompleted); Assert.False(mutation.IsCompleted);
            pause.Release.TrySetResult(); var result = await pending;
            if (command == "create") Assert.IsType<CreatedAtActionResult>(result); else Assert.IsType<OkObjectResult>(result);
            Assert.Equal(1, await mutation); Assert.Equal(warehouse.Name, admittedMappingName);
            await using var fresh = f.ScopedDb(f.VendorId);
            Assert.Equal("Later committed fixture warehouse", (await fresh.Warehouses.AsNoTracking().SingleAsync(ct)).Name);
            var saved = await fresh.Cashboxes.AsNoTracking().SingleAsync(ct); Assert.Equal(warehouse.Id, saved.WarehouseId);
            var audit = Assert.Single(await fresh.AuditLogs.IgnoreQueryFilters().ToListAsync(ct)); Assert.Equal(actor.Id, audit.UserId);
            Assert.Equal(f.VendorId, audit.TenantId); Assert.Equal(saved.Id.ToString(), audit.EntityId);
            treasury.VerifyNoOtherCalls(); reads.VerifyNoOtherCalls();
            Assert.Empty(await fresh.JournalEntries.IgnoreQueryFilters().ToListAsync(ct)); Assert.Empty(await fresh.LedgerEntries.IgnoreQueryFilters().ToListAsync(ct));
            Assert.Empty(await fresh.AccountingOutboxMessages.ToListAsync(ct)); Assert.Empty(await fresh.OutboxMessages.ToListAsync(ct));
        }
        finally
        {
            pause.Release.TrySetResult(); try { await pending; } catch { /* Drain exact owned command, retain original failure. */ }
            if (mutation != null) { try { await mutation; } catch { /* Drain exact owned independent rename. */ } }
        }
    }

    [Theory]
    [InlineData("create", "deleted")]
    [InlineData("update", "deleted")]
    [InlineData("create", "foreign")]
    [InlineData("update", "foreign")]
    [InlineData("create", "renamed")]
    [InlineData("update", "renamed")]
    [InlineData("create", "pending")]
    [InlineData("update", "pending")]
    public async Task WarehouseAdmissionUsesCommittedPostWaitEligibilityAndPreservesPendingCallerChanges(string command, string turnover)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var otherTenant = Guid.NewGuid();
        f.Db.Tenants.Add(new Tenant { Id = otherTenant, Name = "Fictional warehouse turnover tenant", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "warehouse-turnover-actor", Email = "warehouse-turnover@example.invalid",
            PasswordHash = "fixture-not-a-login", IsActive = true, Role = UserRole.Admin };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Cached fixture warehouse" };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Original fixture box", WarehouseId = warehouse.Id };
        f.Db.Users.Add(actor); f.Db.Warehouses.Add(warehouse); if (command == "update") f.Db.Cashboxes.Add(box); await f.Db.SaveChangesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        await using var db = f.ScopedDb(f.VendorId); var cached = await db.Warehouses.SingleAsync(ct);
        var cachedEntry = db.Entry(cached);
        if (command == "update") await db.Cashboxes.Include(b => b.Warehouse).SingleAsync(ct);
        if (turnover == "pending") cached.Name = "Caller pending warehouse name";
        await db.Database.OpenConnectionAsync(ct);
        var treasury = new Mock<ITreasuryOperationService>(MockBehavior.Strict);
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        var mapping = new Mock<ICashboxLedgerAccountService>(); string? mappedName = null;
        mapping.Setup(m => m.EnsureCashboxAccountAsync(It.IsAny<Cashbox>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<Cashbox, string?, CancellationToken>((_, name, _) => mappedName = name)
            .ReturnsAsync(new CashboxLedgerAccountInfo(box.Id, Guid.Empty, "fixture-no-posting", "Fixture mapping recorder", false, warehouse.Id, null));
        var controller = new CashboxController(db, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId),
            NullLogger<CashboxController>.Instance, treasury.Object, reads.Object, mapping.Object, Mock.Of<IAuditService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
        };
        await using var holder = new NpgsqlConnection(f.ConnectionString); await holder.OpenAsync(ct);
        await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync(ct);
        await using var transaction = await holder.BeginTransactionAsync(ct);
        var committed = false;
        if (turnover != "pending")
        {
            var sql = turnover switch
            {
                "deleted" => "UPDATE \"Warehouses\" SET \"IsDeleted\"=TRUE WHERE \"Id\"=@id AND \"TenantId\"=@current",
                "foreign" => "UPDATE \"Warehouses\" SET \"TenantId\"=@tenant WHERE \"Id\"=@id AND \"TenantId\"=@current",
                "renamed" => "UPDATE \"Warehouses\" SET \"Name\"='Current committed fixture warehouse' WHERE \"Id\"=@id AND \"TenantId\"=@current",
                _ => throw new ArgumentOutOfRangeException(nameof(turnover))
            };
            await using var mutation = new NpgsqlCommand(sql, holder, transaction);
            mutation.Parameters.AddWithValue("id", warehouse.Id); mutation.Parameters.AddWithValue("current", f.VendorId);
            if (turnover == "foreign") mutation.Parameters.AddWithValue("tenant", otherTenant);
            Assert.Equal(1, await mutation.ExecuteNonQueryAsync(ct));
        }
        IActionResult? result = null; Exception? error = null;
        async Task Execute()
        {
            error = await Record.ExceptionAsync(async () =>
            {
                result = command == "create"
                    ? (await controller.CreateCashbox(new CreateCashboxRequest { Name = "Command fixture box", WarehouseId = warehouse.Id }, ct)).Result
                    : (await controller.UpdateCashbox(box.Id, new UpdateCashboxRequest { Name = "Command fixture box" }, ct)).Result;
            });
        }
        var pending = Execute(); var blocked = false;
        try
        {
            var waiter = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
            if (turnover != "pending")
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(5) && !pending.IsCompleted)
                {
                    await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", observer);
                    probe.Parameters.AddWithValue("holder", holder.ProcessID); probe.Parameters.AddWithValue("waiter", waiter);
                    if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                    await Task.Delay(20, ct);
                }
                Assert.True(blocked, "Warehouse admission must wait for the exact independent PostgreSQL owner."); Assert.False(pending.IsCompleted);
            }
            await transaction.CommitAsync(ct); committed = true; await pending;
            await using var fresh = f.ScopedDb(f.VendorId);
            var savedWarehouse = await fresh.Warehouses.IgnoreQueryFilters().AsNoTracking().SingleAsync(ct);
            var boxes = await fresh.Cashboxes.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
            var audits = await fresh.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
            var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
            await File.WriteAllTextAsync(Path.Combine(output, "cashbox-warehouse-turnover-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(new { command, turnover, Holder = holder.ProcessID, Waiter = waiter, ExactBackendBlockerObserved = blocked,
                    Result = result?.GetType().Name, Error = error?.GetType().Name, MappedName = mappedName,
                    PendingName = cached.Name, PendingState = cachedEntry.State.ToString(), TransactionDisposed = db.Database.CurrentTransaction == null,
                    Warehouse = new { savedWarehouse.Id, savedWarehouse.TenantId, savedWarehouse.IsDeleted, savedWarehouse.Name },
                    Boxes = boxes.Select(b => new { b.Id, b.TenantId, b.Name, b.WarehouseId }), Audits = audits.Select(a => new { a.UserId, a.TenantId, a.EntityId, a.BeforeDataJson, a.AfterDataJson }),
                    Scope = "Actual warehouse post-wait eligibility and dirty identity-map refusal; mapping recorder, not canonical financial or HTTP proof" }), ct);
            if (turnover is "deleted" or "foreign" or "pending")
            {
                if (turnover == "pending")
                {
                    Assert.IsType<InvalidOperationException>(error); Assert.Equal("Caller pending warehouse name", cached.Name);
                    Assert.Equal(EntityState.Modified, cachedEntry.State); Assert.Equal(warehouse.Name, savedWarehouse.Name);
                }
                else { Assert.Null(error); Assert.IsType<BadRequestObjectResult>(result); }
                if (command == "create") Assert.Empty(boxes);
                else Assert.Equal(box.Name, Assert.Single(boxes).Name);
                Assert.Empty(audits); mapping.VerifyNoOtherCalls();
            }
            else
            {
                Assert.Null(error); if (command == "create") Assert.IsType<CreatedAtActionResult>(result); else Assert.IsType<OkObjectResult>(result);
                Assert.Equal("Current committed fixture warehouse", mappedName); Assert.Equal("Command fixture box", Assert.Single(boxes).Name);
                var audit = Assert.Single(audits); Assert.Equal(actor.Id, audit.UserId); Assert.Equal(f.VendorId, audit.TenantId);
                Assert.Equal(Assert.Single(boxes).Id.ToString(), audit.EntityId);
            }
            Assert.Null(db.Database.CurrentTransaction); treasury.VerifyNoOtherCalls(); reads.VerifyNoOtherCalls();
            Assert.Empty(await fresh.JournalEntries.IgnoreQueryFilters().ToListAsync(ct)); Assert.Empty(await fresh.LedgerEntries.IgnoreQueryFilters().ToListAsync(ct));
            Assert.Empty(await fresh.AccountingOutboxMessages.ToListAsync(ct)); Assert.Empty(await fresh.OutboxMessages.ToListAsync(ct));
        }
        finally
        {
            if (!committed) await transaction.RollbackAsync(CancellationToken.None);
            await pending;
        }
    }
}
