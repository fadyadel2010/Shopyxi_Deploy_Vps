using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using PointOfSale.API.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Audit;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;
using Tender = PointOfSale.Core.Enums.PaymentMethod;

namespace PointOfSale.Tests.Integration;

public sealed class FinancialVoucherIntegrationTests
{
    [Fact]
    public void AdditiveVoucherSchemaMatchesModelWithoutHistoricalPaymentReassignment()
    {
        using var scope = TestDatabaseScope.Open(); using var db = scope.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(db.FinancialVouchers.IgnoreQueryFilters()); Assert.Empty(db.FinancialVoucherEvents.IgnoreQueryFilters());
        var migration = new PointOfSale.Infrastructure.Migrations.FinancialVoucherAuthority { ActiveProvider = db.Database.ProviderName! };
        var sql = Assert.Single(migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>()).Sql;
        foreach (var forbidden in new[] { "DROP TABLE", "DELETE FROM", "TRUNCATE", "UPDATE " }) Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(FinancialVoucherKind.Receipt, "EGP")]
    [InlineData(FinancialVoucherKind.Payment, "EGP")]
    [InlineData(FinancialVoucherKind.Receipt, "USD")]
    [InlineData(FinancialVoucherKind.Payment, "USD")]
    public async Task IssueDoesNotPostTwice_AndLinkedReversalRestoresMoneyAndPartyStatements(FinancialVoucherKind kind, string currency)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, kind, currency);
        var request = Request(s); FinancialVoucher issued;
        await AssertReportTieOutAsync(scope, s, currency, "source-posted", compensated: false);
        await using (var db = scope.CreateDbContext(s.Tenant)) issued = await Service(db, s).IssueAsync(s.Tenant, s.Owner, request);
        await using (var db = scope.CreateDbContext(s.Tenant)) Assert.Equal(issued.Number, (await Service(db, s).IssueAsync(s.Tenant, s.Owner, request)).Number);
        await AssertReportTieOutAsync(scope, s, currency, "voucher-issued", compensated: false);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            Assert.Equal(kind == FinancialVoucherKind.Receipt ? 1 : 2, await db.JournalEntries.CountAsync());
            Assert.Equal(currency, issued.Currency); Assert.Equal(10, issued.Amount); Assert.Equal(s.Owner, issued.CreatedBy);
            Assert.Single(await db.FinancialVoucherEvents.ToListAsync());
        }
        var reversal = new ReverseFinancialVoucher(1, "QA-ERP-R10-20261113-VOUCHER-COMPENSATION");
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            var result = await Service(db, s).ReverseAsync(s.Tenant, issued.Id, s.Owner, reversal);
            Assert.Equal(FinancialVoucherStatus.Reversed, result.Status); Assert.NotNull(result.ReversalJournalId);
        }
        await using (var db = scope.CreateDbContext(s.Tenant)) Assert.Equal(2, (await Service(db, s).ReverseAsync(s.Tenant, issued.Id, s.Owner, reversal)).Version);
        if (kind == FinancialVoucherKind.Payment)
        {
            await using var db = scope.CreateDbContext(s.Tenant);
            var originalReceipt = await Service(db, s).IssueAsync(s.Tenant, s.Owner, new(Guid.NewGuid(), FinancialVoucherKind.Receipt, s.ReceiptId, "QA funding receipt document"));
            await Service(db, s).ReverseAsync(s.Tenant, originalReceipt.Id, s.Owner, new(1, "QA funding receipt compensation"));
        }
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.All(await verify.LedgerAccounts.ToListAsync(), account => Assert.Equal(0, account.CurrentBalance));
        Assert.Equal(0, (await verify.Customers.SingleAsync()).Balance); Assert.Equal(0, (await verify.Suppliers.SingleAsync()).Balance);
        Assert.Single(await verify.CustomerPayments.ToListAsync());
        Assert.Equal(kind == FinancialVoucherKind.Receipt ? 0 : 1, await verify.SupplierPayments.CountAsync());
        Assert.All(await verify.FinancialVoucherEvents.ToListAsync(), x => Assert.Equal(s.Owner, x.CreatedBy));
        Assert.Equal(2, await verify.CustomerTransactions.CountAsync());
        Assert.Equal(kind == FinancialVoucherKind.Receipt ? 0 : 2, await verify.SupplierTransactions.CountAsync());
        await AssertReportTieOutAsync(scope, s, currency, "fully-compensated", compensated: true);
    }

    [Fact]
    public async Task ConcurrentIssueAndReversalConfirmations_AreExactlyOnce()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Receipt, "EGP"); var request = Request(s);
        async Task<FinancialVoucher> Issue() { await using var db = scope.CreateDbContext(s.Tenant); return await Service(db, s).IssueAsync(s.Tenant, s.Owner, request); }
        var issued = await Task.WhenAll(Issue(), Issue()); Assert.Equal(issued[0].Number, issued[1].Number);
        async Task<FinancialVoucher> Reverse() { await using var db = scope.CreateDbContext(s.Tenant); return await Service(db, s).ReverseAsync(s.Tenant, request.OperationId, s.Owner, new(1, "QA simultaneous compensation")); }
        var reversed = await Task.WhenAll(Reverse(), Reverse()); Assert.Equal(reversed[0].ReversalJournalId, reversed[1].ReversalJournalId);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Single(await verify.FinancialVouchers.ToListAsync()); Assert.Equal(2, await verify.FinancialVoucherEvents.CountAsync());
        Assert.Equal(2, await verify.JournalEntries.CountAsync()); Assert.Equal(2, await verify.AccountLedgers.CountAsync());
    }

    [Theory]
    [InlineData(FinancialVoucherKind.Receipt)]
    [InlineData(FinancialVoucherKind.Payment)]
    public async Task ClosedPeriodCompensationRollsBackPartyStatementAndVoucherChanges(FinancialVoucherKind kind)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, kind, "EGP"); var request = Request(s);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            await Service(db, s).IssueAsync(s.Tenant, s.Owner, request);
            var period = await db.AccountingPeriods.SingleAsync();
            await new AccountingPeriodService(db).TransitionAsync(s.Tenant, period.Id, s.Owner, "close", period.Version, "QA closed compensation period");
        }
        await using (var db = scope.CreateDbContext(s.Tenant)) await Assert.ThrowsAsync<AccountingPeriodException>(() => Service(db, s).ReverseAsync(s.Tenant, request.OperationId, s.Owner, new(1, "QA denied compensation")));
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(FinancialVoucherStatus.Issued, (await verify.FinancialVouchers.SingleAsync()).Status);
        Assert.Single(await verify.FinancialVoucherEvents.ToListAsync());
        Assert.Equal(kind == FinancialVoucherKind.Receipt ? 1 : 2, await verify.JournalEntries.CountAsync());
        Assert.Equal(kind == FinancialVoucherKind.Receipt ? 1 : 2, await verify.AccountLedgers.CountAsync());
    }

    [Fact]
    public async Task DuplicateSourceChangedRequestAndDirectHistoryEditsAreRejected()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Receipt, "EGP"); var request = Request(s);
        await using (var db = scope.CreateDbContext(s.Tenant)) await Service(db, s).IssueAsync(s.Tenant, s.Owner, request);
        foreach (var other in new[] { request with { Reason = "Changed" }, request with { OperationId = Guid.NewGuid() } })
        { await using var db = scope.CreateDbContext(s.Tenant); await Assert.ThrowsAsync<FinancialVoucherException>(() => Service(db, s).IssueAsync(s.Tenant, s.Owner, other)); }
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            using var authority = FinancialVoucherMutationScope.Authorize();
            (await db.FinancialVouchers.SingleAsync()).Amount = 999;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            (await db.FinancialVoucherEvents.SingleAsync()).Reason = "Forged";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task CurrentActorAndTenantAuthorityAreRequired_NoUnpostedOrForeignSourceCanIssueVoucher()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Receipt, "EGP");
        await using (var db = scope.CreateDbContext(s.Tenant)) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(db, s).IssueAsync(s.Tenant, s.Cashier, Request(s)));
        await using (var db = scope.CreateDbContext(Guid.NewGuid()))
        { Assert.Empty(await db.FinancialVouchers.ToListAsync()); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(db, s).IssueAsync(s.Tenant, s.Owner, Request(s))); }
        await using (var db = scope.CreateDbContext(s.Tenant)) await Assert.ThrowsAsync<FinancialVoucherException>(() => Service(db, s).IssueAsync(s.Tenant, s.Owner, Request(s) with { SourcePaymentId = Guid.NewGuid() }));
    }

    private sealed record Scenario(Guid Tenant, Guid Owner, Guid Cashier, Guid Customer, Guid Supplier, Guid ReceiptId, Guid PaymentId, Guid Bank, Guid Receivable, Guid Payable, FinancialVoucherKind Kind);
    [Fact]
    public async Task DistinctConcurrentOperationsForOnePaymentIssueExactlyOneRetainedDocument()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Receipt, "EGP");
        async Task<bool> Issue()
        {
            await using var db = scope.CreateDbContext(s.Tenant);
            try { await Service(db, s).IssueAsync(s.Tenant, s.Owner, Request(s)); return true; }
            catch (FinancialVoucherException ex) { Assert.Equal("VOUCHER_ALREADY_ISSUED", ex.Code); return false; }
        }
        Assert.Equal(1, (await Task.WhenAll(Issue(), Issue())).Count(x => x));
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Single(await verify.FinancialVouchers.ToListAsync()); Assert.Single(await verify.FinancialVoucherEvents.ToListAsync());
        Assert.Single(await verify.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task OperationCannotBeReusedForAnotherPayment_AndStaleReversalCannotChangeHistory()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Payment, "EGP"); var request = Request(s);
        await using (var db = scope.CreateDbContext(s.Tenant)) await Service(db, s).IssueAsync(s.Tenant, s.Owner, request);
        await using (var db = scope.CreateDbContext(s.Tenant))
            Assert.Equal("VOUCHER_ALREADY_ISSUED", (await Assert.ThrowsAsync<FinancialVoucherException>(() => Service(db, s).IssueAsync(s.Tenant, s.Owner,
                request with { Kind = FinancialVoucherKind.Receipt, SourcePaymentId = s.ReceiptId }))).Code);
        await using (var db = scope.CreateDbContext(s.Tenant))
            Assert.Equal("VOUCHER_CONFLICT", (await Assert.ThrowsAsync<FinancialVoucherException>(() => Service(db, s).ReverseAsync(s.Tenant, request.OperationId, s.Owner, new(2, "QA stale confirmation")))).Code);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Single(await verify.FinancialVouchers.ToListAsync()); Assert.Single(await verify.FinancialVoucherEvents.ToListAsync());
        Assert.Equal(2, await verify.JournalEntries.CountAsync()); Assert.Equal(2, await verify.AccountLedgers.CountAsync());
    }

    [Fact]
    public async Task ReversalRechecksCurrentSqlRoleAndDisabledActorEvenWithAnExistingDocument()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Receipt, "EGP"); var request = Request(s);
        await using (var db = scope.CreateDbContext(s.Tenant)) await Service(db, s).IssueAsync(s.Tenant, s.Owner, request);
        foreach (var disable in new[] { false, true })
        {
            await using (var db = scope.CreateDbContext(s.Tenant))
            {
                var user = await db.Users.SingleAsync(x => x.Id == s.Owner);
                user.Role = disable ? UserRole.Owner : UserRole.Cashier; user.IsActive = !disable;
                await db.SaveChangesAsync();
            }
            await using var denied = scope.CreateDbContext(s.Tenant);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(denied, s).ReverseAsync(s.Tenant, request.OperationId, s.Owner, new(1, "QA revoked authority")));
        }
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(FinancialVoucherStatus.Issued, (await verify.FinancialVouchers.SingleAsync()).Status);
        Assert.Single(await verify.JournalEntries.ToListAsync()); Assert.Single(await verify.AccountLedgers.ToListAsync());
    }

    [Fact]
    public async Task VoucherEndpointsReturnScalarContractsAndRestrictJournalLookupToExactDocumentLinks()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Payment, "EGP"); var request = Request(s);
        await using var db = scope.CreateDbContext(s.Tenant);
        var voucher = await Service(db, s).IssueAsync(s.Tenant, s.Owner, request);
        var controller = new FinancialVouchersController(db, new TestTenantContext(s.Tenant), Service(db, s));
        Assert.IsType<OkObjectResult>(await controller.Detail(voucher.Id, default));
        var journal = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Journal(voucher.Id, voucher.JournalId, default)).Value);
        Assert.Equal(2, journal.GetProperty("lines").GetArrayLength());
        var unrelated = await db.JournalEntries.Where(x => x.Id != voucher.JournalId).Select(x => x.Id).SingleAsync();
        Assert.IsType<NotFoundResult>(await controller.Journal(voucher.Id, unrelated, default));
        Assert.IsType<NotFoundResult>(await controller.Detail(Guid.NewGuid(), default));
        await using var foreign = scope.CreateDbContext(Guid.NewGuid());
        var foreignController = new FinancialVouchersController(foreign, new TestTenantContext(foreign.ScopedTenantId!.Value), Service(foreign, s));
        Assert.IsType<NotFoundResult>(await foreignController.Detail(voucher.Id, default));
        Assert.IsType<NotFoundResult>(await foreignController.Journal(voucher.Id, voucher.JournalId, default));
        Assert.All(typeof(VoucherResponse).GetProperties(), p => Assert.False(p.PropertyType.IsClass && p.PropertyType != typeof(string)));
    }
    [Fact]
    public async Task UnpostedSourceCannotIssueAReceiptOrCreateANewJournal()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Receipt, "EGP"); var pending = Guid.NewGuid();
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            db.CustomerPayments.Add(new CustomerPayment { Id = pending, TenantId = s.Tenant, CustomerId = s.Customer, PaymentDate = DateTime.UtcNow,
                Amount = 1, BankTransferAmount = 1, PaymentMethod = Tender.BankTransfer, IdempotencyKey = "QA-UNPOSTED" });
            await db.SaveChangesAsync();
            Assert.Equal("VOUCHER_PAYMENT_NOT_POSTED", (await Assert.ThrowsAsync<FinancialVoucherException>(() => Service(db, s).IssueAsync(s.Tenant, s.Owner, Request(s) with { SourcePaymentId = pending }))).Code);
        }
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Empty(await verify.FinancialVouchers.ToListAsync()); Assert.Single(await verify.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task FrozenCurrencyMismatchRejectsCompensationWithoutConvertingOrChangingMoney()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Receipt, "EGP"); var request = Request(s);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            // Isolated malformed legacy-document fixture. Ordinary saves cannot rebase a tenant.
            using var fixtureAuthority = FinancialVoucherMutationScope.Authorize();
            db.FinancialVouchers.Add(new FinancialVoucher { Id = request.OperationId, TenantId = s.Tenant, Number = "QA-ERP-R10-20261113-MISMATCH",
                RequestFingerprint = new string('A', 64), Kind = FinancialVoucherKind.Receipt, SourcePaymentId = s.ReceiptId, PartyId = s.Customer,
                PartyName = "QA voucher customer", Amount = 10, Currency = "USD", PaymentDate = (await db.CustomerPayments.SingleAsync()).PaymentDate,
                JournalId = (await db.JournalEntries.SingleAsync()).Id, Reason = "QA malformed legacy currency fixture", CreatedBy = s.Owner });
            await db.SaveChangesAsync();
            Assert.Equal("VOUCHER_CURRENCY_MISMATCH", (await Assert.ThrowsAsync<FinancialVoucherException>(() => Service(db, s).ReverseAsync(s.Tenant, request.OperationId, s.Owner, new(1, "QA mismatch denial")))).Code);
        }
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            Assert.Equal(1, await db.JournalEntries.CountAsync()); Assert.Equal(1, await db.AccountLedgers.CountAsync());
            Assert.Equal(FinancialVoucherStatus.Issued, (await db.FinancialVouchers.SingleAsync()).Status);
            Assert.Equal("EGP", (await db.Tenants.SingleAsync()).Currency);
        }
    }

    [Fact]
    public async Task FullyCompensatedMoneyDoesNotPermitRelabelingRetainedPaymentHistory()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Receipt, "EGP"); var request = Request(s);
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            await Service(db, s).IssueAsync(s.Tenant, s.Owner, request);
            await Service(db, s).ReverseAsync(s.Tenant, request.OperationId, s.Owner, new(1, "QA fully compensated before rebase denial"));
            Assert.All(await db.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
            (await db.Tenants.SingleAsync()).Currency = "USD";
            await Assert.ThrowsAsync<PointOfSale.Core.Financial.AccountingCurrencyChangeException>(() => db.SaveChangesAsync());
        }
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal("EGP", (await verify.Tenants.SingleAsync()).Currency);
        Assert.Equal("EGP", (await verify.FinancialVouchers.SingleAsync()).Currency);
        Assert.Equal(2, await verify.JournalEntries.CountAsync()); Assert.Single(await verify.CustomerPayments.ToListAsync());
    }

    [Fact]
    public async Task InsufficientReceiptFundsCannotLeaveAPartialPartyOrVoucherReversal()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, FinancialVoucherKind.Payment, "EGP");
        FinancialVoucher receipt, payment;
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            receipt = await Service(db, s).IssueAsync(s.Tenant, s.Owner, new(Guid.NewGuid(), FinancialVoucherKind.Receipt, s.ReceiptId, "QA funding receipt"));
            payment = await Service(db, s).IssueAsync(s.Tenant, s.Owner, Request(s));
        }
        await using (var db = scope.CreateDbContext(s.Tenant)) await Assert.ThrowsAsync<PointOfSale.Core.Exceptions.Accounting.InsufficientFundsException>(() => Service(db, s)
            .ReverseAsync(s.Tenant, receipt.Id, s.Owner, new(1, "QA unavailable receipt funds")));
        await using (var db = scope.CreateDbContext(s.Tenant))
        {
            Assert.Equal(2, await db.JournalEntries.CountAsync()); Assert.Equal(2, await db.AccountLedgers.CountAsync());
            Assert.Equal(2, await db.FinancialVoucherEvents.CountAsync()); Assert.Equal(-10, (await db.Customers.SingleAsync()).Balance);
            Assert.Equal(FinancialVoucherStatus.Issued, (await db.FinancialVouchers.SingleAsync(x => x.Id == receipt.Id)).Status);
            await Service(db, s).ReverseAsync(s.Tenant, payment.Id, s.Owner, new(1, "QA supplier compensation"));
            await Service(db, s).ReverseAsync(s.Tenant, receipt.Id, s.Owner, new(1, "QA restored receipt funds compensation"));
        }
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.All(await verify.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
    }
    [Theory]
    [InlineData(FinancialVoucherKind.Receipt)]
    [InlineData(FinancialVoucherKind.Payment)]
    public async Task SourcesAreSearchablePaginatedTenantScoped_AndIssuedPaymentsDisappearFromCandidates(FinancialVoucherKind kind)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope, kind, "EGP");
        await using var db = scope.CreateDbContext(s.Tenant);
        var controller = new FinancialVouchersController(db, new TestTenantContext(s.Tenant), Service(db, s));
        var response = Assert.IsType<OkObjectResult>(await controller.Sources(kind, "QA voucher", 1));
        var json = JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.True(json.GetProperty("items")[0].GetProperty("Posted").GetBoolean());
        await Service(db, s).IssueAsync(s.Tenant, s.Owner, Request(s));
        var after = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Sources(kind, "QA voucher", 1)).Value);
        Assert.Equal(0, after.GetProperty("total").GetInt32());
        await using var foreign = scope.CreateDbContext(Guid.NewGuid());
        var foreignController = new FinancialVouchersController(foreign, new TestTenantContext(Guid.NewGuid()), Service(foreign, s));
        var empty = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await foreignController.Sources(kind, null, 1)).Value);
        Assert.Equal(0, empty.GetProperty("total").GetInt32());
    }
    private static async Task<Scenario> Seed(TestDatabaseScope scope, FinancialVoucherKind kind, string currency)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind);
        await using var db = scope.CreateDbContext(s.Tenant); var now = DateTime.UtcNow;
        db.Tenants.Add(new Tenant { Id = s.Tenant, Name = "QA-ERP-R10-20261113-VOUCHER", Currency = currency, Status = TenantStatus.Active, IsActive = true });
        foreach (var (id, role) in new[] { (s.Owner, UserRole.Owner), (s.Cashier, UserRole.Cashier) }) db.Users.Add(new User { Id = id, TenantId = s.Tenant,
            Username = $"qa-{id:N}", Email = $"{id:N}@example.invalid", PasswordHash = "not-a-login", IsActive = true, Role = role });
        db.Customers.Add(new Customer { Id = s.Customer, TenantId = s.Tenant, Name = "QA voucher customer", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = s.Supplier, TenantId = s.Tenant, Name = "QA voucher supplier", IsActive = true });
        var bank = Account.Create(s.Tenant, AccountCodes.BankAccount, "QA bank", AccountType.Asset); bank.Id = s.Bank; bank.PreventNegative = true;
        var receivable = Account.Create(s.Tenant, AccountCodes.AccountsReceivable, "QA receivable", AccountType.Asset); receivable.Id = s.Receivable;
        var payable = Account.Create(s.Tenant, AccountCodes.SupplierPayables, "QA payable", AccountType.Liability); payable.Id = s.Payable;
        db.LedgerAccounts.AddRange(bank, receivable, payable); db.TreasuryAccounts.Add(TreasuryAccount.CreateBank(s.Tenant, bank.Id, "QA bank"));
        await db.SaveChangesAsync();
        var today = DateOnly.FromDateTime(now);
        await new AccountingPeriodService(db).CreateAsync(s.Tenant, s.Owner, "QA-VOUCHERS", "QA voucher period", today.AddDays(-1), today.AddDays(1), true, "QA voucher fixture");
        var ledger = Ledger(db, s);
        await using var tx = await db.Database.BeginTransactionAsync();
        db.CustomerPayments.Add(new CustomerPayment { Id = s.ReceiptId, TenantId = s.Tenant, CustomerId = s.Customer, PaymentDate = now, Amount = 10,
            PaymentMethod = Tender.BankTransfer, BankTransferAmount = 10, IdempotencyKey = "QA-VOUCHER-RECEIPT", ReferenceNumber = "QA-ERP-R10-20261113-VOUCHER-RECEIPT", CreatedBy = s.Owner });
        var customerBalance = await ledger.ApplyCustomerTransaction(s.Customer, now, TransactionType.PaymentReceived, "QA receipt", 0, 10, s.ReceiptId, "QA receipt", s.Owner);
        db.CustomerTransactions.Add(new CustomerTransaction { Id = Guid.NewGuid(), TenantId = s.Tenant, CustomerId = s.Customer, TransactionDate = now,
            TransactionType = TransactionType.PaymentReceived, CreditAmount = 10, BalanceAfter = customerBalance, ReferenceId = s.ReceiptId, CreatedBy = s.Owner });
        await db.SaveChangesAsync();
        await Posting(db).PostJournalAsync(new PostingRequest { TenantId = s.Tenant, UserId = s.Owner, JournalDate = now, SourceType = nameof(CustomerPayment), SourceId = s.ReceiptId,
            SourceReference = "QA receipt", Description = "QA receipt", IdempotencyKey = "QA-VOUCHER-RECEIPT", Lines = [PostingLine.Debit(s.Bank, 10), PostingLine.Credit(s.Receivable, 10)] });
        if (kind == FinancialVoucherKind.Payment)
        {
            db.SupplierPayments.Add(new SupplierPayment { Id = s.PaymentId, TenantId = s.Tenant, SupplierId = s.Supplier, PaymentDate = now, Amount = 10,
                PaymentMethod = Tender.BankTransfer, IdempotencyKey = "QA-VOUCHER-PAYMENT", ReferenceNumber = "QA-ERP-R10-20261113-VOUCHER-PAYMENT", CreatedBy = s.Owner });
            var supplierBalance = await ledger.ApplySupplierTransaction(s.Supplier, now, TransactionType.PaymentMade, "QA payment", 10, 0, s.PaymentId, "QA payment", s.Owner);
            db.SupplierTransactions.Add(new SupplierTransaction { Id = Guid.NewGuid(), TenantId = s.Tenant, SupplierId = s.Supplier, TransactionDate = now,
                TransactionType = TransactionType.PaymentMade, DebitAmount = 10, BalanceAfter = supplierBalance, ReferenceId = s.PaymentId, CreatedBy = s.Owner });
            await db.SaveChangesAsync();
            await Posting(db).PostJournalAsync(new PostingRequest { TenantId = s.Tenant, UserId = s.Owner, JournalDate = now, SourceType = nameof(SupplierPayment), SourceId = s.PaymentId,
                SourceReference = "QA payment", Description = "QA payment", IdempotencyKey = "QA-VOUCHER-PAYMENT", Lines = [PostingLine.Debit(s.Payable, 10), PostingLine.Credit(s.Bank, 10)] });
        }
        await tx.CommitAsync(); return s;
    }
    private static LedgerService Ledger(ApplicationDbContext db, Scenario s) => new(db, new TestTenantContext(s.Tenant), NullLogger<LedgerService>.Instance, Mock.Of<IAuditService>());
    private static async Task AssertReportTieOutAsync(TestDatabaseScope scope, Scenario s, string currency, string phase, bool compensated)
    {
        await using var db = scope.CreateDbContext(s.Tenant);
        var reports = new FinancialReportService(db, NullLogger<FinancialReportService>.Instance);
        var from = DateTime.UtcNow.Date.AddDays(-1); var to = DateTime.UtcNow.Date.AddDays(1);
        var trial = await reports.GetTrialBalanceAsync(s.Tenant, from, to);
        var sheet = await reports.GetBalanceSheetAsync(s.Tenant, to);
        var profit = await reports.GetProfitAndLossAsync(s.Tenant, from, to);
        var flow = await reports.GetCashFlowAsync(s.Tenant, from, to);
        var bank = await reports.GetGeneralLedgerAsync(s.Tenant, s.Bank, from, to);
        var receivable = await reports.GetGeneralLedgerAsync(s.Tenant, s.Receivable, from, to);
        var payable = await reports.GetGeneralLedgerAsync(s.Tenant, s.Payable, from, to);
        var expectedBank = compensated || s.Kind == FinancialVoucherKind.Payment ? 0m : 10m;
        var expectedReceivable = compensated ? 0m : -10m;
        var expectedPayable = compensated || s.Kind == FinancialVoucherKind.Receipt ? 0m : -10m;
        var expectedGross = (s.Kind == FinancialVoucherKind.Receipt ? 10m : 20m) * (compensated ? 2 : 1);
        Assert.True(trial.IsBalanced); Assert.Equal(expectedGross, trial.TotalDebits); Assert.Equal(expectedGross, trial.TotalCredits);
        Assert.True(sheet.IsBalanced); Assert.Equal(expectedBank + expectedReceivable, sheet.TotalAssets);
        Assert.Equal(expectedPayable, sheet.TotalLiabilities); Assert.Equal(0, sheet.TotalEquity);
        Assert.Equal(0, profit.NetProfit); Assert.Equal(0, flow.NetProfit);
        Assert.True(flow.Reconciles); Assert.Equal(0, flow.OpeningCashBalance); Assert.Equal(expectedBank, flow.ClosingCashBalance);
        Assert.Equal(expectedBank, flow.NetCashChange); Assert.Equal(expectedBank, flow.OperatingCashFlow);
        Assert.Equal(0, flow.InvestingCashFlow); Assert.Equal(0, flow.FinancingCashFlow);
        Assert.Equal(expectedBank, bank.ClosingBalance); Assert.Equal(expectedReceivable, receivable.ClosingBalance); Assert.Equal(expectedPayable, payable.ClosingBalance);
        Assert.Equal(currency, (await db.Tenants.SingleAsync()).Currency);
        var vouchers = await db.FinancialVouchers.AsNoTracking().ToListAsync();
        Assert.All(vouchers, x => Assert.Equal(currency, x.Currency));
        var journals = await db.JournalEntries.AsNoTracking().Include(x => x.LedgerEntries).ToListAsync();
        Assert.All(journals, x => { Assert.Equal(s.Owner, x.CreatedByUserId); Assert.Equal(x.TotalDebits, x.TotalCredits); });
        // Keep actual source/document/actor links and nonzero/compensated report
        // snapshots as local evidence, not only assertion counts. No database write.
        var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var receipt = new { reference = "QA-ERP-R10-20261113-VOUCHER-REPORT", localOnly = true, production = false, phase,
                tenantId = s.Tenant, actorId = s.Owner, fixtureCurrency = currency, kind = s.Kind.ToString(),
                sourceReceiptId = s.ReceiptId, sourceSupplierPaymentId = s.Kind == FinancialVoucherKind.Payment ? (Guid?)s.PaymentId : null,
                trial.TotalDebits, trial.TotalCredits, sheet.TotalAssets, sheet.TotalLiabilities, sheet.TotalEquity, profit.NetProfit,
                flow.OpeningCashBalance, flow.ClosingCashBalance, flow.OperatingCashFlow, flow.InvestingCashFlow, flow.FinancingCashFlow,
                bankClosingBalance = bank.ClosingBalance, receivableClosingBalance = receivable.ClosingBalance, payableClosingBalance = payable.ClosingBalance,
                vouchers = vouchers.Select(x => new { x.Id, x.Number, x.SourcePaymentId, x.Currency, x.JournalId, x.ReversalJournalId, x.Status, x.CreatedBy, x.ReversedBy }),
                journals = journals.Select(x => new { x.Id, x.SourceId, x.SourceType, x.AccountingPeriodId, x.CreatedByUserId, x.Status, x.ReversesJournalId, x.ReversedByJournalId, x.TotalDebits, x.TotalCredits }) };
            await using var file = new FileStream(Path.Combine(directory, $"voucher-reports-{s.Tenant:N}-{phase}.json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        }
    }
    private static FinancialPostingService Posting(ApplicationDbContext db) => new(db, NullLogger<FinancialPostingService>.Instance);
    private static FinancialVoucherService Service(ApplicationDbContext db, Scenario s) => new(db, Ledger(db, s), Posting(db));
    private static IssueFinancialVoucher Request(Scenario s) => new(Guid.NewGuid(), s.Kind, s.Kind == FinancialVoucherKind.Receipt ? s.ReceiptId : s.PaymentId, "QA-ERP-R10-20261113-VOUCHER-ISSUE");
}
