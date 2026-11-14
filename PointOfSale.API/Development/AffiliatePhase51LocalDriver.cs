#if DEBUG
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.Services.Affiliate;
using BusMessage = MassTransit.EntityFrameworkCoreIntegration.OutboxMessage;

namespace PointOfSale.API.Development;

/// <summary>Local, file-commanded P5.1 driver INSIDE the normal Development runtime.
/// Fixed task-owned store/accounts only. No public route, provider, transfer, or transport adapter.
/// Existing IEventPublisher may emit only the orchestrator's paid fact to its normal projection.
/// Delete the opt-in marker to disable; never deletes/reclassifies financial history.</summary>
public sealed class AffiliatePhase51LocalDriver(IServiceScopeFactory scopes, IWebHostEnvironment environment,
    ILogger<AffiliatePhase51LocalDriver> logger) : BackgroundService
{
    public const string Label = "P5.1_TEST_NO_REAL_MONEY";
    public static readonly Guid Store = Guid.Parse("51510000-2026-4022-a000-000000000001");
    public static readonly Guid Admin = Guid.Parse("51510000-2026-4022-a000-000000000002");
    private static readonly string[] Cases = ["ui", "success", "rollback", "underfunded", "synthetic", "lineage"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string DirectoryPath => Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".ai", "evidence", "affiliate-phase51-20261022"));
    private static Guid AccountId(string name) => Guid.Parse($"51510000-2026-4022-a100-{Array.IndexOf(Cases, name) + 1:000000000000}");
    private static Guid UserId(string name) => Guid.Parse($"51510000-2026-4022-a200-{Array.IndexOf(Cases, name) + 1:000000000000}");
    private static string Password => "P51NoRealMoney!2026"; // Dedicated example.invalid QA identities only.

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Development only");
        await Task.Yield();
        await using var sentinel = scopes.CreateAsyncScope();
        var sentinelDb = sentinel.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var identity = await Identity(sentinelDb, stoppingToken);
        var config = sentinel.ServiceProvider.GetRequiredService<IConfiguration>();
        var configuredOptions = sentinel.ServiceProvider.GetRequiredService<IOptions<AffiliateSettlementOptions>>().Value;
        await Write("main-runtime.json", new { identity, apiPid = Environment.ProcessId, store = Store, admin = Admin,
            accounts = Cases.ToDictionary(n => n, AccountId), label = Label, environment = environment.EnvironmentName,
            effective = new { configuredOptions.RequireVerifiedExternalPayoutEvidence, configuredOptions.AllowManualPayoutEvidenceOverride,
                syntheticQaEnabled = config.GetValue<bool>("SyntheticQa:Enabled"), verifierCount = config.GetSection("SyntheticQa:VerifierIds").GetChildren().Count(),
                syntheticSigningKeyPresent = !string.IsNullOrEmpty(config["SyntheticQa:SigningKey"]),
                posting = sentinel.ServiceProvider.GetRequiredService<IFinancialPostingService>().GetType().FullName,
                publisher = sentinel.ServiceProvider.GetRequiredService<IEventPublisher>().GetType().FullName,
                accountResolver = sentinel.ServiceProvider.GetRequiredService<IAccountResolver>().GetType().FullName,
                cashboxResolver = sentinel.ServiceProvider.GetRequiredService<ICashboxLedgerAccountService>().GetType().FullName,
                brokerHost = config["RabbitMQ:Host"] } }, stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested && File.Exists(Path.Combine(DirectoryPath, "runtime.enabled")))
            {
                foreach (var file in Directory.GetFiles(DirectoryPath, "main-*.command.json").Order())
                {
                    var resultName = Path.GetFileName(file).Replace(".command.json", ".result.json");
                    if (File.Exists(Path.Combine(DirectoryPath, resultName))) continue;
                    var command = JsonSerializer.Deserialize<Command>(await File.ReadAllTextAsync(file, stoppingToken), Json)!;
                    try
                    {
                        await RequireIndependentProof(stoppingToken);
                        await using var scope = scopes.CreateAsyncScope();
                        var sp = scope.ServiceProvider;
                        ((TenantContext)sp.GetRequiredService<ITenantContext>()).SetTenantId(Store);
                        var db = sp.GetRequiredService<ApplicationDbContext>();
                        var connection = await Identity(db, stoppingToken);
                        object? result = command.Action switch
                        {
                            "seed" => await Seed(sp, stoppingToken),
                            "reverse-ui" => await ReverseUi(sp, stoppingToken),
                            "process" => await Process(sp, command.Case, command.Outcome, stoppingToken),
                            "concurrent" => await Concurrent(command.Case, stoppingToken),
                            "replay-event" => await ReplayEvent(sp, stoppingToken),
                            "projections" => await Projections(sp, stoppingToken),
                            "readback" => await Readback(db, stoppingToken),
                            _ => throw new InvalidOperationException("Unknown fixed task action")
                        };
                        await Write(resultName, new { ok = true, command, connection, result, label = Label }, stoppingToken);
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning(e, "P5.1 local no-money command outcome {Action}", command.Action);
                        await Write(resultName, new { ok = false, command, error = e.ToString(), label = Label }, stoppingToken);
                    }
                }
                await Task.Delay(200, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RequireIndependentProof(CancellationToken ct)
    {
        using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(DirectoryPath, "main-runtime.verified.json"), ct));
        if (proof.RootElement.GetProperty("apiPid").GetInt32() != Environment.ProcessId ||
            proof.RootElement.GetProperty("database").GetString() != "PosEcommerceDb_Baseline" ||
            !proof.RootElement.GetProperty("tcpAndSqlMatched").GetBoolean()) throw new InvalidOperationException("Independent runtime proof required");
    }
    private static async Task<object> Identity(ApplicationDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "select current_database(), host(inet_server_addr()), pg_backend_pid()";
        await using var r = await cmd.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        if (r.GetString(0) != "PosEcommerceDb_Baseline" || r.GetString(1) != "192.168.1.5") throw new InvalidOperationException("Wrong authorized primary database/server");
        return new { database = r.GetString(0), server = r.GetString(1), backendPid = r.GetInt32(2), apiPid = Environment.ProcessId };
    }
    private Task Write(string name, object value, CancellationToken ct) => File.WriteAllTextAsync(Path.Combine(DirectoryPath, name), JsonSerializer.Serialize(value, Json), ct);

    private static async Task<object> Seed(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == Store, ct)) throw new InvalidOperationException("Task store already exists: do not reseed financial history");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Add(new Tenant { Id = Store, Name = Label, Slug = "p51-no-real-money", Currency = "EGP", Status = TenantStatus.Active, IsActive = true });
        var admin = new User { Id = Admin, TenantId = Store, Username = "p51-no-money-admin", Email = "p51-admin@example.invalid",
            Role = UserRole.Admin, FirstName = "P5.1", LastName = Label, IsActive = true };
        admin.PasswordHash = sp.GetRequiredService<IPasswordHasher<User>>().HashPassword(admin, Password); db.Add(admin);
        var cash = Account.Create(Store, AccountCodes.CashboxMain, Label, AccountType.Asset, preventNegative: true); cash.SetOpeningBalance(1000);
        var expense = Account.Create(Store, AccountCodes.AffiliateMarketingExpense, Label, AccountType.Expense);
        var payable = Account.Create(Store, AccountCodes.AffiliatePayable, Label, AccountType.Liability, preventNegative: true);
        db.AddRange(cash, expense, payable);
        foreach (var name in Cases)
        {
            var account = AffiliateAccount.Create(Guid.Empty, UserId(name), $"P51QA{Array.IndexOf(Cases, name) + 1:00}", $"{Label} {name}");
            account.Id = AccountId(name); account.Activate(); account.UpdatePayoutWallet("01000000000", "VodafoneCash");
            var user = AffiliateUser.Create(Guid.Empty, $"p51-{name}@example.invalid", "temporary", account.Id);
            user.Id = UserId(name); user.VerifyEmail();
            user.SetPasswordHash(new PasswordHasher<AffiliateUser>().HashPassword(user, Password)); db.AddRange(account, user);
            var amount = name == "ui" ? 500 : name == "underfunded" ? 100 : name == "synthetic" ? 900 : 150;
            var earned = Commission(db, account, amount, name == "synthetic", name);
            if (name is "ui" or "success") Commission(db, account, 900, true, name + "-excluded");
            if (name == "lineage")
            {
                var other = Commission(db, account, 10, false, name + "-conflict");
                var approved = db.ChangeTracker.Entries<AffiliateCommissionLedger>().Single(e => e.Entity.OrderItemId == earned.OrderItemId && e.Entity.EntryType == CommissionEntryType.Approved).Entity;
                typeof(AffiliateCommissionLedger).GetProperty("OriginalEntryId")!.SetValue(approved, other.Id);
            }
            await db.SaveChangesAsync(ct);
            if (name is "success" or "rollback")
                await sp.GetRequiredService<IFinancialPostingService>().PostJournalAsync(new PostingRequest
                {
                    TenantId = Store, JournalDate = DateTime.UtcNow, SourceType = "AffiliateCommissionEarned", SourceId = earned.OrderId,
                    SourceReference = Label, Description = Label, IdempotencyKey = $"AFFILIATE_COMM_EARNED:{Store}:{earned.OrderId}",
                    Lines = [PostingLine.Debit(expense.Id, amount, Label), PostingLine.Credit(payable.Id, amount, Label)]
                }, ct);
        }
        await tx.CommitAsync(ct);
        // Request and approval use their actual services and owned transactions, not domain-row updates.
        foreach (var name in Cases.Where(n => n != "ui"))
        {
            var service = sp.GetRequiredService<IAffiliatePayoutService>();
            AffiliatePayoutRequest request;
            if (name is "success" or "rollback")
            {
                request = await service.RequestPayoutAsync(AccountId(name), 150, Store, ct);
                await service.ApprovePayoutAsync(request.Id, Store, Admin, Label, ct);
            }
            else
            {
                var account = await db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == AccountId(name), ct);
                request = AffiliatePayoutRequest.Create(Store, account.Id, 150, 150, "EGP", account.PayoutWalletNumber!, account.PayoutWalletType!, account.PayoutWalletUpdatedAtUtc);
                request.Approve(Admin, Label + " deliberately invalid backing fixture"); db.Add(request); await db.SaveChangesAsync(ct);
            }
            db.Add(AffiliatePayoutTransferEvidence.Create(Store, "AFFILIATE_PAYOUT", $"{Label}_{request.Id:N}", 150, "EGP", DateTime.UtcNow,
                "P5_SIMULATED_NO_MONEY", new string('0', 64))); await db.SaveChangesAsync(ct);
        }
        return await Readback(db, ct);
    }

    private static AffiliateCommissionLedger Commission(ApplicationDbContext db, AffiliateAccount account, decimal amount, bool synthetic, string suffix)
    {
        var category = new Category { Id = Guid.NewGuid(), TenantId = Store, Name = Label };
        var product = new Product { Id = Guid.NewGuid(), TenantId = Store, CategoryId = category.Id, Name = Label };
        var affiliateCategory = AffiliateCategory.CreateMainCategory(Label, $"p51-{Guid.NewGuid():N}", 1); affiliateCategory.Id = Guid.NewGuid();
        var policy = AffiliateProductPolicy.Create(Store, product.Id, .1m, null, affiliateCategory.Id); policy.Id = Guid.NewGuid();
        var order = new Order { Id = Guid.NewGuid(), TenantId = Store, OrderNumber = $"{Label}_{suffix}", Currency = "EGP", Total = amount * 10,
            ShippingAddress = Label + " fixture only - not delivered", AffiliateAccountId = account.Id, AffiliateReferralCode = account.ReferralCode,
            Status = OrderStatus.Delivered, FulfillmentStatus = FulfillmentStatus.Delivered,
            PaymentStatus = synthetic ? PaymentStatus.SyntheticQaCollected : PaymentStatus.Collected };
        var item = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = product.Id, Quantity = 1, UnitPrice = order.Total, Total = order.Total,
            PaidAmount = order.Total, AffiliateCommissionEligible = true, AffiliateCommissionPolicyIdSnapshot = policy.Id, AffiliateCommissionRateSnapshot = .1m,
            AffiliateCommissionBaseAmountSnapshot = order.Total, AffiliateCommissionAmountSnapshot = amount, AffiliateCommissionSnapshotCapturedAtUtc = DateTime.UtcNow };
        order.Items.Add(item);
        var attribution = AffiliateAttribution.Create(Store, order.Id, account.Id, account.ReferralCode, AttributionSource.ReferralLink); attribution.Id = Guid.NewGuid();
        var earned = AffiliateCommissionLedger.CreateEarned(Store, attribution.Id, account.Id, order.Id, item.Id, order.Total, .1m, null, "EGP");
        db.AddRange(category, product, affiliateCategory, policy, order, attribution, earned, AffiliateCommissionLedger.CreateApproved(earned));
        if (synthetic)
        {
            var customer = new OnlineCustomer { Id = Guid.NewGuid(), TenantId = Store, Email = $"p51-{suffix}@example.invalid", CreatedAtUtc = DateTime.UtcNow };
            var link = AffiliatePromotionLink.Create(Store, account.UserId, product.Id, Guid.NewGuid().ToString("N")[..10], account.Id); link.Id = Guid.NewGuid();
            var run = new SyntheticQaRun { Id = Guid.NewGuid(), TenantId = Store, ProductId = product.Id, CustomerId = customer.Id, PromotionLinkId = link.Id,
                CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
            db.AddRange(customer, link, run, new SyntheticQaOrder { OrderId = order.Id, TenantId = Store, RunId = run.Id, EnrolledAtUtc = DateTime.UtcNow },
                new SyntheticQaCommissionPosting { Id = Guid.NewGuid(), OrderId = order.Id, Amount = amount, Currency = "EGP", PostedAtUtc = DateTime.UtcNow });
        }
        return earned;
    }

    private static async Task<object> ReverseUi(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var earned = await db.AffiliateCommissionLedger.IgnoreQueryFilters().SingleAsync(e => e.AffiliateAccountId == AccountId("ui") && e.EntryType == CommissionEntryType.Earned && e.CommissionAmount == 500, ct);
        var key = $"{Label}:stale-approval-reversal";
        if (!await db.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(e => e.IdempotencyKey == key, ct))
        {
            db.Add(AffiliateCommissionLedger.CreateReversalWithCustomKey(Store, earned.Id, earned.AttributionId, AccountId("ui"), earned.OrderId,
                earned.OrderItemId, 4000, 400, .1m, "EGP", key)); await db.SaveChangesAsync(ct);
        }
        return await Readback(db, ct);
    }

    private async Task<object> Process(IServiceProvider sp, string name, string outcome, CancellationToken ct)
    {
        if (!Cases.Contains(name) || name == "ui" || outcome is not ("success" or "before-commit" or "after-commit")) throw new InvalidOperationException("Fixed local simulation only");
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var request = await db.AffiliatePayoutRequests.IgnoreQueryFilters().SingleAsync(r => r.AffiliateAccountId == AccountId(name) && r.StoreTenantId == Store, ct);
        var account = await db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == request.AffiliateAccountId, ct);
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == Store, ct);
        if (tenant.Name != Label || !account.DisplayName.StartsWith(Label + " ") || request.RequestedAmount != 150 || request.Currency != "EGP" || account.PayoutWalletNumber != "01000000000")
            throw new InvalidOperationException("Refusing non-task authority");
        var reference = $"{Label}_{request.Id:N}";
        var evidence = await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().SingleAsync(e => e.PaymentReference == reference && e.StoreTenantId == Store, ct);
        if (evidence.VerificationSource != "P5_SIMULATED_NO_MONEY" || evidence.ExpectedAmount != 150) throw new InvalidOperationException("Simulation evidence only");
        // Clone, do not mutate registered/global options. Only these fixed labelled fixtures may use
        // the local confirmation. Readiness/eligibility/lineage/FIFO/posting safeguards remain real.
        var normal = sp.GetRequiredService<IOptions<AffiliateSettlementOptions>>().Value;
        var options = new AffiliateSettlementOptions { RequireVerifiedExternalPayoutEvidence = false,
            AccountingStaleMinutes = normal.AccountingStaleMinutes, IntegrationStaleMinutes = normal.IntegrationStaleMinutes };
        var posting = sp.GetRequiredService<IFinancialPostingService>();
        var boundary = new PostingBoundary(posting, outcome == "before-commit");
        var publisher = new PaidFactOnlyPublisher(sp.GetRequiredService<IEventPublisher>(), Store);
        var orchestrator = new AffiliateSettlementOrchestrator(db, sp.GetRequiredService<IAffiliatePayoutService>(), publisher, boundary,
            sp.GetRequiredService<IAccountResolver>(), sp.GetRequiredService<ICashboxLedgerAccountService>(),
            sp.GetRequiredService<ILogger<AffiliateSettlementOrchestrator>>(), Options.Create(options));
        var result = await orchestrator.ExecuteAffiliatePayoutAsync(account.Id, Store, 150, SettlementSource.WithdrawalEngine, reference,
            payoutRequestId: request.Id, paidByUserId: Admin, adminNotes: Label, ct: ct);
        // Capture the ACTUAL persisted/tracked EF outbox even if the normal dispatcher has
        // already delivered/deleted its SQL row. This is evidence, not another outbox model.
        var auditName = $"main-process-{name}-{Guid.NewGuid():N}.audit.json";
        var audit = new { result, boundary.Posts, publisher.Events, publishedFacts = publisher.Facts,
            persistedBusOutbox = db.ChangeTracker.Entries<BusMessage>().Select(e => new { e.Entity.SequenceNumber, e.Entity.MessageId,
                e.Entity.OutboxId, e.Entity.Body, e.Entity.MessageType, e.Entity.DestinationAddress }).ToList(),
            state = await Readback(db, ct), label = Label };
        await Write(auditName, audit, ct);
        if (outcome == "after-commit") throw new TimeoutException(Label + " simulated caller timeout AFTER COMMIT");
        return new { auditName, audit };
    }

    private async Task<object> ReplayEvent(IServiceProvider sp, CancellationToken ct)
    {
        var candidates = Directory.GetFiles(DirectoryPath, "main-process-success-*.audit.json");
        PointOfSale.Core.Events.Affiliate.AffiliateCommissionPaidEvent? fact = null;
        foreach (var candidate in candidates)
        {
            using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(candidate, ct));
            var facts = audit.RootElement.GetProperty("publishedFacts");
            if (facts.GetArrayLength() == 1) { fact = facts[0].Deserialize<PointOfSale.Core.Events.Affiliate.AffiliateCommissionPaidEvent>(Json); break; }
        }
        if (fact == null || fact.StoreTenantId != Store || fact.AffiliateAccountId != AccountId("success") || !fact.OrderNumber.StartsWith(Label + "_"))
            throw new InvalidOperationException("Original fixed-task persisted paid fact required");
        var db = sp.GetRequiredService<ApplicationDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await new PaidFactOnlyPublisher(sp.GetRequiredService<IEventPublisher>(), Store).PublishAsync(fact, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { replayedSameEventId = fact.EventId, fact.EventVersion, label = Label,
            outbox = db.ChangeTracker.Entries<BusMessage>().Select(e => new { e.Entity.SequenceNumber, e.Entity.MessageId, e.Entity.Body }).ToList() };
    }

    private static async Task<object> Projections(IServiceProvider sp, CancellationToken ct)
    {
        var mongo = sp.GetRequiredService<MongoDbContext>();
        return new
        {
            dashboards = await mongo.Database.GetCollection<AffiliateDashboardDocument>("affiliate_dashboards").Find(d => d.TenantId == Store).ToListAsync(ct),
            summaries = await mongo.Database.GetCollection<AffiliateEarningsSummaryDocument>("affiliate_earnings_summaries").Find(d => d.TenantId == Store).ToListAsync(ct),
            orders = await mongo.Database.GetCollection<AffiliateOrderDocument>("affiliate_orders").Find(d => d.TenantId == Store).ToListAsync(ct)
        };
    }

    private async Task<object> Concurrent(string name, CancellationToken ct)
    {
        if (name != "success") throw new InvalidOperationException("Only fixed success fixture may race");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = 0;
        async Task<object> Attempt()
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            ((TenantContext)sp.GetRequiredService<ITenantContext>()).SetTenantId(Store);
            await Identity(sp.GetRequiredService<ApplicationDbContext>(), ct);
            if (Interlocked.Increment(ref waiting) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(ct);
            try { return new { ok = true, result = await Process(sp, name, "success", ct) }; }
            catch (Exception e) { return new { ok = false, error = e.ToString() }; }
        }
        return await Task.WhenAll(Attempt(), Attempt());
    }

    private static async Task<object> Readback(ApplicationDbContext db, CancellationToken ct) => new
    {
        requests = await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.StoreTenantId == Store).ToListAsync(ct),
        commissions = await db.AffiliateCommissionLedger.IgnoreQueryFilters().AsNoTracking().Where(r => r.StoreTenantId == Store).ToListAsync(ct),
        debits = await db.AffiliateBalanceLedger.IgnoreQueryFilters().AsNoTracking().Where(r => r.StoreTenantId == Store).ToListAsync(ct),
        journals = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == Store).ToListAsync(ct),
        lines = await db.LedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == Store).ToListAsync(ct),
        accounts = await db.LedgerAccounts.IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == Store).ToListAsync(ct),
        evidence = await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().AsNoTracking().Where(r => r.StoreTenantId == Store).ToListAsync(ct),
        allocations = await db.AffiliatePayoutTransferAllocations.IgnoreQueryFilters().AsNoTracking().Where(r => r.StoreTenantId == Store).ToListAsync(ct),
        busOutbox = await db.Set<BusMessage>().AsNoTracking().Where(r => r.Body.Contains(Store.ToString())).ToListAsync(ct)
    };

    private sealed class PostingBoundary(IFinancialPostingService actual, bool fail) : IFinancialPostingService
    {
        public int Posts { get; private set; }
        public async Task<JournalEntry> PostJournalAsync(PostingRequest request, CancellationToken ct = default)
        { Posts++; var journal = await actual.PostJournalAsync(request, ct); if (fail) throw new TimeoutException(Label + " simulated posting timeout BEFORE COMMIT"); return journal; }
        public Task<JournalEntry?> GetJournalByIdempotencyKeyAsync(Guid tenantId, string key, CancellationToken ct = default) => actual.GetJournalByIdempotencyKeyAsync(tenantId, key, ct);
        public Task<JournalEntry?> GetJournalAsync(Guid tenantId, Guid id, CancellationToken ct = default) => actual.GetJournalAsync(tenantId, id, ct);
        public Task ValidateJournalAsync(PostingRequest request, CancellationToken ct = default) => actual.ValidateJournalAsync(request, ct);
        public Task<bool> ExistsByIdempotencyKeyAsync(Guid tenantId, string key, CancellationToken ct = default) => actual.ExistsByIdempotencyKeyAsync(tenantId, key, ct);
        public Task<JournalEntry> ReverseJournalAsync(Guid tenantId, Guid journalId, string reason, Guid? userId = null, CancellationToken ct = default) => throw new InvalidOperationException("No reversals from simulator");
    }
    private sealed class PaidFactOnlyPublisher(IEventPublisher actual, Guid store) : IEventPublisher
    {
        public int Events { get; private set; }
        public List<PointOfSale.Core.Events.Affiliate.AffiliateCommissionPaidEvent> Facts { get; } = [];
        public Task PublishAsync<T>(T value, CancellationToken cancellationToken = default) where T : IntegrationEvent => PublishAsync((object)value, cancellationToken);
        public Task PublishAsync(object value, CancellationToken cancellationToken = default)
        {
            if (value is not PointOfSale.Core.Events.Affiliate.AffiliateCommissionPaidEvent e || e.StoreTenantId != store ||
                !e.OrderNumber.StartsWith(Label + "_")) throw new InvalidOperationException("Only labelled local paid fact may leave simulator; no payout instructions");
            Events++; Facts.Add(e); return actual.PublishAsync(value, cancellationToken);
        }
        public Task PublishBatchAsync<T>(IEnumerable<T> values, CancellationToken cancellationToken = default) where T : IntegrationEvent => throw new InvalidOperationException("No batch publication");
        public Task PublishSensitiveAsync<T>(T value, CancellationToken cancellationToken = default) where T : IntegrationEvent, ISensitiveIntegrationEvent => throw new InvalidOperationException("No sensitive/provider publication");
    }
    private sealed record Command(string Action, string Case = "success", string Outcome = "success");
}
#endif
