using System.Security;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using MongoDB.Driver;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Consumers.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.Data;
using ReturnMigration = PointOfSale.Infrastructure.Migrations.AffiliateManualPayoutReturn;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Infrastructure.Treasury;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Only the network-disabled disposable PostgreSQL fixture is accepted.
/// Starts at approved commission; this is NOT checkout/fulfillment E2E.</summary>
public sealed class AffiliateManualPayoutReturnTests : IClassFixture<AffiliateReturnDatabase>
{
    private readonly AffiliateReturnDatabase database;
    public AffiliateManualPayoutReturnTests(AffiliateReturnDatabase database) => this.database = database;

    [Fact]
    public async Task ProductionSchemaAdmission_InstallerAdvancesMetadata_StartupStaysReadOnly()
    {
        await using var db = database.Open(Guid.NewGuid());
        var metadata = new SystemSchemaVersion { Id = Guid.NewGuid(), Component = "postgres",
            Version = "20261113140000_FinancialVoucherAuthority", AppliedAt = DateTime.UtcNow };
        db.Add(metadata); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var service = new PointOfSale.Infrastructure.Operations.SystemSchemaVersionService(db,
            new PointOfSale.Infrastructure.Configuration.RuntimeAdmissionPolicy(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync("postgres", "20261115112011_AffiliateManualPayoutReturn"));
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow("20261115112011_AffiliateManualPayoutReturn", "10.0.15")));
        var installer = await File.ReadAllTextAsync("/src/deploy/affiliate/07-manual-payout-return-schema-version.sql");
        installer = string.Join('\n', installer.Split('\n').Where(line => !line.StartsWith("\\set")));
        await db.Database.ExecuteSqlRawAsync(installer);
        var installed = await db.Set<SystemSchemaVersion>().IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == metadata.Id);
        Assert.Equal("20261115112011_AffiliateManualPayoutReturn", installed.Version);
        await service.RecordAsync("postgres", installed.Version);
        await db.Database.ExecuteSqlRawAsync(installer); // Exact install replay cannot rewrite operational timestamps.
        var replay = await db.Set<SystemSchemaVersion>().IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == metadata.Id);
        Assert.Equal(installed.UpdatedAt, replay.UpdatedAt); Assert.Equal(installed.AppliedAt, replay.AppliedAt);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task SqlAndMongoPaidHistory_ConvergesOnReturn_DelayedPaid_Replay_AndRepayment()
    {
        await using var f = await Scenario.Create(database); await f.Pay();
        var mongo = database.Mongo;
        var orders = mongo.GetCollection<AffiliateOrderDocument>("affiliate_orders");
        Guid orderId;
        await using (var db = f.Open())
        {
            var earned = await db.AffiliateCommissionLedger.IgnoreQueryFilters().SingleAsync(e => e.AffiliateAccountId == f.Affiliate.Id && e.EntryType == CommissionEntryType.Earned);
            orderId = earned.OrderId;
            await orders.InsertOneAsync(new() { Id = Guid.NewGuid(), OrderId = orderId, TenantId = f.Store, AffiliateAccountId = f.Affiliate.Id,
                Currency = "EGP", TotalApproved = 150m, TotalCommission = 150m, Status = "Approved", UpdatedAtUtc = DateTime.UtcNow,
                Items = [new() { OrderItemId = earned.OrderItemId, CommissionAmount = 150m, SettlementStatus = "Approved" }] });
        }
        var paid = new AffiliateCommissionPaidEvent { TenantId = f.Store, StoreTenantId = f.Store, OrderId = orderId,
            AffiliateAccountId = f.Affiliate.Id, SettlementReferenceId = f.Request.Id, TotalPaid = 150m, ItemCount = 1, Currency = "EGP", EventVersion = 1 };
        async Task ConsumePaid()
        {
            await using var db = f.Open();
            var context = new Mock<ConsumeContext<AffiliateCommissionPaidEvent>>(); context.SetupGet(c => c.Message).Returns(paid);
            await new AffiliateCommissionPaidProjectionConsumer(mongo, NullLogger<AffiliateCommissionPaidProjectionConsumer>.Instance, db).Consume(context.Object);
        }
        async Task AssertProjection(decimal amount, string status)
        {
            var order = await orders.Find(o => o.OrderId == orderId && o.TenantId == f.Store).SingleAsync();
            Assert.Equal(amount, order.TotalPaid); Assert.Equal(status, order.Status); Assert.Equal(status, Assert.Single(order.Items).SettlementStatus);
            Assert.Equal(amount, (await mongo.GetCollection<AffiliateDashboardDocument>("affiliate_dashboards")
                .Find(d => d.TenantId == f.Store && d.AffiliateAccountId == f.Affiliate.Id).SingleAsync()).TotalPaid);
            Assert.Equal(amount, (await mongo.GetCollection<AffiliateEarningsSummaryDocument>("affiliate_earnings_summaries")
                .Find(d => d.TenantId == f.Store && d.AffiliateAccountId == f.Affiliate.Id).SingleAsync()).TotalPaid);
        }
        await ConsumePaid(); await AssertProjection(150m, "Paid");
        var command = await f.Review(); ManualPayoutReturnResult returned;
        await using (var db = f.Open()) returned = await f.Orchestrator(db).ReturnAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        var fact = new AffiliateManualPayoutReturnedEvent { TenantId = f.Store, StoreTenantId = f.Store, OrderId = orderId,
            AffiliateAccountId = f.Affiliate.Id, ReturnId = returned.ReturnId, EventVersion = 2 };
        async Task ConsumeReturn()
        {
            await using var db = f.Open();
            var context = new Mock<ConsumeContext<AffiliateManualPayoutReturnedEvent>>(); context.SetupGet(c => c.Message).Returns(fact);
            await new AffiliateCommissionPaidProjectionConsumer(mongo, NullLogger<AffiliateCommissionPaidProjectionConsumer>.Instance, db).Consume(context.Object);
        }
        await ConsumeReturn(); await AssertProjection(0m, "Approved");
        await ConsumePaid(); await ConsumeReturn(); await AssertProjection(0m, "Approved");
        await using (var db = f.Open())
        {
            var second = await Scenario.Payouts(db).RequestPayoutAsync(f.Affiliate.Id, 150m, f.Store);
            await Scenario.Payouts(db).ApprovePayoutAsync(second.Id, f.Store, f.Actor, "LOCAL second approval"); db.ChangeTracker.Clear();
            await f.Orchestrator(db).ExecuteAdminManualPayoutAsync(second.Id, f.Store, f.Actor, await f.PayCommand(db, second));
        }
        // An old returned-cash or Paid envelope cannot overwrite a later re-payment.
        await ConsumeReturn(); await ConsumePaid(); await AssertProjection(150m, "Paid");
    }

    [Fact]
    public async Task Paid_Return_Replay_NewWithdrawalAndRepayment_PreservesHistoryAndBalances()
    {
        await using var f = await Scenario.Create(database);
        await f.Pay();
        var command = await f.Review();
        ManualPayoutReturnResult result;
        await using (var db = f.Open()) result = await f.Orchestrator(db).ReturnAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        Assert.False(result.WasIdempotentSkip);
        await f.AssertReturned();
        await using (var db = f.Open())
        {
            var replay = await f.Orchestrator(db).ReturnAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
            Assert.True(replay.WasIdempotentSkip); Assert.Equal(result.ReturnId, replay.ReturnId);
            Assert.Equal(result.RecordedAtUtc, replay.RecordedAtUtc);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(db).ReturnAdminManualPayoutAsync(
                f.Request.Id, f.Store, f.Actor, command with { ReceiptReference = "DIFFERENT-RECEIPT" }));
        }
        await using (var db = f.Open())
        {
            var service = Scenario.Payouts(db);
            var second = await service.RequestPayoutAsync(f.Affiliate.Id, 150m, f.Store);
            await service.ApprovePayoutAsync(second.Id, f.Store, f.Actor, "Isolated re-payment approval");
            db.ChangeTracker.Clear();
            await f.Orchestrator(db).ExecuteAdminManualPayoutAsync(second.Id, f.Store, f.Actor, await f.PayCommand(db, second));
        }
        await using (var db = f.Open())
        {
            Assert.Equal(350m, await f.Cash(db));
            var balance = await Scenario.Payouts(db).GetAvailableBalanceAsync(f.Affiliate.Id, f.Store);
            Assert.Equal(0m, balance.RawAvailable); Assert.Equal(150m, balance.TotalPaid);
            Assert.Equal(2, await db.AffiliateCommissionLedger.IgnoreQueryFilters().CountAsync(e => e.AffiliateAccountId == f.Affiliate.Id && e.EntryType == CommissionEntryType.Paid));
            Assert.Single(await AffiliatePayoutReturnAuthority.EffectiveHistory(db).Where(e => e.AffiliateAccountId == f.Affiliate.Id && e.EntryType == CommissionEntryType.Paid).ToListAsync());
            Assert.Equal(2, await db.AffiliateBalanceLedger.IgnoreQueryFilters().CountAsync(e => e.AffiliateAccountId == f.Affiliate.Id && e.EntryType == BalanceEntryType.Withdrawal));
            Assert.Equal(PayoutRequestStatus.Returned, (await db.AffiliatePayoutRequests.SingleAsync(r => r.Id == f.Request.Id)).Status);
        }
    }

    [Theory]
    [InlineData("attestation")][InlineData("review")][InlineData("actor")][InlineData("tenant")]
    [InlineData("inactive")][InlineData("deleted")][InlineData("manager")][InlineData("source")]
    public async Task InvalidAuthority_DoesNotChangePaidFinancialState(string fault)
    {
        await using var f = await Scenario.Create(database); await f.Pay();
        var command = await f.Review();
        if (fault is "inactive" or "deleted" or "manager" or "source")
        {
            await using var changed = f.Open();
            if (fault == "source")
                (await changed.Cashboxes.IgnoreQueryFilters().SingleAsync(c => c.Id == f.Cashbox)).IsActive = false;
            else
            {
                var user = await changed.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == f.Actor);
                if (fault == "inactive") user.IsActive = false;
                if (fault == "deleted") user.IsDeleted = true;
                if (fault == "manager") user.Role = UserRole.Manager;
            }
            await changed.SaveChangesAsync();
        }
        await using var db = f.Open();
        var actor = fault == "actor" ? Guid.NewGuid() : f.Actor;
        var store = fault == "tenant" ? Guid.NewGuid() : f.Store;
        if (fault == "attestation") command = command with { ReceivedManually = false };
        if (fault == "review") command = command with { ReviewFingerprint = new string('0', 64) };
        var error = await Record.ExceptionAsync(() => f.Orchestrator(db).ReturnAdminManualPayoutAsync(f.Request.Id, store, actor, command));
        if (fault is "actor" or "tenant" or "inactive" or "deleted" or "manager") Assert.IsType<SecurityException>(error);
        else
        {
            var invalid = Assert.IsType<InvalidOperationException>(error);
            var code = fault switch { "attestation" => "AFFILIATE_PAYOUT_RETURN_ATTESTATION_REQUIRED", "review" => "AFFILIATE_MANUAL_REQUEST_CHANGED",
                "source" => "AFFILIATE_PAYOUT_RETURN_AUTHORITY_CONFLICT", _ => throw new InvalidOperationException("Unknown local test fault") };
            Assert.StartsWith(code, invalid.Message);
        }
        await f.AssertStillPaid();
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task PostingOrEventFailure_RollsBackEntireReturn(bool eventFailure)
    {
        await using var f = await Scenario.Create(database); await f.Pay();
        var command = await f.Review();
        await using var db = f.Open();
        var posting = new Mock<IFinancialPostingService>(MockBehavior.Strict);
        posting.Setup(p => p.PostJournalAsync(It.IsAny<PostingRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ISOLATED_INJECTED_POSTING_FAILURE"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(db, eventFailure,
            eventFailure ? null : posting.Object).ReturnAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command));
        await f.AssertStillPaid();
    }

    [Fact]
    public async Task ConcurrentReturn_ExactlyOneReceipt_IdenticalReplay()
    {
        await using var f = await Scenario.Create(database); await f.Pay();
        var command = await f.Review();
        async Task<ManualPayoutReturnResult> Execute()
        {
            await using var db = f.Open();
            return await f.Orchestrator(db).ReturnAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        }
        var results = await Task.WhenAll(Execute(), Execute());
        Assert.Single(results.Where(r => !r.WasIdempotentSkip)); Assert.Single(results.Where(r => r.WasIdempotentSkip));
        Assert.Equal(results[0].ReturnId, results[1].ReturnId); await f.AssertReturned();
    }

    [Fact]
    public async Task CentralPosting_RejectsInventedReceipt_AndSubstitutedDestination()
    {
        await using var f = await Scenario.Create(database); await f.Pay();
        await using (var db = f.Open())
        {
            var payable = await db.LedgerAccounts.IgnoreQueryFilters().SingleAsync(a => a.TenantId == f.Store && a.AccountCode == AccountCodes.AffiliatePayable);
            var invented = new PostingRequest { TenantId = f.Store, SourceType = AffiliatePayoutReturnAuthority.SourceType,
                SourceId = Guid.NewGuid(), JournalDate = DateTime.UtcNow.Date, UserId = f.Actor, Description = "LOCAL denied invented receipt",
                IdempotencyKey = "LOCAL-NOT-RETURN-AUTHORITY", Lines = [PostingLine.Debit(f.CashAccount, 150m), PostingLine.Credit(payable.Id, 150m)] };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance).PostJournalAsync(invented));
        }
        await f.AssertStillPaid();
        var command = await f.Review(); ManualPayoutReturnResult returned;
        await using (var db = f.Open()) returned = await f.Orchestrator(db).ReturnAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        await using (var db = f.Open())
        {
            var expected = await AffiliatePayoutReturnAuthority.BuildPostingAsync(db, f.Store, returned.ReturnId, default);
            var expense = await db.LedgerAccounts.IgnoreQueryFilters().SingleAsync(a => a.TenantId == f.Store && a.AccountCode == AccountCodes.AffiliateMarketingExpense);
            expected.IdempotencyKey += ":FORGED";
            expected.Lines[0] = PostingLine.Debit(expense.Id, 150m); // Non-cash substitution must not bypass validation.
            await Assert.ThrowsAsync<PointOfSale.Core.Exceptions.Accounting.InvalidJournalException>(() =>
                new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance).PostJournalAsync(expected));
        }
        await f.AssertReturned();
    }

    [Fact]
    public async Task ArbitraryPositiveAdjustment_DoesNotRestoreWithdrawalAuthority()
    {
        await using var f = await Scenario.Create(database); await f.Pay();
        await using var db = f.Open();
        db.AffiliateBalanceLedger.Add(AffiliateBalanceLedger.CreateAdjustment(f.Store, f.Affiliate.Id, 150m, "EGP", "Isolated non-authoritative adjustment", Guid.NewGuid()));
        await db.SaveChangesAsync();
        Assert.Equal(0m, (await Scenario.Payouts(db).GetAvailableBalanceAsync(f.Affiliate.Id, f.Store)).RawAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Scenario.Payouts(db).RequestPayoutAsync(f.Affiliate.Id, 150m, f.Store));
    }

    [Fact]
    public async Task ReturnHistory_IsAppendOnly_InEfAndDatabase_IncludingTruncate()
    {
        await using var f = await Scenario.Create(database); await f.Pay(); var command = await f.Review();
        await using (var db = f.Open()) await f.Orchestrator(db).ReturnAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        await using (var db = f.Open())
        {
            var receipt = await db.AffiliateManualPayoutReturns.SingleAsync(r => r.RequestId == f.Request.Id);
            db.Entry(receipt).Property(r => r.Reason).CurrentValue = "Forbidden rewrite";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = f.Open())
        {
            foreach (var table in new[] { "AffiliateManualPayoutReturns", "AffiliateManualPayoutReturnAllocations" })
            {
                // SQL mutation attempts are negative tests only, within a guarded disposable database.
                foreach (var sql in new[] { $"UPDATE \"{table}\" SET \"Amount\" = \"Amount\"", $"DELETE FROM \"{table}\"", $"TRUNCATE TABLE \"{table}\" CASCADE" })
                    await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            }
        }
        await f.AssertReturned();
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly AffiliateReturnDatabase database;
        internal Guid Store = Guid.NewGuid(), Actor = Guid.NewGuid(), Cashbox = Guid.NewGuid(), CashAccount;
        internal AffiliateAccount Affiliate = null!;
        internal AffiliatePayoutRequest Request = null!;
        private readonly ServiceProvider services;
        private Scenario(AffiliateReturnDatabase database)
        {
            this.database = database;
            var collection = new ServiceCollection();
            collection.AddScoped<ITenantContext>(_ => { var t = new TenantContext(); t.SetTenantId(Store); return t; });
            collection.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(database.Connection));
            collection.AddScoped<IAffiliatePayoutService>(s => Payouts(s.GetRequiredService<ApplicationDbContext>()));
            collection.AddScoped<IFinancialPostingService>(s => Posting(s.GetRequiredService<ApplicationDbContext>()));
            collection.AddScoped<IAccountResolver>(s => new AccountResolver(s.GetRequiredService<ApplicationDbContext>(), NullLogger<AccountResolver>.Instance));
            collection.AddScoped<ICashboxLedgerAccountService>(s => new CashboxLedgerAccountService(s.GetRequiredService<ApplicationDbContext>(), NullLogger<CashboxLedgerAccountService>.Instance));
            collection.AddScoped<IEventPublisher>(s => Publisher(s.GetRequiredService<ApplicationDbContext>(), false));
            services = collection.BuildServiceProvider();
        }
        internal ApplicationDbContext Open() => database.Open(Store);
        internal static AffiliatePayoutService Payouts(ApplicationDbContext db) => new(db, NullLogger<AffiliatePayoutService>.Instance);
        private static FinancialPostingService Posting(ApplicationDbContext db) => new(db, NullLogger<FinancialPostingService>.Instance);
        private static IEventPublisher Publisher(ApplicationDbContext db, bool fail)
        {
            var events = new Mock<IEventPublisher>();
            events.Setup(e => e.PublishAsync(It.IsAny<AffiliateManualPayoutReturnedEvent>(), It.IsAny<CancellationToken>()))
                .Returns<AffiliateManualPayoutReturnedEvent, CancellationToken>(async (e, ct) => {
                    Assert.NotNull(db.Database.CurrentTransaction);
                    var tenant = new TenantContext(); tenant.SetTenantId(e.StoreTenantId);
                    var native = new PointOfSale.Infrastructure.EventBus.MassTransitEventPublisher(db,
                        Mock.Of<IBus>(MockBehavior.Strict), Mock.Of<IPublishEndpoint>(MockBehavior.Strict),
                        NullLogger<PointOfSale.Infrastructure.EventBus.MassTransitEventPublisher>.Instance, tenant);
                    await native.PublishAsync(e, ct);
                    if (fail) throw new InvalidOperationException("ISOLATED_INJECTED_OUTBOX_FAILURE");
                });
            return events.Object;
        }
        internal AffiliateSettlementOrchestrator Orchestrator(ApplicationDbContext db, bool fail = false, IFinancialPostingService? posting = null) => new(
            db, Payouts(db), Publisher(db, fail), posting ?? Posting(db), new AccountResolver(db, NullLogger<AccountResolver>.Instance),
            new CashboxLedgerAccountService(db, NullLogger<CashboxLedgerAccountService>.Instance), NullLogger<AffiliateSettlementOrchestrator>.Instance,
            Options.Create(new AffiliateSettlementOptions()), services.GetRequiredService<IServiceScopeFactory>());
        internal static async Task<Scenario> Create(AffiliateReturnDatabase database)
        {
            var f = new Scenario(database); await using var db = f.Open();
            f.Affiliate = AffiliateAccount.Create(Guid.Empty, Guid.NewGuid(), $"LOCAL{Guid.NewGuid():N}"[..12], "ISOLATED NO CONTACT");
            f.Affiliate.Id = Guid.NewGuid(); f.Affiliate.Activate(); f.Affiliate.UpdatePayoutWallet("01000000000", "VodafoneCash");
            var user = AffiliateUser.Create(Guid.Empty, $"local-{f.Affiliate.Id:N}@example.invalid", "ISOLATED-NONLOGIN-HASH", f.Affiliate.Id);
            user.Id = f.Affiliate.UserId; user.VerifyEmail();
            var email = $"local-{f.Actor:N}@example.invalid";
            db.AddRange(new Tenant { Id = f.Store, Name = "ISOLATED", Slug = $"local-{f.Store:N}", Currency = "EGP", IsActive = true, Status = TenantStatus.Active },
                new User { Id = f.Actor, TenantId = f.Store, Username = email, Email = email, NormalizedEmail = email.ToUpperInvariant(),
                    NormalizedUsername = email.ToUpperInvariant(), PasswordHash = "ISOLATED-NONLOGIN-HASH", Role = UserRole.Owner, IsActive = true,
                    FirstName = "LOCAL", LastName = "ONLY", EmailVerificationStatus = EmailVerificationStatus.Verified }, f.Affiliate, user);
            await db.SaveChangesAsync();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await new AccountingPeriodService(db).CreateAsync(f.Store, f.Actor, "LOCAL-OPEN", "Isolated open period",
                today.AddDays(-1), today.AddDays(1), true, "Disposable financial validation only");
            await new TenantAccountSeeder(db, NullLogger<TenantAccountSeeder>.Instance).SeedAsync(f.Store);
            var cashbox = new Cashbox { Id = f.Cashbox, TenantId = f.Store, Name = "ISOLATED CASH", IsActive = true, OpenedByUserId = f.Actor, OpenedAt = DateTime.UtcNow };
            db.Add(cashbox); await db.SaveChangesAsync();
            var cash = await new CashboxLedgerAccountService(db, NullLogger<CashboxLedgerAccountService>.Instance).EnsureCashboxAccountAsync(cashbox);
            f.CashAccount = cash.AccountId;
            var treasury = await db.TreasuryAccounts.IgnoreQueryFilters().SingleAsync(t => t.AccountId == f.CashAccount);
            await new TreasuryInitialCapitalizationService(db, Posting(db)).CreateAndPostAsync(f.Store, f.Actor,
                new(Guid.NewGuid(), DateTime.UtcNow.Date, [new(treasury.Id, 500m)], "LOCAL CAPITAL", "Disposable simulation; no physical cash",
                    TreasuryInitialCapitalization.DeclarationIdentifier, true));
            var category = new Category { Id = Guid.NewGuid(), TenantId = f.Store, Name = "LOCAL" };
            var product = new Product { Id = Guid.NewGuid(), TenantId = f.Store, CategoryId = category.Id, Name = "LOCAL" };
            var policyCategory = AffiliateCategory.CreateMainCategory("LOCAL", $"local-{Guid.NewGuid():N}", 1); policyCategory.Id = Guid.NewGuid();
            var policy = AffiliateProductPolicy.Create(f.Store, product.Id, .1m, null, policyCategory.Id); policy.Id = Guid.NewGuid();
            var order = new Order { Id = Guid.NewGuid(), TenantId = f.Store, OrderNumber = $"LOCAL-{Guid.NewGuid():N}", Currency = "EGP", Total = 1500m,
                AffiliateAccountId = f.Affiliate.Id, AffiliateReferralCode = f.Affiliate.ReferralCode, Status = OrderStatus.Delivered,
                FulfillmentStatus = FulfillmentStatus.Delivered, PaymentStatus = PaymentStatus.Collected, FinancialPostingStatus = FinancialPostingStatus.NotRequired };
            var item = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = product.Id, Quantity = 1, UnitPrice = 1500m, Total = 1500m,
                PaidAmount = 1500m, AffiliateCommissionEligible = true, AffiliateCommissionPolicyIdSnapshot = policy.Id, AffiliateCommissionRateSnapshot = .1m,
                AffiliateCommissionBaseAmountSnapshot = 1500m, AffiliateCommissionAmountSnapshot = 150m, AffiliateCommissionSnapshotCapturedAtUtc = DateTime.UtcNow };
            order.Items.Add(item);
            var attribution = AffiliateAttribution.Create(f.Store, order.Id, f.Affiliate.Id, f.Affiliate.ReferralCode, AttributionSource.ReferralLink); attribution.Id = Guid.NewGuid();
            var earned = AffiliateCommissionLedger.CreateEarned(f.Store, attribution.Id, f.Affiliate.Id, order.Id, item.Id, 1500m, .1m, null, "EGP");
            db.AddRange(category, product, policyCategory, policy, order, attribution, earned, AffiliateCommissionLedger.CreateApproved(earned)); await db.SaveChangesAsync();
            var accounts = await db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.TenantId == f.Store).ToDictionaryAsync(a => a.AccountCode);
            await Posting(db).PostJournalAsync(new PostingRequest { TenantId = f.Store, JournalDate = DateTime.UtcNow, SourceType = "AffiliateCommissionEarned",
                SourceId = order.Id, Description = "LOCAL approved commission boundary", UserId = f.Actor, IdempotencyKey = $"AFFILIATE_COMM_EARNED:{f.Store}:{order.Id}",
                Lines = [PostingLine.Debit(accounts[AccountCodes.AffiliateMarketingExpense].Id, 150m), PostingLine.Credit(accounts[AccountCodes.AffiliatePayable].Id, 150m)] });
            f.Request = await Payouts(db).RequestPayoutAsync(f.Affiliate.Id, 150m, f.Store);
            await Payouts(db).ApprovePayoutAsync(f.Request.Id, f.Store, f.Actor, "LOCAL approval"); return f;
        }
        internal async Task<AdminManualPayoutCommand> PayCommand(ApplicationDbContext db, AffiliatePayoutRequest request)
        {
            var source = await AffiliateManualCashSource.ResolveAsync(db, Store, Cashbox, default);
            return new(Cashbox, 150m, "EGP", AffiliateSettlementOrchestrator.DestinationFingerprint(request), "Cash", true,
                AffiliateManualPayout.AttestationText, "LOCAL ONLY", "No external transfer", AffiliateManualCashSource.Fingerprint(Cashbox, source!));
        }
        internal async Task Pay()
        {
            await using var db = Open();
            await Orchestrator(db).ExecuteAdminManualPayoutAsync(Request.Id, Store, Actor, await PayCommand(db, Request));
        }
        internal async Task<AdminManualPayoutReturnCommand> Review()
        {
            await using var db = Open();
            var payout = await db.AffiliateManualPayouts.SingleAsync(m => m.RequestId == Request.Id);
            var journal = await db.JournalEntries.IgnoreQueryFilters().SingleAsync(j => j.SourceId == Request.Id);
            var source = await AffiliateManualCashSource.ResolveAsync(db, Store, Cashbox, default);
            return new(AffiliateSettlementOrchestrator.ReturnReviewFingerprint(payout, journal.Id, AffiliateManualCashSource.Fingerprint(Cashbox, source!)),
                true, AffiliateManualPayoutReturn.AttestationText, "LOCAL-RETURN-RECEIPT", "Local full cash return");
        }
        internal Task<decimal> Cash(ApplicationDbContext db) => db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.Id == CashAccount).Select(a => a.CurrentBalance).SingleAsync();
        internal async Task AssertStillPaid()
        {
            await using var db = Open(); Assert.Equal(350m, await Cash(db));
            Assert.False(await db.AffiliateManualPayoutReturns.AnyAsync(r => r.RequestId == Request.Id));
            Assert.Equal(PayoutRequestStatus.Paid, (await db.AffiliatePayoutRequests.SingleAsync(r => r.Id == Request.Id)).Status);
            Assert.Equal(0m, (await Payouts(db).GetAvailableBalanceAsync(Affiliate.Id, Store)).RawAvailable);
            Assert.False(await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == Store && a.ActionType == "AffiliateManualPayoutReturned"));
            Assert.False(await db.OutboxMessages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == Store));
        }
        internal async Task AssertReturned()
        {
            await using var db = Open(); Assert.Equal(500m, await Cash(db));
            Assert.Single(await db.AffiliateManualPayoutReturns.Where(r => r.RequestId == Request.Id).ToListAsync());
            Assert.Equal(PayoutRequestStatus.Returned, (await db.AffiliatePayoutRequests.SingleAsync(r => r.Id == Request.Id)).Status);
            var balance = await Payouts(db).GetAvailableBalanceAsync(Affiliate.Id, Store);
            Assert.Equal(150m, balance.RawAvailable); Assert.Equal(0m, balance.TotalPaid);
            var receipt = await db.AffiliateManualPayoutReturns.SingleAsync(r => r.RequestId == Request.Id);
            var journal = await db.JournalEntries.IgnoreQueryFilters().Include(j => j.LedgerEntries).SingleAsync(j => j.SourceId == receipt.Id);
            Assert.Equal(150m, journal.LedgerEntries.Sum(l => l.DebitAmount)); Assert.Equal(150m, journal.LedgerEntries.Sum(l => l.CreditAmount));
            Assert.Single(await db.AffiliateManualPayouts.Where(m => m.RequestId == Request.Id).ToListAsync());
            var envelope = Assert.Single(await db.OutboxMessages.IgnoreQueryFilters().Where(m => m.TenantId == Store).ToListAsync());
            Assert.Contains(nameof(AffiliateManualPayoutReturnedEvent), envelope.EventType);
            Assert.True(envelope.EventVersion > 0); Assert.NotNull(envelope.AggregateId);
            Assert.Single(await db.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.AffiliateAccountId == Affiliate.Id && e.EntryType == CommissionEntryType.Paid).ToListAsync());
        }
        public ValueTask DisposeAsync() => services.DisposeAsync();
    }
}

public sealed class AffiliateManualPayoutReturnContractTests
{
    [Theory]
    [InlineData("Review", "GET")][InlineData("Execute", "POST")]
    public void Endpoints_RequireExistingOwnerAdminPayoutPermission(string action, string method)
    {
        var controller = typeof(PointOfSale.API.Controllers.AffiliateManualPayoutReturnController);
        var authorization = Assert.Single(controller.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        Assert.Equal("Owner,Admin", authorization.Roles);
        Assert.True(PointOfSale.API.Security.PosEndpointPermissionCatalog.TryResolve("AffiliateManualPayoutReturn", action, [method], out var permission));
        Assert.Equal(PointOfSale.Core.Security.PosPermissions.AffiliatesPayoutManage, permission);
        Assert.True(PointOfSale.Core.Security.PosRolePermissionCatalog.IsGranted(UserRole.Owner, permission));
        Assert.True(PointOfSale.Core.Security.PosRolePermissionCatalog.IsGranted(UserRole.Admin, permission));
        Assert.False(PointOfSale.Core.Security.PosRolePermissionCatalog.IsGranted(UserRole.Manager, permission));
        Assert.False(PointOfSale.Core.Security.PosRolePermissionCatalog.IsGranted(UserRole.Cashier, permission));
    }

    [Fact]
    public async Task Execute_RejectsAnonymousActor_WithoutCallingFinancialService()
    {
        var service = new Mock<IAffiliateSettlementOrchestrator>(MockBehavior.Strict);
        var controller = new PointOfSale.API.Controllers.AffiliateManualPayoutReturnController(null!,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(Guid.NewGuid()), service.Object) {
            ControllerContext = new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedResult>(await controller.Execute(Guid.NewGuid(),
            new(new string('A', 64), true, AffiliateManualPayoutReturn.AttestationText, "LOCAL", "LOCAL"), default));
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Review_DoesNotDiscloseForeignPayout()
    {
        var store = Guid.NewGuid();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new PointOfSale.Tests.Infrastructure.TestTenantContext(store));
        var requestId = Guid.NewGuid();
        db.AffiliateManualPayouts.Add(new AffiliateManualPayout { Id = Guid.NewGuid(), RequestId = requestId, StoreTenantId = Guid.NewGuid(), Amount = 150m });
        await db.SaveChangesAsync();
        var service = new Mock<IAffiliateSettlementOrchestrator>(MockBehavior.Strict);
        var controller = new PointOfSale.API.Controllers.AffiliateManualPayoutReturnController(db,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(store), service.Object);
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(await controller.Review(requestId, default));
        service.VerifyNoOtherCalls(); Assert.False(db.ChangeTracker.HasChanges());
    }
}

public sealed class AffiliateReturnDatabase : IAsyncLifetime
{
    internal string Connection { get; private set; } = "";
    internal IMongoDatabase Mongo { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        Assert.Equal("network-none-local-socket", Environment.GetEnvironmentVariable("AFFILIATE_RETURN_ISOLATION"));
        Connection = Environment.GetEnvironmentVariable("AFFILIATE_RETURN_DB") ?? throw new InvalidOperationException("Disposable fixture required");
        var cs = new NpgsqlConnectionStringBuilder(Connection);
        Assert.Equal("/tmp", cs.Host); Assert.Equal("affiliate_return_fixture", cs.Database); Assert.Equal("affiliate_fixture", cs.Username);
        Assert.False(File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/token"));
        Mongo = new MongoClient("mongodb://127.0.0.1:27017/affiliate_return_fixture?directConnection=true&serverSelectionTimeoutMS=15000")
            .GetDatabase("affiliate_return_fixture");
        await using var db = Open(Guid.NewGuid());
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.EnsureCreatedAsync();
        // Rehearse actual additive migration on empty fixture tables, not business data.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"AffiliateManualPayoutReturnAllocations\"; DROP TABLE \"AffiliateManualPayoutReturns\";");
        var migration = new ReturnMigration { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, db.Model);
        foreach (var command in commands) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Throws<NotSupportedException>(() => { _ = migration.DownOperations; });
        if (!await db.Currencies.AnyAsync(c => c.Code == "EGP"))
        {
            db.Currencies.Add(new Currency { Code = "EGP", Name = "Egyptian Pound", Symbol = "EGP", DecimalPlaces = 2 });
            await db.SaveChangesAsync();
        }
    }
    internal ApplicationDbContext Open(Guid store)
    {
        var tenant = new TenantContext(); tenant.SetTenantId(store);
        return new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(Connection).Options, tenant);
    }
    public Task DisposeAsync() => Task.CompletedTask; // Container owns disposal; no application data cleanup.
}
