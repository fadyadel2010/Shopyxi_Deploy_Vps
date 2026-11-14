using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Audit;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Helpers;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Tests.Infrastructure;
using Tender = PointOfSale.Core.Enums.PaymentMethod;

namespace PointOfSale.Tests.Integration;

public sealed class EgpFinancialPrecisionAcceptanceTests
{
    [Theory]
    [InlineData("0.005", "0.01")]
    [InlineData("1.005", "1.01")]
    [InlineData("-1.005", "-1.01")]
    [InlineData("123.454", "123.45")]
    [InlineData("123.455", "123.46")]
    public void EgpRoundingUsesDecimalTwoPlacesAndAwayFromZero(string input, string expected)
    {
        var value = decimal.Parse(input, CultureInfo.InvariantCulture);
        var rounded = decimal.Parse(expected, CultureInfo.InvariantCulture);
        Assert.Equal(rounded, CurrencyHelper.Round(value));
        Assert.Equal(rounded, ProductTaxPolicy.Round(value));
    }

    [Fact]
    public void EgpTaxUsesRoundedDiscountedBaseAndDoesNotAddInclusiveTaxTwice()
    {
        Assert.Equal(0.30m, 0.10m + 0.20m);
        Assert.Equal(1.40m, ProductTaxPolicy.CalculateAddedTax(10.05m, 0.05m, 14m, false));
        Assert.Equal(0.01m, ProductTaxPolicy.CalculateAddedTax(0.05m, 0m, 10m, false));
        Assert.Equal(0m, ProductTaxPolicy.CalculateAddedTax(10.05m, 0.05m, 14m, true));
        Assert.Equal(0m, ProductTaxPolicy.CalculateAddedTax(10.05m, 0.05m, 14m, false, false));
    }

    [Theory]
    [InlineData(false, "0.01")]
    [InlineData(false, "0.30")]
    [InlineData(false, "123.45")]
    [InlineData(true, "0.01")]
    [InlineData(true, "0.30")]
    [InlineData(true, "123.45")]
    public async Task EgpFractionalPaymentVoucherReportsAndCanonicalReversalRetainExactAmounts(bool supplierPayment, string input)
    {
        var amount = decimal.Parse(input, CultureInfo.InvariantCulture);
        await using var scope = TestDatabaseScope.Open();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        var customer = Guid.NewGuid(); var supplier = Guid.NewGuid();
        var receiptId = Guid.NewGuid(); var paymentId = Guid.NewGuid();
        var reference = "QA-ERP-R10-20261113-EGP-PRECISION-" + tenant.ToString("N");
        var now = DateTime.UtcNow;
        await using var db = scope.CreateDbContext(tenant);
        db.Tenants.Add(new Tenant { Id = tenant, Name = reference, Currency = "EGP", Status = TenantStatus.Active, IsActive = true });
        db.Users.Add(new User { Id = actor, TenantId = tenant, Username = reference, Email = tenant.ToString("N") + "@example.invalid",
            PasswordHash = "not-a-login", Role = UserRole.Owner, IsActive = true });
        db.Customers.Add(new Customer { Id = customer, TenantId = tenant, Name = reference, IsActive = true });
        db.Suppliers.Add(new Supplier { Id = supplier, TenantId = tenant, Name = reference, IsActive = true });
        var bank = Account.Create(tenant, AccountCodes.BankAccount, "QA internal bank ledger", AccountType.Asset); bank.PreventNegative = true;
        var receivable = Account.Create(tenant, AccountCodes.AccountsReceivable, "QA receivable", AccountType.Asset);
        var payable = Account.Create(tenant, AccountCodes.SupplierPayables, "QA payable", AccountType.Liability);
        db.LedgerAccounts.AddRange(bank, receivable, payable);
        db.TreasuryAccounts.Add(TreasuryAccount.CreateBank(tenant, bank.Id, reference));
        await db.SaveChangesAsync();
        var today = DateOnly.FromDateTime(now);
        await new AccountingPeriodService(db).CreateAsync(tenant, actor, "QA-EGP", reference, today.AddDays(-1), today.AddDays(1), true, reference);
        var ledger = new LedgerService(db, new TestTenantContext(tenant), NullLogger<LedgerService>.Instance, Mock.Of<IAuditService>());
        var posting = new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance);
        var vouchers = new FinancialVoucherService(db, ledger, posting);
        // Synthetic local source records use only internal ledger services. No provider is registered or called.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.CustomerPayments.Add(new CustomerPayment { Id = receiptId, TenantId = tenant, CustomerId = customer, Amount = amount,
                PaymentDate = now, PaymentMethod = Tender.BankTransfer, BankTransferAmount = amount,
                ReferenceNumber = reference, IdempotencyKey = reference + "-RECEIPT", CreatedBy = actor });
            var balance = await ledger.ApplyCustomerTransaction(customer, now, TransactionType.PaymentReceived, reference, 0, amount, receiptId, reference, actor);
            db.CustomerTransactions.Add(new CustomerTransaction { Id = Guid.NewGuid(), TenantId = tenant, CustomerId = customer,
                TransactionDate = now, TransactionType = TransactionType.PaymentReceived, CreditAmount = amount, BalanceAfter = balance, ReferenceId = receiptId, CreatedBy = actor });
            await db.SaveChangesAsync();
            await posting.PostJournalAsync(new PostingRequest { TenantId = tenant, UserId = actor, JournalDate = now, SourceType = nameof(CustomerPayment),
                SourceId = receiptId, SourceReference = reference, Description = reference, IdempotencyKey = reference + "-RECEIPT",
                Lines = [PostingLine.Debit(bank.Id, amount), PostingLine.Credit(receivable.Id, amount)] });
            if (supplierPayment)
            {
                db.SupplierPayments.Add(new SupplierPayment { Id = paymentId, TenantId = tenant, SupplierId = supplier, Amount = amount,
                    PaymentDate = now, PaymentMethod = Tender.BankTransfer, ReferenceNumber = reference, IdempotencyKey = reference + "-PAYMENT", CreatedBy = actor });
                balance = await ledger.ApplySupplierTransaction(supplier, now, TransactionType.PaymentMade, reference, amount, 0, paymentId, reference, actor);
                db.SupplierTransactions.Add(new SupplierTransaction { Id = Guid.NewGuid(), TenantId = tenant, SupplierId = supplier,
                    TransactionDate = now, TransactionType = TransactionType.PaymentMade, DebitAmount = amount, BalanceAfter = balance, ReferenceId = paymentId, CreatedBy = actor });
                await db.SaveChangesAsync();
                await posting.PostJournalAsync(new PostingRequest { TenantId = tenant, UserId = actor, JournalDate = now, SourceType = nameof(SupplierPayment),
                    SourceId = paymentId, SourceReference = reference, Description = reference, IdempotencyKey = reference + "-PAYMENT",
                    Lines = [PostingLine.Debit(payable.Id, amount), PostingLine.Credit(bank.Id, amount)] });
            }
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal(amount, (await db.CustomerPayments.SingleAsync()).Amount);
        if (supplierPayment) Assert.Equal(amount, (await db.SupplierPayments.SingleAsync()).Amount);
        var reports = new FinancialReportService(db, NullLogger<FinancialReportService>.Instance);
        var from = now.Date.AddDays(-1); var to = now.Date.AddDays(1);
        var source = await reports.GetTrialBalanceAsync(tenant, from, to);
        Assert.Equal(amount * (supplierPayment ? 2 : 1), source.TotalDebits);
        Assert.Equal(source.TotalDebits, source.TotalCredits);
        var kind = supplierPayment ? FinancialVoucherKind.Payment : FinancialVoucherKind.Receipt;
        var voucher = await vouchers.IssueAsync(tenant, actor, new(Guid.NewGuid(), kind, supplierPayment ? paymentId : receiptId, reference));
        Assert.Equal("EGP", voucher.Currency); Assert.Equal(amount, voucher.Amount);
        var issued = await reports.GetTrialBalanceAsync(tenant, from, to);
        Assert.Equal(source.TotalDebits, issued.TotalDebits); Assert.Equal(source.TotalCredits, issued.TotalCredits);
        await vouchers.ReverseAsync(tenant, voucher.Id, actor, new(1, reference + "-REVERSAL"));
        if (supplierPayment)
        {
            var funding = await vouchers.IssueAsync(tenant, actor, new(Guid.NewGuid(), FinancialVoucherKind.Receipt, receiptId, reference));
            await vouchers.ReverseAsync(tenant, funding.Id, actor, new(1, reference + "-FUNDING-REVERSAL"));
        }
        db.ChangeTracker.Clear();
        Assert.Equal("EGP", (await db.Tenants.SingleAsync()).Currency);
        Assert.All(await db.LedgerAccounts.ToListAsync(), a => Assert.Equal(0m, a.CurrentBalance));
        Assert.Equal(0m, (await db.Customers.SingleAsync()).Balance); Assert.Equal(0m, (await db.Suppliers.SingleAsync()).Balance);
        var trial = await reports.GetTrialBalanceAsync(tenant, from, to);
        var sheet = await reports.GetBalanceSheetAsync(tenant, to);
        var profit = await reports.GetProfitAndLossAsync(tenant, from, to);
        var flow = await reports.GetCashFlowAsync(tenant, from, to);
        Assert.Equal(source.TotalDebits * 2, trial.TotalDebits); Assert.Equal(trial.TotalDebits, trial.TotalCredits);
        Assert.Equal(0m, sheet.TotalAssets); Assert.Equal(0m, sheet.TotalLiabilities); Assert.Equal(0m, sheet.TotalEquity);
        Assert.Equal(0m, profit.NetProfit); Assert.Equal(0m, flow.NetCashChange); Assert.Equal(0m, flow.ClosingCashBalance);
        Assert.All(await db.JournalEntries.Include(j => j.LedgerEntries).ToListAsync(), j =>
        { Assert.Equal(j.TotalDebits, j.TotalCredits); Assert.Equal(amount, j.TotalDebits); Assert.Equal(actor, j.CreatedByUserId); Assert.NotNull(j.AccountingPeriodId); });
        var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var receipt = new { reference, tenant, actor, amount, currency = "EGP", supplierPayment, source.TotalDebits,
                compensatedDebits = trial.TotalDebits, compensatedCredits = trial.TotalCredits, sheet.TotalAssets, sheet.TotalLiabilities,
                profit.NetProfit, flow.NetCashChange, originalReceiptId = receiptId, supplierPaymentId = supplierPayment ? (Guid?)paymentId : null,
                vouchers = await db.FinancialVouchers.Select(v => new { v.Id, v.JournalId, v.ReversalJournalId, v.Amount, v.Currency, v.Status }).ToListAsync() };
            await using var file = new FileStream(Path.Combine(directory, "egp-precision-" + tenant.ToString("N") + ".json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    [Fact]
    public void GenerateExactSixMigrationUpgradeSqlWithoutApplyingToProduction()
    {
        using var scope = TestDatabaseScope.Open(); using var db = scope.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript("20261113090000_PartyArchivalLifecycle", "20261113140000_FinancialVoucherAuthority");
        Assert.DoesNotContain("TRUNCATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
        var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
            File.WriteAllText(Path.Combine(directory, "six-migration-upgrade.sql"), sql);
    }
}
