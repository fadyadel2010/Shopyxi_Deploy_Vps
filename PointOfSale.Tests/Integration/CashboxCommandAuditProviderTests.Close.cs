using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

public sealed partial class CashboxCommandAuditProviderTests
{
    private static CashboxController CloseController(ApplicationDbContext db, Guid tenant, Guid actor) => new(
        db, new PointOfSale.Tests.Infrastructure.TestTenantContext(tenant), NullLogger<CashboxController>.Instance,
        Mock.Of<ITreasuryOperationService>(MockBehavior.Strict), Mock.Of<ITreasuryReadService>(),
        Mock.Of<ICashboxLedgerAccountService>(), Mock.Of<IAuditService>(MockBehavior.Strict))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
    };

    private static async Task<(User Actor, Cashbox Box)> SeedCloseAsync(
        ProductionCoordinationAuthorityTests.Fixture f, bool active = true)
    {
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "close-authority",
            Email = "close-authority@example.invalid", PasswordHash = "fixture-not-a-login", Role = UserRole.Admin, IsActive = true };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Cached close preimage", IsActive = active };
        f.Db.AddRange(actor, box); await f.Db.SaveChangesAsync(); return (actor, box);
    }

    [Fact]
    public async Task CountedVarianceRequiresDurableExplanationBeforeClosing()
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, box) = await SeedCloseAsync(f);
        await using var db = f.ScopedDb(f.VendorId);
        var mapping = new Mock<ICashboxLedgerAccountService>(MockBehavior.Strict);
        mapping.Setup(x => x.GetCashboxAccountIfExistsAsync(f.VendorId, box.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashboxLedgerAccountInfo(box.Id, Guid.NewGuid(), "QA-CASHBOX", box.Name, true, null, null));
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        reads.Setup(x => x.GetAccountBalanceAsync(f.VendorId, "QA-CASHBOX", It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);
        var controller = new CashboxController(db, new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId),
            NullLogger<CashboxController>.Instance, Mock.Of<ITreasuryOperationService>(MockBehavior.Strict),
            reads.Object, mapping.Object, Mock.Of<IAuditService>(MockBehavior.Strict))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) } }
        };

        var denied = await controller.CloseCashbox(box.Id, new CloseCashboxRequest { ActualCash = 99m, Notes = "   " });
        var deniedBody = Assert.IsType<BadRequestObjectResult>(denied);
        Assert.Contains("CASHBOX_VARIANCE_NOTES_REQUIRED", JsonSerializer.Serialize(deniedBody.Value));
        Assert.True((await f.Db.Cashboxes.AsNoTracking().SingleAsync()).IsActive);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync());
        Assert.Null(db.Database.CurrentTransaction);

        var accepted = await controller.CloseCashbox(box.Id, new CloseCashboxRequest
        {
            ActualCash = 99m,
            Notes = " Counted twice by shift manager. "
        });
        var result = Assert.IsType<OkObjectResult>(accepted);
        Assert.Contains("-1", JsonSerializer.Serialize(result.Value));
        var saved = await f.Db.Cashboxes.AsNoTracking().SingleAsync();
        Assert.False(saved.IsActive);
        var audit = Assert.Single(await f.Db.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        Assert.Equal("CashboxClose", audit.ActionType);
        Assert.Equal(actor.Id, audit.UserId);
        using var after = JsonDocument.Parse(audit.AfterDataJson!);
        var details = after.RootElement.GetProperty("details");
        Assert.Equal(100m, details.GetProperty("expectedBalance").GetDecimal());
        Assert.Equal(99m, details.GetProperty("actualCash").GetDecimal());
        Assert.Equal(-1m, details.GetProperty("difference").GetDecimal());
        Assert.Equal("Counted twice by shift manager.", details.GetProperty("notes").GetString());
        await CloseReceiptAsync(new
        {
            workflow = "physical-count-variance",
            expectedBalance = 100m,
            actualCash = 99m,
            difference = -1m,
            unexplainedVarianceDenied = true,
            explainedVarianceClosed = true,
            actor = actor.Id,
            audit = audit.LogId
        });
        mapping.VerifyAll(); reads.VerifyAll();
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("closed")]
    public async Task CloseUsesPersistedSubjectInsteadOfAnUnchangedCachedPreimage(string turnover)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, box) = await SeedCloseAsync(f, active: turnover == "closed");
        await using var db = f.ScopedDb(f.VendorId);
        var cached = await db.Cashboxes.SingleAsync();
        await using (var other = new NpgsqlConnection(f.ConnectionString))
        {
            await other.OpenAsync();
            await using var update = new NpgsqlCommand(
                "UPDATE \"Cashboxes\" SET \"Name\"='Persisted close authority',\"IsActive\"=@active WHERE \"Id\"=@id", other);
            update.Parameters.AddWithValue("active", turnover != "closed"); update.Parameters.AddWithValue("id", box.Id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        Assert.Equal(EntityState.Unchanged, db.Entry(cached).State);
        var result = await CloseController(db, f.VendorId, actor.Id).CloseCashbox(box.Id, new CloseCashboxRequest { ActualCash = 0m });
        var audits = await f.Db.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        if (turnover == "closed") { Assert.IsType<BadRequestObjectResult>(result); Assert.Empty(audits); }
        else
        {
            Assert.IsType<OkObjectResult>(result);
            using var before = JsonDocument.Parse(Assert.Single(audits).BeforeDataJson!);
            Assert.Equal("Persisted close authority", before.RootElement.GetProperty("Name").GetString());
            Assert.True(before.RootElement.GetProperty("IsActive").GetBoolean());
        }
        var saved = await f.Db.Cashboxes.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.False(saved.IsActive); Assert.Equal("Persisted close authority", saved.Name);
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(await f.Db.LedgerEntries.IgnoreQueryFilters().ToListAsync());
        await CloseReceiptAsync(new { turnover, Result = result.GetType().Name, AuditCount = audits.Count, saved.Name, saved.IsActive });
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task CloseCannotFlushOrDiscardAnUnrelatedPendingCallerIdentity(EntityState state)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, box) = await SeedCloseAsync(f);
        await using var db = f.ScopedDb(f.VendorId);
        var pending = await db.Users.SingleAsync(); pending.Username = "Caller-owned pending close identity";
        db.Entry(pending).State = state;
        var result = await CloseController(db, f.VendorId, actor.Id).CloseCashbox(box.Id, new CloseCashboxRequest { ActualCash = 0m });
        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(state, db.Entry(pending).State); Assert.Equal("Caller-owned pending close identity", pending.Username);
        var saved = await f.Db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(actor.Username, saved.Username); Assert.False(saved.IsDeleted);
        await using var persisted = f.ScopedDb(f.VendorId);
        Assert.True((await persisted.Cashboxes.AsNoTracking().SingleAsync(c => c.Id == box.Id)).IsActive);
        Assert.Empty(await f.Db.AuditLogs.IgnoreQueryFilters().ToListAsync()); Assert.Null(db.Database.CurrentTransaction);
        await CloseReceiptAsync(new { RequestedState = state.ToString(), RetainedState = db.Entry(pending).State.ToString(), saved.Username });
    }

    [Theory]
    [InlineData("name")]
    [InlineData("closed")]
    [InlineData("deleted")]
    public async Task CloseRevalidatesTheExactPostWaitSubjectBeforeItsAuditSnapshot(string turnover)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var (actor, box) = await SeedCloseAsync(f);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        await using var db = f.ScopedDb(f.VendorId); await db.Cashboxes.SingleAsync(ct); await db.Database.OpenConnectionAsync(ct);
        await using var holder = new NpgsqlConnection(f.ConnectionString); await holder.OpenAsync(ct);
        await using var observer = new NpgsqlConnection(f.ConnectionString); await observer.OpenAsync(ct);
        await using var tx = await holder.BeginTransactionAsync(ct);
        var sql = turnover switch {
            "closed" => "UPDATE \"Cashboxes\" SET \"IsActive\"=FALSE WHERE \"Id\"=@id",
            "deleted" => "UPDATE \"Cashboxes\" SET \"IsDeleted\"=TRUE WHERE \"Id\"=@id",
            _ => "UPDATE \"Cashboxes\" SET \"Name\"='Committed while close waited' WHERE \"Id\"=@id" };
        await using (var update = new NpgsqlCommand(sql, holder, tx))
        { update.Parameters.AddWithValue("id", box.Id); Assert.Equal(1, await update.ExecuteNonQueryAsync(ct)); }
        var pending = CloseController(db, f.VendorId, actor.Id).CloseCashbox(box.Id, new CloseCashboxRequest { ActualCash = 0m }, ct);
        var committed = false; var blocked = false;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !pending.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@waiter))", observer);
                probe.Parameters.AddWithValue("holder", holder.ProcessID);
                probe.Parameters.AddWithValue("waiter", ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            Assert.True(blocked, "Close must observably contend on the exact independently owned subject.");
            await tx.CommitAsync(ct); committed = true;
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var audits = await f.Db.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
            if (turnover == "name")
            {
                Assert.IsType<OkObjectResult>(result);
                using var before = JsonDocument.Parse(Assert.Single(audits).BeforeDataJson!);
                Assert.Equal("Committed while close waited", before.RootElement.GetProperty("Name").GetString());
            }
            else { Assert.IsType<ConflictObjectResult>(result); Assert.Empty(audits); }
            Assert.Null(db.Database.CurrentTransaction); Assert.Empty(await f.Db.LedgerEntries.IgnoreQueryFilters().ToListAsync(ct));
            await CloseReceiptAsync(new { turnover, ExactBackendBlockerObserved = blocked, Result = result.GetType().Name, AuditCount = audits.Count });
        }
        finally
        {
            if (!committed) await tx.RollbackAsync(CancellationToken.None);
            try { await pending; } catch { /* Drain only this owned operation without replacing the original assertion. */ }
        }
    }

    private static Task CloseReceiptAsync(object value)
    {
        var root = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Owned fixture evidence root required.");
        return File.WriteAllTextAsync(Path.Combine(root, "cashbox-close-authority-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(value));
    }
}
