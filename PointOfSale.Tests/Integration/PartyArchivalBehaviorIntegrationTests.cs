using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Audit;
using PointOfSale.API.DTOs;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Integration;

/// <summary>Actual controller and PostgreSQL execution, not source-string certification.</summary>
public sealed class PartyArchivalBehaviorIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveReplayReactivate_PreservesHistoryBalanceAndActor(bool supplier)
    {
        await using var scope = TestDatabaseScope.Open();
        var tenant = new TestTenantContext(Guid.NewGuid());
        var actor = Guid.NewGuid();
        var party = Guid.NewGuid();
        await using var db = scope.CreateDbContext(tenant);
        db.Tenants.Add(new Tenant { Id = tenant.RequiredTenantId, Name = "QA-ERP-R9I-FIN-2026-PARTY", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        db.Users.Add(new User { Id = actor, TenantId = tenant.RequiredTenantId, Username = "qa-owner", Email = "qa@example.invalid", PasswordHash = "not-a-login", IsActive = true, Role = UserRole.Owner });
        var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = tenant.RequiredTenantId, Name = "QA warehouse", Code = "QA", IsActive = true };
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        await new AccountingPeriodService(db).CreateAsync(tenant.RequiredTenantId, actor, "QA-R10-FIXTURE", "Isolated archival history fixture",
            new DateOnly(DateTime.UtcNow.Year, 1, 1), new DateOnly(DateTime.UtcNow.Year, 12, 31), true, "Explicit period for synthetic financial history fixture");
        if (supplier)
        {
            db.Suppliers.Add(new Supplier { Id = party, TenantId = tenant.RequiredTenantId, Name = "QA supplier", Balance = 27.50m });
            db.PurchaseInvoices.Add(new PurchaseInvoice { Id = Guid.NewGuid(), TenantId = tenant.RequiredTenantId, SupplierId = party, WarehouseId = warehouse.Id, InvoiceNumber = "QA-ERP-R9I-FIN-2026-PUR", InvoiceDate = DateTime.UtcNow, TotalAmount = 27.50m });
        }
        else
        {
            db.Customers.Add(new Customer { Id = party, TenantId = tenant.RequiredTenantId, Name = "QA customer", Balance = 27.50m });
            db.SalesInvoices.Add(new SalesInvoice { Id = Guid.NewGuid(), TenantId = tenant.RequiredTenantId, CustomerId = party, WarehouseId = warehouse.Id, InvoiceNumber = "QA-ERP-R9I-FIN-2026-INV", InvoiceDate = DateTime.UtcNow, TotalAmount = 27.50m });
        }
        await db.SaveChangesAsync();
        var historyLedger = new LedgerService(db, tenant, NullLogger<LedgerService>.Instance, Mock.Of<IAuditService>());
        if (supplier)
            await historyLedger.ApplySupplierTransaction(party, DateTime.UtcNow, TransactionType.Purchase, "QA-ERP-R9I-FIN-2026-PUR", 0m, 27.50m, null, "Synthetic lifecycle history fixture", actor);
        else
            await historyLedger.ApplyCustomerTransaction(party, DateTime.UtcNow, TransactionType.Sale, "QA-ERP-R9I-FIN-2026-INV", 27.50m, 0m, null, "Synthetic lifecycle history fixture", actor);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var audit = new Mock<IAuditService>();
        var http = Context(actor);
        var customerController = Customers(db, tenant, audit.Object, http);
        var supplierController = Suppliers(db, tenant, audit.Object, http);
        var reason = new PartyLifecycleRequest("QA lifecycle acceptance, no financial settlement");
        async Task AssertArchivedStatementAsync()
        {
            var controller = new StatementOfAccountController(db, tenant, NullLogger<StatementOfAccountController>.Instance, historyLedger, Mock.Of<IAuditService>(), Mock.Of<IAccountingOutboxService>(), Mock.Of<IFinancialMutationIdempotencyService>());
            var request = new SOAFilterRequest { PartyType = supplier ? PartyType.Supplier : PartyType.Customer, PartyId = party };
            var result = Assert.IsType<OkObjectResult>((await controller.GetStatementOfAccount(request)).Result);
            var statement = Assert.IsType<SOAResponse>(result.Value);
            Assert.Equal(27.50m, Math.Abs(statement.Summary.ClosingBalance));
            Assert.Single(statement.Transactions);
        }
        if (supplier)
        {
            Assert.IsType<OkObjectResult>((await supplierController.ArchiveSupplier(party, reason, default)).Result);
            Assert.IsType<OkObjectResult>((await supplierController.ArchiveSupplier(party, reason, default)).Result);
            var row = await db.Suppliers.AsNoTracking().SingleAsync();
            Assert.False(row.IsActive); Assert.False(row.IsDeleted); Assert.Equal(actor, row.LastArchivedByUserId); Assert.NotNull(row.LastArchivedAtUtc); Assert.Equal(-27.50m, row.Balance);
            Assert.Single(await db.PurchaseInvoices.Include(x => x.Supplier).Where(x => x.Supplier!.Id == party).ToListAsync());
            Assert.IsType<OkObjectResult>((await supplierController.GetSupplier(party)).Result);
            await AssertArchivedStatementAsync();
            Assert.IsType<OkObjectResult>((await supplierController.ReactivateSupplier(party, reason, default)).Result);
            row = await db.Suppliers.AsNoTracking().SingleAsync(); Assert.True(row.IsActive); Assert.Equal(actor, row.LastReactivatedByUserId); Assert.NotNull(row.LastReactivatedAtUtc); Assert.Equal(-27.50m, row.Balance);
        }
        else
        {
            Assert.IsType<OkObjectResult>((await customerController.ArchiveCustomer(party, reason, default)).Result);
            Assert.IsType<OkObjectResult>((await customerController.ArchiveCustomer(party, reason, default)).Result);
            var row = await db.Customers.AsNoTracking().SingleAsync();
            Assert.False(row.IsActive); Assert.False(row.IsDeleted); Assert.Equal(actor, row.LastArchivedByUserId); Assert.NotNull(row.LastArchivedAtUtc); Assert.Equal(27.50m, row.Balance);
            Assert.Single(await db.SalesInvoices.Include(x => x.Customer).Where(x => x.Customer!.Id == party).ToListAsync());
            Assert.IsType<OkObjectResult>((await customerController.GetCustomer(party)).Result);
            await AssertArchivedStatementAsync();
            Assert.IsType<OkObjectResult>((await customerController.ReactivateCustomer(party, reason, default)).Result);
            row = await db.Customers.AsNoTracking().SingleAsync(); Assert.True(row.IsActive); Assert.Equal(actor, row.LastReactivatedByUserId); Assert.NotNull(row.LastReactivatedAtUtc); Assert.Equal(27.50m, row.Balance);
        }
        var logs = await db.AuditLogs.Where(x => x.EntityId == party.ToString()).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Single(logs, x => x.ActionType == "Archive");
        Assert.Single(logs, x => x.ActionType == "Reactivate");
        Assert.All(logs, x => { Assert.Equal(actor, x.UserId); Assert.Equal(tenant.RequiredTenantId, x.TenantId); Assert.True(x.SuccessFlag); });
        var statement = new StatementOfAccountController(db, tenant, NullLogger<StatementOfAccountController>.Instance, historyLedger, Mock.Of<IAuditService>(), Mock.Of<IAccountingOutboxService>(), Mock.Of<IFinancialMutationIdempotencyService>());
        var query = new SOAFilterRequest { PartyType = supplier ? PartyType.Supplier : PartyType.Customer, PartyId = party };
        var statementResult = Assert.IsType<OkObjectResult>((await statement.GetStatementOfAccount(query)).Result);
        var response = Assert.IsType<SOAResponse>(statementResult.Value);
        Assert.Equal(27.50m, Math.Abs(response.Summary.ClosingBalance));
        Assert.Single(response.Transactions);
        Assert.Empty(await db.JournalEntries.ToListAsync());
        var evidence = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(evidence))
            await File.WriteAllTextAsync(Path.Combine(evidence, supplier ? "supplier-archival-reconciliation.json" : "customer-archival-reconciliation.json"),
                JsonSerializer.Serialize(new { isolatedQa = true, tenantId = tenant.RequiredTenantId, partyId = party, actorId = actor,
                    archiveReplayAuditRows = 1, reactivationAuditRows = 1, auditIds = logs.Select(x => x.LogId),
                    finalIsActive = true, finalIsDeleted = false, historicalBalanceRetained = supplier ? -27.50m : 27.50m,
                    closingStatementAbsoluteBalance = response.Summary.ClosingBalance, retainedStatementTransactions = response.Transactions.Count,
                    fixtureInvoiceSeededNotPostedWorkflow = true, lifecycleJournalEffect = 0, financialSettlementClaimed = false }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Theory]
    [InlineData(UserRole.Cashier, true)]
    [InlineData(UserRole.Owner, false)]
    public async Task UnprivilegedOrInactiveActor_CannotMutateEitherLifecycle(UserRole role, bool active)
    {
        await using var scope = TestDatabaseScope.Open();
        var tenant = new TestTenantContext(Guid.NewGuid());
        var actor = Guid.NewGuid();
        await using var db = scope.CreateDbContext(tenant);
        db.Tenants.Add(new Tenant { Id = tenant.RequiredTenantId, Name = "QA party denial", Currency = "EGP", IsActive = true });
        db.Users.Add(new User { Id = actor, TenantId = tenant.RequiredTenantId, Username = "denied", Email = "denied@example.invalid", PasswordHash = "not-a-login", Role = role, IsActive = active });
        var id = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = id, TenantId = tenant.RequiredTenantId, Name = "QA customer" });
        db.Suppliers.Add(new Supplier { Id = id, TenantId = tenant.RequiredTenantId, Name = "QA supplier" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var audit = new Mock<IAuditService>(MockBehavior.Strict);
        Assert.IsType<ForbidResult>((await Customers(db, tenant, audit.Object, Context(actor)).ArchiveCustomer(id, null, default)).Result);
        Assert.IsType<ForbidResult>((await Suppliers(db, tenant, audit.Object, Context(actor)).ArchiveSupplier(id, null, default)).Result);
        Assert.True((await db.Customers.AsNoTracking().SingleAsync()).IsActive);
        Assert.True((await db.Suppliers.AsNoTracking().SingleAsync()).IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditPersistenceFailure_RollsBackLifecycleMutation(bool supplier)
    {
        await using var scope = TestDatabaseScope.Open();
        var tenant = new TestTenantContext(Guid.NewGuid());
        var actor = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using var seed = scope.CreateDbContext(tenant);
        seed.Tenants.Add(new Tenant { Id = tenant.RequiredTenantId, Name = "QA rollback", Currency = "EGP", IsActive = true });
        seed.Users.Add(new User { Id = actor, TenantId = tenant.RequiredTenantId, Username = "owner", Email = "owner@example.invalid", PasswordHash = "not-a-login", Role = UserRole.Owner, IsActive = true });
        seed.Customers.Add(new Customer { Id = id, TenantId = tenant.RequiredTenantId, Name = "QA customer" });
        seed.Suppliers.Add(new Supplier { Id = id, TenantId = tenant.RequiredTenantId, Name = "QA supplier" });
        await seed.SaveChangesAsync();
        var options = new DbContextOptionsBuilder<PointOfSale.Infrastructure.Data.ApplicationDbContext>()
            .UseNpgsql(seed.Database.GetConnectionString()).AddInterceptors(new FailAuditSave()).Options;
        await using (var db = new PointOfSale.Infrastructure.Data.ApplicationDbContext(options, tenant))
        {
            var audit = new Mock<IAuditService>(MockBehavior.Strict);
            if (supplier)
                await Assert.ThrowsAsync<InvalidOperationException>(() => Suppliers(db, tenant, audit.Object, Context(actor)).ArchiveSupplier(id, null, default));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => Customers(db, tenant, audit.Object, Context(actor)).ArchiveCustomer(id, null, default));
        }
        await using var verify = scope.CreateDbContext(tenant);
        Assert.True((await verify.Customers.SingleAsync()).IsActive);
        Assert.True((await verify.Suppliers.SingleAsync()).IsActive);
        Assert.Empty(await verify.AuditLogs.ToListAsync());
    }

    private sealed class FailAuditSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditLog>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("QA injected audit storage failure");
            return ValueTask.FromResult(result);
        }
    }

    private static DefaultHttpContext Context(Guid actor) => new() { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "Owner")], "QA")) };
    private static CustomersController Customers(PointOfSale.Infrastructure.Data.ApplicationDbContext db, TestTenantContext tenant, IAuditService audit, HttpContext http) =>
        new(db, tenant, NullLogger<CustomersController>.Instance, Mock.Of<IValidationService>(), audit, Mock.Of<ILedgerService>(), Mock.Of<IFinancialMutationIdempotencyService>(), Mock.Of<IAccountingOutboxService>(), Mock.Of<IAccountResolver>(), Mock.Of<ICashboxLedgerAccountService>()) { ControllerContext = new ControllerContext { HttpContext = http } };
    private static SuppliersController Suppliers(PointOfSale.Infrastructure.Data.ApplicationDbContext db, TestTenantContext tenant, IAuditService audit, HttpContext http) =>
        new(db, tenant, NullLogger<SuppliersController>.Instance, Mock.Of<IValidationService>(), audit, Mock.Of<ILedgerService>(), Mock.Of<IAccountingOutboxService>(), Mock.Of<IAccountResolver>(), Mock.Of<ITreasuryReadService>(), Mock.Of<ICashboxLedgerAccountService>(), Mock.Of<IFinancialMutationIdempotencyService>()) { ControllerContext = new ControllerContext { HttpContext = http } };
}
