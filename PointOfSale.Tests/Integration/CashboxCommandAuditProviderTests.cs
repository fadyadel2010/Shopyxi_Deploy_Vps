using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Treasury;

namespace PointOfSale.Tests.Integration;

// Actual SQL metadata + actual AuditService, never an audit host/worker, real
// money, external sink or primary database. Queue fallback is recorded only.
public sealed partial class CashboxCommandAuditProviderTests
{
    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("open", false)]
    [InlineData("open", true)]
    public async Task MetadataAndItsDurableSqlAuditCommitTogetherWithoutVolatileQueueSubstitution(string command, bool failAudit)
        => await ExecuteAsync(command, failAudit, actualMapping: false);

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    public async Task CanonicalDedicatedAccountMappingSharesMetadataAndAuditRollbackWithoutAnyFinancialPosting(string command, bool failAudit)
        => await ExecuteAsync(command, failAudit, actualMapping: true);

    private static async Task ExecuteAsync(string command, bool failAudit, bool actualMapping)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        var actor = new User { Id = Guid.NewGuid(), TenantId = f.VendorId, Username = "audit-actor", Email = "audit-actor@example.invalid",
            PasswordHash = "fixture-not-a-login", IsActive = true, Role = UserRole.Admin };
        var box = new Cashbox { Id = Guid.NewGuid(), TenantId = f.VendorId, Name = "Original fixture box", IsActive = false };
        f.Db.Users.Add(actor); if (command != "create") f.Db.Cashboxes.Add(box); await f.Db.SaveChangesAsync();
        var tenant = new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId);
        if (actualMapping)
        {
            await using var seed = f.ScopedDb(f.VendorId);
            await new TenantAccountSeeder(seed, NullLogger<TenantAccountSeeder>.Instance).SeedAsync(f.VendorId);
            if (command == "update")
                await new CashboxLedgerAccountService(seed, NullLogger<CashboxLedgerAccountService>.Instance).EnsureCashboxAccountAsync(box);
        }
        await using var baseline = f.ScopedDb(f.VendorId);
        var originalAccounts = await baseline.LedgerAccounts.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.AccountCode, a.Name, a.ParentAccountId, a.CurrentBalance }).ToListAsync();
        var originalTreasury = await baseline.TreasuryAccounts.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.AccountId, a.Name, a.ReferenceCode, a.IsDefault, a.Location, a.DisplayOrder }).ToListAsync();
        if (actualMapping) { Assert.NotEmpty(originalAccounts); Assert.NotEmpty(originalTreasury); }
        var fault = new AuditSqlFailure(failAudit);
        ApplicationDbContext Context() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.ConnectionString)
            .AddInterceptors(fault).Options, tenant);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Admin")], "fixture")) };
        var fallback = new List<AuditLogItem>();
        var queue = new Mock<IAuditQueue>(MockBehavior.Strict);
        queue.Setup(q => q.Enqueue(It.IsAny<AuditLogItem>())).Callback<AuditLogItem>(fallback.Add);
        queue.SetupGet(q => q.QueueDepth).Returns(() => fallback.Count);
        await using var provider = new ServiceCollection().AddScoped<ApplicationDbContext>(_ => Context()).BuildServiceProvider();
        var audit = new AuditService(queue.Object, new HttpContextAccessor { HttpContext = http }, tenant,
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditService>.Instance);
        await using var db = Context();
        var treasury = new Mock<ITreasuryOperationService>(MockBehavior.Strict);
        var reads = new Mock<ITreasuryReadService>(MockBehavior.Strict);
        var controller = new CashboxController(db, tenant, NullLogger<CashboxController>.Instance,
            treasury.Object, actualMapping ? new TreasuryReadService(db, NullLogger<TreasuryReadService>.Instance) : reads.Object,
            actualMapping ? new CashboxLedgerAccountService(db, NullLogger<CashboxLedgerAccountService>.Instance) : Mock.Of<ICashboxLedgerAccountService>(), audit)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        IActionResult? result = null;
        var error = await Record.ExceptionAsync(async () =>
        {
            result = command switch
            {
                "create" => (await controller.CreateCashbox(new CreateCashboxRequest { Name = "New fixture box" })).Result,
                "update" => (await controller.UpdateCashbox(box.Id, new UpdateCashboxRequest { Name = "Changed fixture box" })).Result,
                "delete" => await controller.DeleteCashbox(box.Id),
                "open" => (await controller.OpenCashbox(box.Id, new OpenCashboxRequest { OpeningBalance = 0m })).Result,
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
        });
        await using var fresh = f.ScopedDb(f.VendorId);
        var rows = await fresh.Cashboxes.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var audits = await fresh.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var accounts = await fresh.LedgerAccounts.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.AccountCode, a.Name, a.ParentAccountId, a.CurrentBalance }).ToListAsync();
        var mappings = await fresh.TreasuryAccounts.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.AccountId, a.Name, a.ReferenceCode, a.IsDefault, a.Location, a.DisplayOrder }).ToListAsync();
        var output = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required");
        await File.WriteAllTextAsync(Path.Combine(output, "cashbox-command-audit-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new { command, failAudit, actualMapping, Result = result?.GetType().Name, Error = error?.GetType().Name,
                fault.Attempts, fault.SqlState, FallbackCount = fallback.Count,
                Rows = rows.Select(b => new { b.Id, b.TenantId, b.Name, b.IsActive, b.IsDeleted }),
                Audits = audits.Select(a => new { a.LogId, a.TenantId, a.UserId, a.ActionType, a.EntityId, a.SuccessFlag }),
                OriginalAccounts = originalAccounts, Accounts = accounts, OriginalTreasury = originalTreasury, Mappings = mappings,
                Scope = "Actual AuditService and PostgreSQL abort at audit save; local fallback recorder only, no worker/external sink" }));
        Assert.Equal(1, fault.Attempts);
        if (failAudit)
        {
            Assert.Equal("P0001", fault.SqlState);
            if (command == "create") Assert.Empty(rows);
            else
            {
                var unchanged = Assert.Single(rows); Assert.Equal(box.Name, unchanged.Name);
                Assert.False(unchanged.IsActive); Assert.False(unchanged.IsDeleted);
            }
            Assert.Empty(audits); Assert.Empty(fallback);
            Assert.Equal(originalAccounts, accounts); Assert.Equal(originalTreasury, mappings);
            Assert.True(error != null || result is ObjectResult { StatusCode: 500 }, "Audit SQL failure cannot return success after committing metadata.");
        }
        else
        {
            Assert.Null(error);
            if (command == "create") Assert.IsType<CreatedAtActionResult>(result);
            else if (command == "delete") Assert.IsType<NoContentResult>(result);
            else Assert.IsType<OkObjectResult>(result);
            var saved = Assert.Single(rows); var logged = Assert.Single(audits);
            Assert.Equal(f.VendorId, logged.TenantId); Assert.Equal(actor.Id, logged.UserId);
            Assert.Equal(saved.Id.ToString(), logged.EntityId); Assert.True(logged.SuccessFlag);
            Assert.Equal("Cashbox" + char.ToUpperInvariant(command[0]) + command[1..], logged.ActionType);
            Assert.Equal(command == "open", saved.IsActive); Assert.Equal(command == "delete", saved.IsDeleted);
            Assert.Empty(fallback);
            if (actualMapping)
            {
                var dedicated = Assert.Single(mappings, m => m.ReferenceCode == saved.Id.ToString("N"));
                var account = Assert.Single(accounts, a => a.Id == dedicated.AccountId);
                Assert.Contains(saved.Name, account.Name); Assert.Contains(saved.Name, dedicated.Name);
                Assert.Equal(0m, account.CurrentBalance);
                if (command == "create")
                {
                    Assert.Equal(originalAccounts.Count + 1, accounts.Count); Assert.Equal(originalTreasury.Count + 1, mappings.Count);
                    Assert.True(dedicated.IsDefault);
                }
                else
                {
                    Assert.Equal(originalAccounts.Count, accounts.Count); Assert.Equal(originalTreasury.Count, mappings.Count);
                    Assert.Equal(Assert.Single(originalTreasury, m => m.ReferenceCode == box.Id.ToString("N")).Id, dedicated.Id);
                }
                Assert.All(accounts, a => Assert.Equal(0m, a.CurrentBalance));
            }
        }
        treasury.VerifyNoOtherCalls(); reads.VerifyNoOtherCalls();
        Assert.Empty(await fresh.JournalEntries.IgnoreQueryFilters().ToListAsync()); Assert.Empty(await fresh.OutboxMessages.ToListAsync());
        Assert.Empty(await fresh.LedgerEntries.IgnoreQueryFilters().ToListAsync()); Assert.Empty(await fresh.AccountingOutboxMessages.ToListAsync());
    }

    private sealed class AuditSqlFailure(bool enabled) : SaveChangesInterceptor
    {
        internal int Attempts { get; private set; }
        internal string? SqlState { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added))
            {
                Attempts++;
                if (enabled)
                {
                    try
                    {
                        await data.Context.Database.ExecuteSqlRawAsync(
                            "DO $$ BEGIN RAISE EXCEPTION USING ERRCODE='P0001', MESSAGE='APP2 fictional cashbox audit persistence failure'; END $$;", cancellationToken);
                    }
                    catch (PostgresException error) { SqlState = error.SqlState; throw; }
                    throw new InvalidOperationException("The actual PostgreSQL audit abort must not succeed.");
                }
            }
            return result;
        }
    }
}
