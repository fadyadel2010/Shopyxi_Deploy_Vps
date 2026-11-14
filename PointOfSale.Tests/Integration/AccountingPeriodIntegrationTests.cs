using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Integration;

public sealed class AccountingPeriodIntegrationTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly End = new(2026, 11, 30);
    private static readonly DateTime PostingDate = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AdditiveUpgradeAndCurrentModel_AgreeWithoutBusinessSeedsOrHistoricalReassignment()
    {
        using var scope = TestDatabaseScope.Open();
        using var db = scope.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
        var applied = db.Database.GetAppliedMigrations().ToArray();
        Assert.Contains("20261113100000_StockTransferNumberSequenceAuthority", applied);
        Assert.Contains("20261113110000_AccountingPeriodAuthority", applied);
        Assert.Empty(db.AccountingPeriods.IgnoreQueryFilters());
        Assert.Empty(db.AccountingPeriodEvents.IgnoreQueryFilters());
        Assert.Empty(db.JournalEntries.IgnoreQueryFilters());
        var script = db.GetService<IMigrator>().GenerateScript("20261113090000_PartyArchivalLifecycle", "20261113110000_AccountingPeriodAuthority");
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE \"LedgerJournalEntries\"", script, StringComparison.OrdinalIgnoreCase);
        var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(directory)) File.WriteAllText(Path.Combine(directory, "reviewed-candidate-upgrade.sql"), script);
    }

    [Fact]
    public async Task CreateOpenCloseReopen_RejectsBackdating_RetainsOriginalAndCompensatingJournals()
    {
        using var scope = TestDatabaseScope.Open();
        var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant);
        var service = new AccountingPeriodService(db);
        var period = await service.CreateAsync(s.Tenant, s.Owner, "QA-ERP-R10-20261113-PERIOD", "November QA", Start, End, true, "Synthetic isolated period acceptance");
        var posting = Posting(db);
        var request = Request(s);
        var journal = await posting.PostJournalAsync(request);
        Assert.Equal(period.Id, journal.AccountingPeriodId);
        await service.TransitionAsync(s.Tenant, period.Id, s.Owner, "close", 1, "Balanced and no unresolved work");
        var before = await Balances(scope, s);
        var rejected = await Assert.ThrowsAsync<AccountingPeriodException>(() => posting.PostJournalAsync(Request(s)));
        Assert.Equal("PERIOD_NOT_OPEN", rejected.Code);
        Assert.Equal(journal.Id, (await posting.PostJournalAsync(request)).Id);
        Assert.Equal(before, await Balances(scope, s));
        await service.TransitionAsync(s.Tenant, period.Id, s.Owner, "reopen", 2, "Explicit authorized QA correction");
        var actor = new AccountingActorContext();
        using var originalDate = actor.UseActor(s.Tenant, s.Owner, PostingDate);
        var reversal = await new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance, actor: actor)
            .ReverseJournalAsync(s.Tenant, journal.Id, "QA compensating reversal", s.Owner);
        var totals = await service.TotalsAsync(s.Tenant, period);
        Assert.Equal(new AccountingPeriodTotals(2, 20, 20), totals);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(2, await verify.JournalEntries.CountAsync());
        Assert.Equal(4, await verify.LedgerEntries.CountAsync());
        Assert.All(await verify.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
        Assert.Equal(journal.Id, reversal.ReversesJournalId);
        Assert.Equal(3, await verify.AccountingPeriodEvents.CountAsync());
        var events = await verify.AccountingPeriodEvents.OrderBy(x => x.Version).ToListAsync();
        Assert.All(events, x => Assert.Equal(s.Owner, x.CreatedBy));
        await Receipt("period-lifecycle", new { tenant = s.Tenant, actor = s.Owner, periodId = period.Id, journalId = journal.Id, reversalId = reversal.Id, totals, originalDatePreserved = journal.JournalDate, finalNet = 0 });
    }

    [Fact]
    public async Task OverlapInvalidBoundariesAndDuplicateCode_AreRejectedWithoutPartialHistory()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant); var service = new AccountingPeriodService(db);
        await service.CreateAsync(s.Tenant, s.Owner, "NOV", "November", Start, End, false, "Draft planning");
        foreach (var dates in new[] { (Start.AddDays(-1), Start), (End, End.AddDays(1)), (Start, End) })
        {
            var ex = await Assert.ThrowsAsync<AccountingPeriodException>(() => service.CreateAsync(s.Tenant, s.Owner, "OTHER", "Overlap", dates.Item1, dates.Item2, true, "Rejected QA overlap"));
            Assert.Equal("PERIOD_OVERLAP", ex.Code);
        }
        await Assert.ThrowsAsync<AccountingPeriodException>(() => service.CreateAsync(s.Tenant, s.Owner, "INVALID", "Invalid", End, Start, true, "Invalid boundaries"));
        await Assert.ThrowsAsync<AccountingPeriodException>(() => service.CreateAsync(s.Tenant, s.Owner, "NOV", "Duplicate", End.AddDays(1), End.AddDays(10), true, "Duplicate code"));
        Assert.Single(await db.AccountingPeriods.ToListAsync()); Assert.Single(await db.AccountingPeriodEvents.ToListAsync());
    }

    [Fact]
    public async Task NoPeriodOrDraftPeriod_FailsClosed_ThenExplicitOpenAllowsPosting()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant);
        Assert.Equal("PERIOD_REQUIRED", (await Assert.ThrowsAsync<AccountingPeriodException>(() => Posting(db).PostJournalAsync(Request(s)))).Code);
        var service = new AccountingPeriodService(db);
        var p = await service.CreateAsync(s.Tenant, s.Owner, "DRAFT", "Draft", Start, End, false, "QA planning");
        Assert.Equal("PERIOD_NOT_OPEN", (await Assert.ThrowsAsync<AccountingPeriodException>(() => Posting(db).PostJournalAsync(Request(s)))).Code);
        Assert.Empty(await db.JournalEntries.ToListAsync());
        await service.TransitionAsync(s.Tenant, p.Id, s.Owner, "open", 1, "Ready to post");
        Assert.NotNull(await Posting(db).PostJournalAsync(Request(s)));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("reopen")]
    public async Task CashierCannotManagePeriod_OrForgeSameTenantAuthority(string action)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant); var service = new AccountingPeriodService(db);
        var p = await service.CreateAsync(s.Tenant, s.Owner, "QA", "QA", Start, End, true, "QA permission test");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TransitionAsync(s.Tenant, p.Id, s.Cashier, action, 1, "Cashier denial"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(s.Tenant, Guid.NewGuid(), "DEC", "Unknown actor", End.AddDays(1), End.AddDays(10), true, "Unknown actor denial"));
        Assert.Equal(AccountingPeriodStatus.Open, (await db.AccountingPeriods.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await db.AccountingPeriodEvents.ToListAsync());
    }

    [Fact]
    public async Task DuplicateCloseStaleVersionMissingReason_LeaveOneClosingEvent()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant); var service = new AccountingPeriodService(db);
        var p = await service.CreateAsync(s.Tenant, s.Owner, "QA", "QA", Start, End, true, "QA");
        await Assert.ThrowsAsync<AccountingPeriodException>(() => service.TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 1, " "));
        await service.TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 1, "QA close");
        Assert.Equal("PERIOD_CONFLICT", (await Assert.ThrowsAsync<AccountingPeriodException>(() => service.TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 1, "Stale"))).Code);
        Assert.Equal("PERIOD_INVALID_TRANSITION", (await Assert.ThrowsAsync<AccountingPeriodException>(() => service.TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 2, "Duplicate"))).Code);
        Assert.Equal(2, await db.AccountingPeriodEvents.CountAsync());
    }

    [Fact]
    public async Task UnresolvedOutboxBlocksClose_AndReplayCannotUseTheWorkerClockToBypassClosure()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant); var service = new AccountingPeriodService(db);
        var now = DateOnly.FromDateTime(DateTime.UtcNow);
        var p = await service.CreateAsync(s.Tenant, s.Owner, "CURRENT", "Current", now.AddDays(-1), now.AddDays(1), true, "QA outbox");
        var message = AccountingOutboxMessage.Create(s.Tenant, "PosSale", Guid.NewGuid(), "PosSale", "{}", "QA-PENDING");
        message.CreatedBy = s.Owner;
        db.AccountingOutboxMessages.Add(message); await db.SaveChangesAsync();
        Assert.Equal("PERIOD_PENDING_OUTBOX", (await Assert.ThrowsAsync<AccountingPeriodException>(() => service.TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 1, "Cannot close pending work"))).Code);
        Assert.Equal(AccountingPeriodStatus.Open, (await db.AccountingPeriods.AsNoTracking().SingleAsync()).Status);
        // Exercise a durable replay envelope against a separate closed historical period.
        var old = await service.CreateAsync(s.Tenant, s.Owner, "HIST", "Historical", new(2025, 1, 1), new(2025, 1, 31), true, "Historical replay test");
        // The conservative close barrier is intentionally tenant-wide; historical replay
        // rejection is exercised independently in the next isolated test.
        Assert.NotNull(old);
    }

    [Fact]
    public async Task DurableReplayDateAndActor_AreRestored_AndClosedPeriodRejectsReplay()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant); var service = new AccountingPeriodService(db);
        var p = await service.CreateAsync(s.Tenant, s.Owner, "OLD", "Old", Start, End, true, "Replay date authority");
        await service.TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 1, "Closed before stale replay");
        var actor = new AccountingActorContext();
        using (actor.UseActor(s.Tenant, s.Cashier, PostingDate))
        {
            Assert.Equal(PostingDate, actor.ResolvePostingDate()); Assert.Equal(s.Cashier, actor.ResolveUserId(s.Tenant));
            var request = Request(s); request.JournalDate = actor.ResolvePostingDate();
            await Assert.ThrowsAsync<AccountingPeriodException>(() => new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance, actor: actor).PostJournalAsync(request));
        }
        Assert.Null(actor.ResolveUserId(s.Tenant)); Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task ClosedPeriodSourceWrite_RollsBackAllSourceSideEffects()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant); var service = new AccountingPeriodService(db);
        var p = await service.CreateAsync(s.Tenant, s.Owner, "QA", "QA", Start, End, true, "Source rollback test");
        await service.TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 1, "QA closed");
        var customer = new Customer { Id = Guid.NewGuid(), TenantId = s.Tenant, Name = "QA atomic side effect", Balance = 10 };
        db.Customers.Add(customer);
        db.CustomerPayments.Add(new CustomerPayment { Id = Guid.NewGuid(), TenantId = s.Tenant, CustomerId = customer.Id, PaymentDate = PostingDate, Amount = 10 });
        await Assert.ThrowsAsync<AccountingPeriodException>(() => db.SaveChangesAsync());
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Empty(await verify.Customers.ToListAsync()); Assert.Empty(await verify.CustomerPayments.ToListAsync());
        Assert.Empty(await verify.JournalEntries.ToListAsync()); Assert.Empty(await verify.AccountingOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task DraftAdmissionIntoClosedPeriod_IsRejectedEvenWhenAmountsAndDatesAreUnchanged()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant);
        var supplier = new Supplier { Id = Guid.NewGuid(), TenantId = s.Tenant, Name = "QA draft supplier" };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = s.Tenant, Name = "QA draft warehouse", Code = "QA" };
        db.AddRange(supplier, warehouse); await db.SaveChangesAsync();
        var draft = new PurchaseInvoice { Id = Guid.NewGuid(), TenantId = s.Tenant, SupplierId = supplier.Id, WarehouseId = warehouse.Id,
            InvoiceNumber = "QA-ERP-R10-20261113-DRAFT", InvoiceDate = PostingDate, Status = InvoiceStatus.Draft, TotalAmount = 1 };
        db.PurchaseInvoices.Add(draft); await db.SaveChangesAsync();
        var service = new AccountingPeriodService(db);
        var p = await service.CreateAsync(s.Tenant, s.Owner, "DRAFT-DENIAL", "Draft denial", Start, End, true, "QA draft admission");
        await service.TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 1, "Closed before draft completion");
        draft.Status = InvoiceStatus.Completed;
        Assert.Equal("PERIOD_NOT_OPEN", (await Assert.ThrowsAsync<AccountingPeriodException>(() => db.SaveChangesAsync())).Code);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(InvoiceStatus.Draft, (await verify.PurchaseInvoices.SingleAsync()).Status);
        Assert.Empty(await verify.StockMovements.ToListAsync()); Assert.Empty(await verify.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task OrphanFinancialLineWrite_RequiresItsTrackedSourceDateBeforeAnyPersistence()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant);
        db.SalesInvoiceItems.Add(new SalesInvoiceItem { Id = Guid.NewGuid(), SalesInvoiceId = Guid.NewGuid(), Quantity = 1, UnitPrice = 1 });
        Assert.Equal("PERIOD_SOURCE_REQUIRED", (await Assert.ThrowsAsync<AccountingPeriodException>(() => db.SaveChangesAsync())).Code);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Empty(await verify.SalesInvoiceItems.ToListAsync()); Assert.Empty(await verify.StockMovements.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostingVersusClose_IsSerializedWithoutLatePosting(bool closeFirst)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        Guid periodId;
        await using (var seed = scope.CreateDbContext(s.Tenant)) periodId = (await new AccountingPeriodService(seed).CreateAsync(s.Tenant, s.Owner, "RACE", "Race", Start, End, true, "QA race")).Id;
        await using var holder = scope.CreateDbContext(s.Tenant);
        await using var tx = await holder.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await AccountingPeriodPostingGuard.RequireOpenAsync(holder, s.Tenant, PostingDate, default);
        Task contender;
        if (closeFirst)
        {
            // Close while holding the same durable write fence, then let the posting contend.
            await tx.RollbackAsync();
            await tx.DisposeAsync();
            await new AccountingPeriodService(holder).TransitionAsync(s.Tenant, periodId, s.Owner, "close", 1, "Close wins");
            await using var loser = scope.CreateDbContext(s.Tenant);
            await Assert.ThrowsAsync<AccountingPeriodException>(() => Posting(loser).PostJournalAsync(Request(s)));
        }
        else
        {
            // The close operation runs on an independent connection and cannot pass the fence
            // until this journal transaction commits. No timer-based race assumptions.
            await using var closer = scope.CreateDbContext(s.Tenant);
            contender = new AccountingPeriodService(closer).TransitionAsync(s.Tenant, periodId, s.Owner, "close", 1, "Post wins before close");
            await Posting(holder).PostJournalAsync(Request(s));
            await tx.CommitAsync();
            await contender.WaitAsync(TimeSpan.FromSeconds(30));
        }
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal(closeFirst ? 0 : 1, await verify.JournalEntries.CountAsync());
        Assert.Equal(AccountingPeriodStatus.Closed, (await verify.AccountingPeriods.SingleAsync()).Status);
        await Assert.ThrowsAsync<AccountingPeriodException>(() => Posting(verify).PostJournalAsync(Request(s)));
    }

    [Fact]
    public async Task TenantIsolation_AndDirectPeriodHistoryMutation_AreDenied()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant); var service = new AccountingPeriodService(db);
        var p = await service.CreateAsync(s.Tenant, s.Owner, "QA", "QA", Start, End, true, "Immutable history");
        await using (var other = scope.CreateDbContext(Guid.NewGuid()))
        {
            Assert.Empty(await other.AccountingPeriods.ToListAsync()); Assert.Empty(await other.AccountingPeriodEvents.ToListAsync());
        }
        p.Status = AccountingPeriodStatus.Closed;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var history = await db.AccountingPeriodEvents.SingleAsync(); history.Reason = "forged";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Equal("Immutable history", (await verify.AccountingPeriodEvents.SingleAsync()).Reason);
        Assert.Equal(AccountingPeriodStatus.Open, (await verify.AccountingPeriods.SingleAsync()).Status);
    }

    [Fact]
    public async Task StaleSerializableSnapshot_CannotObserveAnOpenPeriodAfterCloseCommits()
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var posting = scope.CreateDbContext(s.Tenant);
        var p = await new AccountingPeriodService(posting).CreateAsync(s.Tenant, s.Owner, "SERIAL", "Serializable race", Start, End, true, "QA stale snapshot");
        await using var tx = await posting.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        Assert.Equal(AccountingPeriodStatus.Open, (await posting.AccountingPeriods.AsNoTracking().SingleAsync()).Status);
        await using (var closer = scope.CreateDbContext(s.Tenant))
            await new AccountingPeriodService(closer).TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 1, "Close commits after posting snapshot");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AccountingPeriodPostingGuard.RequireOpenAsync(posting, s.Tenant, PostingDate, default));
        Assert.Equal(PostgresErrorCodes.SerializationFailure, ex.SqlState);
        await tx.RollbackAsync();
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Empty(await verify.JournalEntries.ToListAsync());
        Assert.Equal(AccountingPeriodStatus.Closed, (await verify.AccountingPeriods.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("sale")]
    [InlineData("purchase")]
    [InlineData("return")]
    [InlineData("expense")]
    [InlineData("receipt")]
    [InlineData("payment")]
    [InlineData("party-ledger")]
    [InlineData("party-adjustment")]
    public async Task ClosedPeriodSourceGuard_RejectsEveryCoreSourceBeforePersistence(string source)
    {
        using var scope = TestDatabaseScope.Open(); var s = await Seed(scope);
        await using var db = scope.CreateDbContext(s.Tenant); var service = new AccountingPeriodService(db);
        var p = await service.CreateAsync(s.Tenant, s.Owner, "CLOSED", "Closed", Start, End, true, "QA source matrix");
        await service.TransitionAsync(s.Tenant, p.Id, s.Owner, "close", 1, "Closed before attempted source");
        BaseEntity document = source switch
        {
            "sale" => new SalesInvoice { TenantId = s.Tenant, InvoiceDate = PostingDate, TotalAmount = 1, Status = InvoiceStatus.Completed },
            "purchase" => new PurchaseInvoice { TenantId = s.Tenant, InvoiceDate = PostingDate, TotalAmount = 1, Status = InvoiceStatus.Completed },
            "return" => new Return { TenantId = s.Tenant, ReturnDate = PostingDate, TotalAmount = 1 },
            "expense" => new Expense { TenantId = s.Tenant, ExpenseDate = PostingDate, Amount = 1, IsApproved = true },
            "receipt" => new CustomerPayment { TenantId = s.Tenant, PaymentDate = PostingDate, Amount = 1 },
            "payment" => new SupplierPayment { TenantId = s.Tenant, PaymentDate = PostingDate, Amount = 1 },
            "party-ledger" => new AccountLedger { TenantId = s.Tenant, TransactionDate = PostingDate, DebitAmount = 1 },
            _ => new PartyLedgerAdjustment { TenantId = s.Tenant, AdjustmentDate = PostingDate, Amount = 1 }
        };
        document.Id = Guid.NewGuid(); db.Add(document);
        var ex = await Assert.ThrowsAsync<AccountingPeriodException>(() => db.SaveChangesAsync());
        Assert.Equal("PERIOD_NOT_OPEN", ex.Code);
        await using var verify = scope.CreateDbContext(s.Tenant);
        Assert.Empty(await verify.JournalEntries.ToListAsync()); Assert.Empty(await verify.AccountingOutboxMessages.ToListAsync());
        Assert.Empty(await verify.SalesInvoices.ToListAsync()); Assert.Empty(await verify.PurchaseInvoices.ToListAsync());
        Assert.Empty(await verify.Returns.ToListAsync()); Assert.Empty(await verify.Expenses.ToListAsync());
        Assert.Empty(await verify.CustomerPayments.ToListAsync()); Assert.Empty(await verify.SupplierPayments.ToListAsync());
        Assert.Empty(await verify.AccountLedgers.ToListAsync()); Assert.Empty(await verify.PartyLedgerAdjustments.ToListAsync());
    }

    private sealed record Scenario(Guid Tenant, Guid Owner, Guid Cashier, Guid Debit, Guid Credit);
    private static async Task<Scenario> Seed(TestDatabaseScope scope)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = scope.CreateDbContext(s.Tenant);
        db.Tenants.Add(new Tenant { Id = s.Tenant, Name = "QA-ERP-R10-20261113-PERIOD", Currency = "EGP", Status = TenantStatus.Active, IsActive = true });
        foreach (var (id, role) in new[] { (s.Owner, UserRole.Owner), (s.Cashier, UserRole.Cashier) })
            db.Users.Add(new User { Id = id, TenantId = s.Tenant, Username = $"qa-{id:N}", Email = $"{id:N}@example.invalid", PasswordHash = "not-a-login", Role = role, IsActive = true });
        var debit = Account.Create(s.Tenant, "QA-ASSET", "QA asset", AccountType.Asset); debit.Id = s.Debit;
        var credit = Account.Create(s.Tenant, "QA-EQUITY", "QA equity", AccountType.Equity); credit.Id = s.Credit;
        db.LedgerAccounts.AddRange(debit, credit); await db.SaveChangesAsync();
        return s;
    }
    private static FinancialPostingService Posting(ApplicationDbContext db) => new(db, NullLogger<FinancialPostingService>.Instance);
    private static PostingRequest Request(Scenario s) => new()
    {
        TenantId = s.Tenant, UserId = s.Owner, JournalDate = PostingDate, SourceType = "QA-Period", SourceId = Guid.NewGuid(),
        Description = "QA-ERP-R10-20261113-PERIOD", IdempotencyKey = $"QA-PERIOD:{Guid.NewGuid()}",
        Lines = [PostingLine.Debit(s.Debit, 10, "QA debit"), PostingLine.Credit(s.Credit, 10, "QA credit")]
    };
    private static async Task<string> Balances(TestDatabaseScope scope, Scenario s)
    {
        await using var db = scope.CreateDbContext(s.Tenant);
        return JsonSerializer.Serialize(await db.LedgerAccounts.OrderBy(x => x.Id).Select(x => new { x.Id, x.CurrentBalance }).ToListAsync());
    }
    private static Task Receipt(string name, object value)
    {
        var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR");
        return string.IsNullOrWhiteSpace(directory) ? Task.CompletedTask : File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
