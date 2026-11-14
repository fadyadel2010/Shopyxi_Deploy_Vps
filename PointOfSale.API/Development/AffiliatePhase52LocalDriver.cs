#if DEBUG
using System.Text.Json;
using OutboxMessage = MassTransit.EntityFrameworkCoreIntegration.OutboxMessage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Models.Accounting;
using Npgsql;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.API.Development;

/// <summary>Fixed P5.2 identities only, local file opt-in, no HTTP injection route.
/// No order/attribution/commission/projection construction: checkout and canonical
/// lifecycle services must establish those. Only explicit simulated payout facts leave
/// the guarded payout scope. Does not change global policy or Synthetic QA.</summary>
public sealed class AffiliatePhase52LocalDriver(IServiceScopeFactory scopes, IWebHostEnvironment environment,
    ILogger<AffiliatePhase52LocalDriver> logger) : BackgroundService
{
    public const string Label = "P5.2_TEST_NO_REAL_MONEY";
    private static readonly Guid Store = AffiliatePhase51LocalDriver.Store;
    private static readonly Guid Admin = AffiliatePhase51LocalDriver.Admin;
    private static readonly Guid Affiliate = Guid.Parse("52520000-2026-4022-a100-000000000001");
    private static readonly Guid User = Guid.Parse("52520000-2026-4022-a200-000000000001");
    private static readonly Guid ProductId = Guid.Parse("52520000-2026-4022-a300-000000000001");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string DirectoryPath => Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".ai", "evidence", "affiliate-p52-20261022"));
    private Task Write(string name, object value, CancellationToken ct) => File.WriteAllTextAsync(Path.Combine(DirectoryPath, name), JsonSerializer.Serialize(value, Json), ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Development only");
        await Task.Yield();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var identity = await Identity(db, stoppingToken);
            await Write("runtime.json", identity, stoppingToken);
            var sp = scope.ServiceProvider;
            var configuration = sp.GetRequiredService<IConfiguration>();
            var options = sp.GetRequiredService<IOptions<AffiliateSettlementOptions>>().Value;
            await Write("safety.json", new { options = new { options.RequireVerifiedExternalPayoutEvidence,
                options.AllowManualPayoutEvidenceOverride, options.RequireVerifiedManualDeliveryEvidence,
                options.AllowManualDeliveryEvidenceOverride, options.AccountingStaleMinutes, options.IntegrationStaleMinutes },
                qaEnabled = configuration.GetValue<bool>("SyntheticQa:Enabled"),
                qaVerifierCount = configuration.GetSection("SyntheticQa:VerifierIds").GetChildren().Count(),
                qaSigningKeyPresent = !string.IsNullOrEmpty(configuration["SyntheticQa:SigningKey"]),
                posting = sp.GetRequiredService<IFinancialPostingService>().GetType().FullName,
                publisher = sp.GetRequiredService<IEventPublisher>().GetType().FullName }, stoppingToken);
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested && File.Exists(Path.Combine(DirectoryPath, "runtime.enabled")))
            {
                foreach (var file in Directory.GetFiles(DirectoryPath, "p52-*.command.json").Order())
                {
                    var name = Path.GetFileName(file).Replace(".command.json", ".result.json");
                    if (File.Exists(Path.Combine(DirectoryPath, name))) continue;
                    var command = JsonSerializer.Deserialize<Command>(await File.ReadAllTextAsync(file, stoppingToken), Json)!;
                    try
                    {
                        using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(DirectoryPath, "runtime.verified.json"), stoppingToken));
                        if (proof.RootElement.GetProperty("apiPid").GetInt32() != Environment.ProcessId ||
                            !proof.RootElement.GetProperty("tcpAndSqlMatched").GetBoolean()) throw new InvalidOperationException("Independent primary runtime proof required");
                        await using var scope = scopes.CreateAsyncScope();
                        var sp = scope.ServiceProvider;
                        ((TenantContext)sp.GetRequiredService<ITenantContext>()).SetTenantId(Store);
                        var db = sp.GetRequiredService<ApplicationDbContext>();
                        await Identity(db, stoppingToken);
                        object result = command.Action switch
                        {
                            "setup" => await Setup(sp, stoppingToken),
                            "read" => await Read(sp, stoppingToken),
                            "evidence" => await PrepareEvidence(sp, command.RequestId, stoppingToken),
                            "process" => await Process(command.RequestId, command.Outcome, stoppingToken),
                            "race" => await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Process(command.RequestId, "normal", stoppingToken))),
                             "recognize" => await Recognize(sp, command.OrderId, stoppingToken),
                            "replay-event" => await Replay(sp, command.EventId, stoppingToken),
                            _ => throw new InvalidOperationException("Unknown fixed task action")
                        };
                        await Write(name, new { ok = true, command, result, apiPid = Environment.ProcessId, label = Label }, stoppingToken);
                    }
                    catch (Exception error)
                    {
                        logger.LogWarning(error, "P5.2 task command {Action} failed", command.Action);
                        await Write(name, new { ok = false, command, error = error.ToString(), label = Label }, stoppingToken);
                    }
                }
                await Task.Delay(200, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static async Task<object> Identity(ApplicationDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "select current_database(), host(inet_server_addr()), pg_backend_pid()";
        await using var reader = await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
        if (reader.GetString(0) != "PosEcommerceDb_Baseline" || reader.GetString(1) != "192.168.1.5")
            throw new InvalidOperationException("Wrong database/server");
        return new { database = reader.GetString(0), server = reader.GetString(1), backendPid = reader.GetInt32(2), apiPid = Environment.ProcessId };
    }

    private static async Task<object> Setup(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == Store && t.Name == AffiliatePhase51LocalDriver.Label, ct))
            throw new InvalidOperationException("Existing task store required");
        if (await db.AffiliateAccounts.IgnoreQueryFilters().AnyAsync(a => a.Id == Affiliate, ct))
            throw new InvalidOperationException("Task fixture exists; do not reseed");
        var account = AffiliateAccount.Create(Guid.Empty, User, "P52NOMONEY", Label);
        account.Id = Affiliate; account.Activate(); account.UpdatePayoutWallet("01000000000", "VodafoneCash");
        var user = AffiliateUser.Create(Guid.Empty, "p52-canonical@example.invalid", "temporary", Affiliate);
        user.Id = User; user.VerifyEmail();
        user.SetPasswordHash(new PasswordHasher<AffiliateUser>().HashPassword(user, "P52NoRealMoney!2026"));
        var category = new Category { Id = Guid.NewGuid(), TenantId = Store, Name = Label };
        var unit = new Unit { Id = Guid.NewGuid(), TenantId = Store, Name = Label, Symbol = "P52", IsActive = true };
        var product = new Product { Id = ProductId, TenantId = Store, CategoryId = category.Id, Name = Label,
            SKU = "P52-NO-MONEY", Slug = "p52-test-no-real-money", Description = Label + " — fictional development item, never fulfill physically",
            SalePrice = 1500, OnlinePrice = 1500, TrackStock = false, AllowBackorder = true,
            PublishStatus = PublishStatus.Published, IsOnlineVisible = true, IsActive = true };
        db.AddRange(account, user, category, unit, product);
        db.Add(new ProductUnit { ProductId = ProductId, UnitId = unit.Id, SellingPrice = 1500,
            PurchasePrice = 0, ConversionFactor = 1, IsDefault = true, IsBaseUnit = true });
        await db.SaveChangesAsync(ct);
        var affiliateCategory = await db.AffiliateCategories.IgnoreQueryFilters()
            .FirstAsync(c => c.Name == AffiliatePhase51LocalDriver.Label && c.IsActive, ct);
        await sp.GetRequiredService<IAffiliateService>().EnableAffiliateWithCategoryAsync(ProductId, .1m, affiliateCategory.Id, ct: ct);
        return new { affiliate = Affiliate, user = User, product = ProductId, store = Store, label = Label };
    }

    private static async Task<object> Recognize(IServiceProvider sp, Guid orderId, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var order = await db.Orders.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == orderId && o.TenantId == Store && o.AffiliateAccountId == Affiliate, ct);
        if (order.CustomerNotes?.Contains(Label) != true || !await db.OrderItems.IgnoreQueryFilters().AnyAsync(i => i.OrderId == orderId && i.ProductId == ProductId, ct))
            throw new InvalidOperationException("Canonical labelled task order required");
        var service = sp.GetRequiredService<IAffiliateService>();
        await service.RecordCommissionForOrderAsync(orderId, ct);
        await service.ApproveCommissionForOrderAsync(orderId, ct);
        return await Read(sp, ct);
    }

    private static async Task<object> PrepareEvidence(IServiceProvider sp, Guid requestId, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var request = await db.AffiliatePayoutRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == requestId && r.StoreTenantId == Store && r.AffiliateAccountId == Affiliate, ct);
        if (request.RequestedAmount != 150 || request.Currency != "EGP" || request.PayoutWalletNumberSnapshot != "01000000000")
            throw new InvalidOperationException("Fixed task simulation only");
        var reference = $"{Label}_{requestId:N}";
        var evidence = await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().SingleOrDefaultAsync(e => e.PaymentReference == reference, ct);
        if (evidence == null)
        {
            db.Add(AffiliatePayoutTransferEvidence.Create(Store, "AFFILIATE_PAYOUT", reference, 150, "EGP", DateTime.UtcNow,
                "P5_SIMULATED_NO_MONEY", new string('0', 64)));
            await db.SaveChangesAsync(ct);
        }
        else if (evidence.VerificationSource != "P5_SIMULATED_NO_MONEY" || evidence.StoreTenantId != Store || evidence.ExpectedAmount != 150)
            throw new InvalidOperationException("Task simulation evidence mismatch");
        return new { requestId, reference, provenance = "P5_SIMULATED_NO_MONEY" };
    }

    private async Task<object> Process(Guid requestId, string outcome, CancellationToken ct)
    {
        if (outcome is not ("normal" or "before-commit" or "after-commit" or "serialization" or "serialization-exhaustion" or
            "serialization-after-commit" or "amount-mismatch" or "evidence-mismatch" or "replay-reference-mismatch"))
            throw new InvalidOperationException("Unknown fixed simulation outcome");
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        ((TenantContext)sp.GetRequiredService<ITenantContext>()).SetTenantId(Store);
        var db = sp.GetRequiredService<ApplicationDbContext>(); await Identity(db, ct);
        var request = await db.AffiliatePayoutRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == requestId && r.StoreTenantId == Store && r.AffiliateAccountId == Affiliate, ct);
        if (request.RequestedAmount != 150 || request.Currency != "EGP" || request.PayoutWalletNumberSnapshot != "01000000000")
            throw new InvalidOperationException("Fixed task simulation only");
        var reference = $"{Label}_{requestId:N}";
        var evidence = await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().SingleAsync(e => e.PaymentReference == reference, ct);
        if (evidence.VerificationSource != "P5_SIMULATED_NO_MONEY" || evidence.StoreTenantId != Store || evidence.ExpectedAmount != 150)
            throw new InvalidOperationException("Task simulation evidence mismatch");
        var normal = sp.GetRequiredService<IOptions<AffiliateSettlementOptions>>().Value;
        var options = new AffiliateSettlementOptions { RequireVerifiedExternalPayoutEvidence = false,
            AccountingStaleMinutes = normal.AccountingStaleMinutes, IntegrationStaleMinutes = normal.IntegrationStaleMinutes };
        var facts = new List<AffiliateCommissionPaidEvent>();
        var envelopes = new List<object>();
        var publisher = new PaidOnly(sp.GetRequiredService<IEventPublisher>(), facts);
        var retries = new List<string>();
        var retryScopes = new GuardedScopes(scopes, facts, envelopes, retries, outcome == "serialization-exhaustion");
        IFinancialPostingService posting = sp.GetRequiredService<IFinancialPostingService>();
        if (outcome is "before-commit" or "serialization" or "serialization-exhaustion" or "serialization-after-commit")
            posting = new PostingBoundary(posting, db, outcome);
        var orchestrator = new AffiliateSettlementOrchestrator(db, sp.GetRequiredService<IAffiliatePayoutService>(), publisher,
            posting, sp.GetRequiredService<IAccountResolver>(),
            sp.GetRequiredService<ICashboxLedgerAccountService>(), sp.GetRequiredService<ILogger<AffiliateSettlementOrchestrator>>(), Options.Create(options), retryScopes);
        PayoutExecutionResult result;
        try
        {
            result = await orchestrator.ExecuteAffiliatePayoutAsync(Affiliate, Store, outcome == "amount-mismatch" ? 149 : 150,
                SettlementSource.WithdrawalEngine, outcome == "replay-reference-mismatch" ? reference + "_MISMATCH" : reference,
                payoutRequestId: requestId, paidByUserId: Admin, adminNotes: Label,
                externalTransferAmount: outcome == "evidence-mismatch" ? 151 : null, ct: ct);
        }
        catch (Exception error)
        {
            await Write($"p52-failure-{Guid.NewGuid():N}.audit.json", new { requestId, outcome, error = error.ToString(),
                retryScopes = retries, facts, outbox = envelopes.Concat(Envelopes(db)).ToList() }, ct);
            throw;
        }
        envelopes.AddRange(Envelopes(db));
        var audit = new { result, facts, outbox = envelopes, outcome, retryScopes = retries };
        await Write($"p52-process-{Guid.NewGuid():N}.audit.json", audit, ct);
        if (outcome is "after-commit" or "serialization-after-commit") throw new TimeoutException(Label + " simulated caller timeout AFTER COMMIT; audit retained");
        return audit;
    }

    private async Task<object> Replay(IServiceProvider sp, Guid eventId, CancellationToken ct)
    {
        AffiliateCommissionPaidEvent? fact = null;
        foreach (var file in Directory.GetFiles(DirectoryPath, "p52-process-*.audit.json"))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
            fact = json.RootElement.GetProperty("facts").EnumerateArray().Select(e => e.Deserialize<AffiliateCommissionPaidEvent>(Json)).FirstOrDefault(e => e!.EventId == eventId);
            if (fact != null) break;
        }
        if (fact == null) throw new InvalidOperationException("Captured task fact required");
        var db = sp.GetRequiredService<ApplicationDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await new PaidOnly(sp.GetRequiredService<IEventPublisher>()).PublishAsync(fact, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { fact.EventId, outbox = db.ChangeTracker.Entries<OutboxMessage>().Select(e => new { e.Entity.SequenceNumber, e.Entity.MessageId, e.Entity.Body }).ToList() };
    }

    private static async Task<object> Read(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var mongo = sp.GetRequiredService<MongoDbContext>();
        return new { balance = await sp.GetRequiredService<IAffiliatePayoutService>().GetAvailableBalanceAsync(Affiliate, Store, ct),
            orders = await db.Orders.IgnoreQueryFilters().AsNoTracking().Where(o => o.TenantId == Store && o.AffiliateAccountId == Affiliate).ToListAsync(ct),
            commissions = await db.AffiliateCommissionLedger.IgnoreQueryFilters().AsNoTracking().Where(c => c.AffiliateAccountId == Affiliate).ToListAsync(ct),
            requests = await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.AffiliateAccountId == Affiliate).ToListAsync(ct),
            projections = await mongo.Database.GetCollection<AffiliateOrderDocument>("affiliate_orders")
                .Find(d => d.AffiliateAccountId == Affiliate).ToListAsync(ct),
            dashboards = await mongo.Database.GetCollection<AffiliateDashboardDocument>("affiliate_dashboards")
                .Find(d => d.AffiliateAccountId == Affiliate).ToListAsync(ct),
            summaries = await mongo.Database.GetCollection<AffiliateEarningsSummaryDocument>("affiliate_earnings_summaries")
                .Find(d => d.AffiliateAccountId == Affiliate).ToListAsync(ct) };
    }

    private static IEnumerable<object> Envelopes(ApplicationDbContext db) => db.ChangeTracker.Entries<OutboxMessage>()
        .Select(e => (object)new { e.Entity.SequenceNumber, e.Entity.MessageId, e.Entity.Body }).ToList();

    private sealed class GuardedScopes(IServiceScopeFactory actual, List<AffiliateCommissionPaidEvent> facts, List<object> envelopes,
        List<string> retries, bool exhaust) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            retries.Add("Fresh DI scope after rolled-back PostgreSQL transaction");
            return new GuardedScope(actual.CreateScope(), facts, envelopes, exhaust);
        }
        private sealed class GuardedScope(IServiceScope actual, List<AffiliateCommissionPaidEvent> facts, List<object> envelopes, bool exhaust) : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type type) => type == typeof(IEventPublisher)
                ? new PaidOnly(actual.ServiceProvider.GetRequiredService<IEventPublisher>(), facts)
                : type == typeof(IFinancialPostingService) && exhaust
                ? new PostingBoundary(actual.ServiceProvider.GetRequiredService<IFinancialPostingService>(),
                    actual.ServiceProvider.GetRequiredService<ApplicationDbContext>(), "serialization-exhaustion")
                : actual.ServiceProvider.GetService(type);
            private void Capture() => envelopes.AddRange(Envelopes(actual.ServiceProvider.GetRequiredService<ApplicationDbContext>()));
            public void Dispose() { Capture(); actual.Dispose(); }
            public async ValueTask DisposeAsync()
            {
                Capture();
                if (actual is IAsyncDisposable asyncScope) await asyncScope.DisposeAsync(); else actual.Dispose();
            }
        }
    }

    private sealed class PostingBoundary(IFinancialPostingService actual, ApplicationDbContext db, string outcome) : IFinancialPostingService
    {
        public async Task<JournalEntry> PostJournalAsync(PostingRequest request, CancellationToken ct = default)
        {
            await actual.PostJournalAsync(request, ct);
            if (outcome.StartsWith("serialization", StringComparison.Ordinal))
            {
                // Real PostgreSQL transaction-aborting response after real posting SaveChanges.
                // Fixed SQL only, no alternate database and no synthetic financial state.
                await db.Database.ExecuteSqlRawAsync("DO $$ BEGIN RAISE EXCEPTION USING ERRCODE = '40001', MESSAGE = 'P5.2_TEST_NO_REAL_MONEY controlled serialization abort'; END $$;", ct);
                throw new InvalidOperationException("Expected PostgreSQL abort did not occur");
            }
            throw new TimeoutException(Label + " simulated posting timeout BEFORE COMMIT");
        }
        public Task<JournalEntry?> GetJournalByIdempotencyKeyAsync(Guid tenantId, string key, CancellationToken ct = default) => actual.GetJournalByIdempotencyKeyAsync(tenantId, key, ct);
        public Task<JournalEntry?> GetJournalAsync(Guid tenantId, Guid id, CancellationToken ct = default) => actual.GetJournalAsync(tenantId, id, ct);
        public Task ValidateJournalAsync(PostingRequest request, CancellationToken ct = default) => actual.ValidateJournalAsync(request, ct);
        public Task<bool> ExistsByIdempotencyKeyAsync(Guid tenantId, string key, CancellationToken ct = default) => actual.ExistsByIdempotencyKeyAsync(tenantId, key, ct);
        public Task<JournalEntry> ReverseJournalAsync(Guid tenantId, Guid journalId, string reason, Guid? userId = null, CancellationToken ct = default) => throw new InvalidOperationException("No reversals");
    }

    private sealed class PaidOnly(IEventPublisher actual, List<AffiliateCommissionPaidEvent>? captured = null) : IEventPublisher
    {
        public Task PublishAsync<T>(T value, CancellationToken cancellationToken = default) where T : IntegrationEvent => PublishAsync((object)value, cancellationToken);
        public Task PublishAsync(object value, CancellationToken cancellationToken = default)
        {
            if (value is not AffiliateCommissionPaidEvent fact || fact.StoreTenantId != Store || fact.AffiliateAccountId != Affiliate)
                throw new InvalidOperationException("Only the fixed task Paid fact may leave this scope");
            captured?.Add(fact); return actual.PublishAsync(value, cancellationToken);
        }
        public Task PublishBatchAsync<T>(IEnumerable<T> values, CancellationToken cancellationToken = default) where T : IntegrationEvent => throw new InvalidOperationException("No batches");
        public Task PublishSensitiveAsync<T>(T value, CancellationToken cancellationToken = default) where T : IntegrationEvent, ISensitiveIntegrationEvent => throw new InvalidOperationException("No provider instructions");
    }
    private sealed record Command(string Action, Guid RequestId = default, Guid OrderId = default, Guid EventId = default, string Outcome = "normal");
}
#endif
