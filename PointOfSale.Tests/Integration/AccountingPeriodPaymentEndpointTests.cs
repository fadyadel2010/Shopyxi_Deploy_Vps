using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Audit;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Tests.Infrastructure;
using PaymentMethod = PointOfSale.Core.Enums.PaymentMethod;

namespace PointOfSale.Tests.Integration;

public sealed class AccountingPeriodPaymentEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedPeriodPayment_PropagatesDomainConflict_NotGeneric500_AndRollsBackPaymentPartyAndIdempotency(bool supplier)
    {
        using var scope = TestDatabaseScope.Open();
        var tenantId = Guid.NewGuid(); var actor = Guid.NewGuid(); var party = Guid.NewGuid();
        var tenant = new TestTenantContext(tenantId);
        await using var db = scope.CreateDbContext(tenant);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "QA-ERP-R10-20261113-PAYMENT-PERIOD", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        db.Users.Add(new User { Id = actor, TenantId = tenantId, Username = $"qa-{actor:N}", Email = $"{actor:N}@example.invalid", PasswordHash = "not-a-login", Role = UserRole.Owner, IsActive = true });
        if (supplier) db.Suppliers.Add(new Supplier { Id = party, TenantId = tenantId, Name = "QA supplier", IsActive = true });
        else db.Customers.Add(new Customer { Id = party, TenantId = tenantId, Name = "QA customer", IsActive = true });
        var bank = Account.Create(tenantId, "1030", "QA bank", AccountType.Asset); db.LedgerAccounts.Add(bank);
        await db.SaveChangesAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var periods = new AccountingPeriodService(db);
        var period = await periods.CreateAsync(tenantId, actor, "CLOSED-PAYMENT", "Closed payment period", today.AddDays(-1), today.AddDays(1), true, "QA period denial");
        await periods.TransitionAsync(tenantId, period.Id, actor, "close", period.Version, "QA close before payment");
        var audit = Mock.Of<IAuditService>();
        var ledger = new LedgerService(db, tenant, NullLogger<LedgerService>.Instance, audit);
        var outbox = new AccountingOutboxService(db, Mock.Of<ICorrelationIdAccessor>(), NullLogger<AccountingOutboxService>.Instance);
        var accounts = new AccountResolver(db, NullLogger<AccountResolver>.Instance);
        var idempotency = new FinancialMutationIdempotencyService(db, tenant, NullLogger<FinancialMutationIdempotencyService>.Instance);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "QA")) };
        http.Request.Headers["Idempotency-Key"] = "QA-ERP-R10-20261113-CLOSED-PAYMENT";
        AccountingPeriodException error;
        if (supplier)
        {
            // This test targets period rejection before financial posting, not a funded payment.
            var treasury = new Mock<ITreasuryReadService>();
            treasury.Setup(x => x.GetAccountBalanceAsync(tenantId, "1030", It.IsAny<CancellationToken>())).ReturnsAsync(100);
            var controller = new SuppliersController(db, tenant, NullLogger<SuppliersController>.Instance, Mock.Of<IValidationService>(), audit,
                ledger, outbox, accounts, treasury.Object, Mock.Of<ICashboxLedgerAccountService>(), idempotency)
            { ControllerContext = new ControllerContext { HttpContext = http } };
            error = await Assert.ThrowsAsync<AccountingPeriodException>(() => controller.CreateSupplierPayment(party,
                new CreateSupplierPaymentRequest { Amount = 10, PaymentMethod = PaymentMethod.BankTransfer, ReferenceNumber = "QA-ERP-R10-20261113-REJECTED" }, default));
        }
        else
        {
            var controller = new CustomersController(db, tenant, NullLogger<CustomersController>.Instance, Mock.Of<IValidationService>(), audit,
                ledger, idempotency, outbox, accounts, Mock.Of<ICashboxLedgerAccountService>())
            { ControllerContext = new ControllerContext { HttpContext = http } };
            error = await Assert.ThrowsAsync<AccountingPeriodException>(() => controller.CreateCustomerPayment(party,
                new CreateCustomerPaymentRequest { Amount = 10, PaymentMethod = PaymentMethod.BankTransfer, ReferenceNumber = "QA-ERP-R10-20261113-REJECTED" }, default));
        }
        Assert.Equal("PERIOD_NOT_OPEN", error.Code);
        await using var verify = scope.CreateDbContext(tenant);
        Assert.Empty(await verify.CustomerPayments.ToListAsync()); Assert.Empty(await verify.SupplierPayments.ToListAsync());
        Assert.Empty(await verify.CustomerTransactions.ToListAsync()); Assert.Empty(await verify.SupplierTransactions.ToListAsync());
        Assert.Empty(await verify.AccountLedgers.ToListAsync()); Assert.Empty(await verify.AccountingOutboxMessages.ToListAsync());
        Assert.Empty(await verify.JournalEntries.ToListAsync()); Assert.Empty(await verify.SalesRequestIdempotencyRecords.ToListAsync());
        Assert.Equal(0, (await verify.LedgerAccounts.SingleAsync()).CurrentBalance);
        if (supplier) Assert.Equal(0, (await verify.Suppliers.SingleAsync()).Balance);
        else Assert.Equal(0, (await verify.Customers.SingleAsync()).Balance);
    }
}
