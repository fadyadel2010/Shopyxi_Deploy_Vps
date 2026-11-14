using System.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MassTransit;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Infrastructure.Treasury;
using PointOfSale.Infrastructure.Consumers.Affiliate;
using PointOfSale.API.Controllers;
using Microsoft.AspNetCore.Mvc;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Opt-in primary-development PostgreSQL only. Fresh retained P512 fixtures. No external operations or simulator.</summary>
public sealed class AffiliateP512ManualPayoutTests
{
    [PrimaryAffiliateFact]
    public async Task BrowserReview_ReadOnlyControllerContract()
    {
        var path = Path.Combine(TestConfiguration.GetSolutionRoot(), ".ai/reports/p512-browser-fixture.json");
        if (!File.Exists(path)) return;
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var store = metadata.RootElement.GetProperty("Store").GetGuid();
        var request = metadata.RootElement.GetProperty("requestId").GetGuid();
        await using var db = await AffiliateP53ePrimarySupport.Open("P512-readonly-review-controller");
        var tenant = new TenantContext(); tenant.SetTenantId(store);
        var controller = new AffiliateManualPayoutController(db, tenant, Mock.Of<IAffiliateSettlementOrchestrator>());
        var result = Assert.IsType<OkObjectResult>(await controller.Review(request, default));
        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(request, json.GetProperty("requestId").GetGuid());
        Assert.Equal(2, json.GetProperty("sources").GetArrayLength());
        var action = new ActionContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(), new(), new());
        var context = new Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext(action, [], result, controller);
        var filter = new PointOfSale.API.Infrastructure.Mvc.PreventEntityExposureFilter(
            NullLogger<PointOfSale.API.Infrastructure.Mvc.PreventEntityExposureFilter>.Instance, Options.Create(new JsonOptions()));
        await filter.OnResultExecutionAsync(context, () => Task.FromResult(
            new Microsoft.AspNetCore.Mvc.Filters.ResultExecutedContext(action, [], context.Result, controller)));
        Assert.IsType<OkObjectResult>(context.Result);
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        private PointOfSale.Tests.Integration.ProductionCoordinationAuthorityTests.Fixture? _ownedFixture;

        // New bounded entry point for P514 only. Existing primary/browser
        // fixture paths remain unchanged and are NOT authorized by this path.
        internal static async Task<Fixture> CreateGuardedP514Infrastructure()
        {
            var owned = await PointOfSale.Tests.Integration.ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
            var f = new Fixture { _ownedFixture = owned, Store = owned.VendorId, Connection = owned.ConnectionString,
                Label = $"P514-DISPOSABLE-NO-EXTERNAL-TRANSFER-{Guid.NewGuid():N}" };
            try
            {
                f.Db = f.Open();
                var affiliate = AffiliateAccount.Create(Guid.Empty, Guid.NewGuid(), $"P514{Guid.NewGuid():N}"[..12], f.Label);
                affiliate.Id = Guid.NewGuid(); affiliate.Activate();
                // Fictional local fixture destination only. No provider or
                // transfer implementation is registered or invoked by P514.
                affiliate.UpdatePayoutWallet("01000000000", "VodafoneCash");
                f.Affiliate = affiliate;
                var user = AffiliateUser.Create(Guid.Empty, $"p514-{affiliate.Id:N}@example.invalid",
                    Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), affiliate.Id);
                user.Id = affiliate.UserId; user.VerifyEmail();
                var email = $"p514-{f.Actor:N}@example.invalid";
                f.Db.AddRange(new User { Id = f.Actor, TenantId = f.Store, Username = email, Email = email,
                    NormalizedEmail = email.ToUpperInvariant(), NormalizedUsername = email.ToUpperInvariant(),
                    PasswordHash = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                    Role = UserRole.Owner, IsActive = true, FirstName = "P514", LastName = "DISPOSABLE ONLY",
                    EmailVerificationStatus = EmailVerificationStatus.Verified }, affiliate, user);
                await f.Db.SaveChangesAsync();
                await new TenantAccountSeeder(f.Db, NullLogger<TenantAccountSeeder>.Instance).SeedAsync(f.Store);
                var cashboxes = new CashboxLedgerAccountService(f.Db, NullLogger<CashboxLedgerAccountService>.Instance);
                foreach (var id in new[] { f.CashboxId, f.EmptyCashboxId })
                {
                    var cashbox = new Cashbox { Id = id, TenantId = f.Store, Name = f.Label + (id == f.CashboxId ? " funded" : " empty"),
                        IsActive = true, OpenedByUserId = f.Actor, OpenedAt = DateTime.UtcNow };
                    f.Db.Add(cashbox); await f.Db.SaveChangesAsync();
                    var mapping = await cashboxes.EnsureCashboxAccountAsync(cashbox);
                    if (id == f.CashboxId) f.SourceAccountId = mapping.AccountId;
                }
                var treasury = await f.Db.TreasuryAccounts.IgnoreQueryFilters().SingleAsync(t => t.AccountId == f.SourceAccountId);
                var capitalization = await new TreasuryInitialCapitalizationService(f.Db, Posting(f.Db)).CreateAndPostAsync(f.Store, f.Actor,
                    new(Guid.NewGuid(), DateTime.UtcNow.Date, [new(treasury.Id, 500m)], f.Label,
                        "Disposable isolated SQL simulation; no real cash, wallet or external transfer",
                        TreasuryInitialCapitalization.DeclarationIdentifier, true));
                f.CapitalizationId = capitalization.Id;
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Guid Store = Guid.NewGuid(), Actor = Guid.NewGuid(), OrderId = Guid.NewGuid();
        internal AffiliateAccount Affiliate = null!;
        internal Guid CashboxId = Guid.NewGuid(), EmptyCashboxId = Guid.NewGuid(), SourceAccountId;
        internal string Connection = "";
        internal AffiliatePayoutRequest Request = null!;
        internal ApplicationDbContext Db = null!;
        internal Guid CapitalizationId;
        internal string Label = $"P512-NO-EXTERNAL-TRANSFER-{Guid.NewGuid():N}";
        internal ApplicationDbContext Open(params IInterceptor[] interceptors)
        {
            var tenant = new TenantContext(); tenant.SetTenantId(Store);
            return new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(Connection).AddInterceptors(interceptors).Options, tenant);
        }
        internal static async Task<Fixture> Create(bool browser = false, bool approved = true, decimal amount = 150m, bool infrastructureOnlyP514 = false)
        {
            await using var primary = await AffiliateP53ePrimarySupport.Open("P512-create-task-owned-fixture");
            var f = new Fixture { Connection = primary.Database.GetConnectionString()! };
            if (infrastructureOnlyP514) f.Label = $"P514-NO-EXTERNAL-TRANSFER-{Guid.NewGuid():N}";
            f.Db = f.Open();
            var sourceAffiliate = await primary.AffiliateAccounts.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(a => a.Id == Guid.Parse("01a03e11-e234-7d40-b77e-99f9463c7a40"));
            var sourceUser = await primary.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == sourceAffiliate.UserId);
            var sourceAdmin = await primary.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Email == "ahramco.software@gmail.com");
            var affiliate = AffiliateAccount.Create(Guid.Empty, Guid.NewGuid(), $"P512{Guid.NewGuid():N}"[..12], f.Label);
            affiliate.Id = Guid.NewGuid(); affiliate.Activate();
            // Copy authoritative destination only; no fabricated wallet/provider identifier.
            affiliate.UpdatePayoutWallet(sourceAffiliate.PayoutWalletNumber!, sourceAffiliate.PayoutWalletType!);
            f.Affiliate = affiliate;
            var user = AffiliateUser.Create(Guid.Empty, $"p512-{affiliate.Id:N}@example.invalid", sourceUser.PasswordHash, affiliate.Id);
            user.Id = affiliate.UserId; user.VerifyEmail();
            var email = $"p512-{f.Actor:N}@example.invalid";
            f.Db.AddRange(new Tenant { Id = f.Store, Name = f.Label, Slug = $"p512-{f.Store:N}", Currency = "EGP", IsActive = true, Status = TenantStatus.Active },
                new User { Id = f.Actor, TenantId = f.Store, Username = email, Email = email, NormalizedEmail = email.ToUpperInvariant(),
                    NormalizedUsername = email.ToUpperInvariant(), PasswordHash = sourceAdmin.PasswordHash, Role = UserRole.Owner, IsActive = true,
                    FirstName = "P512", LastName = "INTERNAL TEST ONLY", EmailVerificationStatus = EmailVerificationStatus.Verified }, affiliate, user);
            await f.Db.SaveChangesAsync();
            await new TenantAccountSeeder(f.Db, NullLogger<TenantAccountSeeder>.Instance).SeedAsync(f.Store);
            var cashboxes = new CashboxLedgerAccountService(f.Db, NullLogger<CashboxLedgerAccountService>.Instance);
            foreach (var id in new[] { f.CashboxId, f.EmptyCashboxId })
            {
                var cashbox = new Cashbox { Id = id, TenantId = f.Store, Name = f.Label + (id == f.CashboxId ? " funded" : " empty"), IsActive = true,
                    OpenedByUserId = f.Actor, OpenedAt = DateTime.UtcNow };
                f.Db.Add(cashbox); await f.Db.SaveChangesAsync();
                var mapping = await cashboxes.EnsureCashboxAccountAsync(cashbox);
                if (id == f.CashboxId) f.SourceAccountId = mapping.AccountId;
            }
            var treasury = await f.Db.TreasuryAccounts.IgnoreQueryFilters().SingleAsync(t => t.AccountId == f.SourceAccountId);
            var cap = await new TreasuryInitialCapitalizationService(f.Db, Posting(f.Db)).CreateAndPostAsync(f.Store, f.Actor,
                new(Guid.NewGuid(), DateTime.UtcNow.Date, [new(treasury.Id, 500m)], f.Label,
                    "P512 authorized internal development test capital; no actual collection or external transfer", TreasuryInitialCapitalization.DeclarationIdentifier, true));
            f.CapitalizationId = cap.Id;
            if (infrastructureOnlyP514) return f;
            var category = new Category { Id = Guid.NewGuid(), TenantId = f.Store, Name = f.Label };
            var product = new Product { Id = Guid.NewGuid(), TenantId = f.Store, CategoryId = category.Id, Name = f.Label };
            var policyCategory = AffiliateCategory.CreateMainCategory(f.Label, $"p512-{Guid.NewGuid():N}", 1); policyCategory.Id = Guid.NewGuid();
            var policy = AffiliateProductPolicy.Create(f.Store, product.Id, .1m, null, policyCategory.Id); policy.Id = Guid.NewGuid();
            // Explicit internal fixture at the pre-existing approved-commission boundary.
            // No real collection/delivery command or physical operation is executed.
            var order = new Order { Id = f.OrderId, TenantId = f.Store, OrderNumber = f.Label,
                CustomerNotes = "P512 internal settlement fixture — NOT a real collection, shipment or delivery", ShippingAddress = "DO NOT SHIP — INTERNAL TEST",
                Currency = "EGP", Total = amount * 10, AffiliateAccountId = affiliate.Id, AffiliateReferralCode = affiliate.ReferralCode,
                Status = OrderStatus.Delivered, FulfillmentStatus = FulfillmentStatus.Delivered, PaymentStatus = PaymentStatus.Collected,
                FinancialPostingStatus = FinancialPostingStatus.NotRequired };
            var item = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = product.Id, Quantity = 1,
                UnitPrice = order.Total, Total = order.Total, PaidAmount = order.Total, AffiliateCommissionEligible = true,
                AffiliateCommissionPolicyIdSnapshot = policy.Id, AffiliateCommissionRateSnapshot = .1m,
                AffiliateCommissionBaseAmountSnapshot = order.Total, AffiliateCommissionAmountSnapshot = amount,
                AffiliateCommissionSnapshotCapturedAtUtc = DateTime.UtcNow };
            order.Items.Add(item);
            var attribution = AffiliateAttribution.Create(f.Store, order.Id, affiliate.Id, affiliate.ReferralCode, AttributionSource.ReferralLink); attribution.Id = Guid.NewGuid();
            var earned = AffiliateCommissionLedger.CreateEarned(f.Store, attribution.Id, affiliate.Id, order.Id, item.Id, order.Total, .1m, null, "EGP");
            f.Db.AddRange(category, product, policyCategory, policy, order, attribution, earned, AffiliateCommissionLedger.CreateApproved(earned));
            await f.Db.SaveChangesAsync();
            var accounts = await f.Db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.TenantId == f.Store).ToDictionaryAsync(a => a.AccountCode);
            await Posting(f.Db).PostJournalAsync(new PostingRequest { TenantId = f.Store, JournalDate = DateTime.UtcNow,
                SourceType = "AffiliateCommissionEarned", SourceId = order.Id, Description = f.Label, UserId = f.Actor,
                IdempotencyKey = $"AFFILIATE_COMM_EARNED:{f.Store}:{order.Id}", Lines = [
                    PostingLine.Debit(accounts[AccountCodes.AffiliateMarketingExpense].Id, amount, "P512 internal fixture commission expense"),
                    PostingLine.Credit(accounts[AccountCodes.AffiliatePayable].Id, amount, "P512 internal fixture payable") ] });
            // Normal application services own withdrawal reservation and approval.
            var service = new AffiliatePayoutService(f.Db, NullLogger<AffiliatePayoutService>.Instance);
            f.Request = await service.RequestPayoutAsync(affiliate.Id, amount, f.Store);
            if (approved) await service.ApprovePayoutAsync(f.Request.Id, f.Store, f.Actor, f.Label);
            f.Db.ChangeTracker.Clear();
            if (browser)
            {
                // Materialize the fixture's upstream Earned/Approved projections through
                // existing consumers. The live Paid fact will use the API's real bus outbox.
                var mongoConfig = AffiliateP53ePrimarySupport.Config.GetSection("MongoDB");
                var mongo = new MongoClient(mongoConfig["ConnectionString"]).GetDatabase(mongoConfig["DatabaseName"]);
                var earnedEvent = new AffiliateCommissionEarnedEvent { TenantId = f.Store, StoreTenantId = f.Store,
                    OrderId = f.OrderId, OrderNumber = f.Label, AffiliateAccountId = affiliate.Id, ReferralCode = affiliate.ReferralCode,
                    Currency = "EGP", TotalCommission = amount, ItemCount = 1, Items = [new() { OrderItemId = item.Id,
                        ProductId = product.Id, ProductName = product.Name, PaidAmount = order.Total, CommissionRate = .1m, CommissionAmount = amount }] };
                var earnedContext = Mock.Of<ConsumeContext<AffiliateCommissionEarnedEvent>>(c => c.Message == earnedEvent && c.CancellationToken == CancellationToken.None);
                await new AffiliateCommissionEarnedProjectionConsumer(mongo, NullLogger<AffiliateCommissionEarnedProjectionConsumer>.Instance).Consume(earnedContext);
                var approvedEvent = new AffiliateCommissionApprovedEvent { TenantId = f.Store, StoreTenantId = f.Store,
                    OrderId = f.OrderId, OrderNumber = f.Label, AffiliateAccountId = affiliate.Id, Currency = "EGP", TotalApproved = amount, ItemCount = 1 };
                var approvedContext = Mock.Of<ConsumeContext<AffiliateCommissionApprovedEvent>>(c => c.Message == approvedEvent && c.CancellationToken == CancellationToken.None);
                await new AffiliateCommissionApprovedProjectionConsumer(mongo, NullLogger<AffiliateCommissionApprovedProjectionConsumer>.Instance).Consume(approvedContext);
                var path = Path.Combine(TestConfiguration.GetSolutionRoot(), ".ai/reports/p512-browser-fixture.json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { f.Store, f.Actor, ownerEmail = email, affiliateEmail = user.Email,
                    affiliateId = affiliate.Id, affiliateUserId = user.Id, f.OrderId, requestId = f.Request.Id, f.CashboxId,
                    f.EmptyCashboxId, f.SourceAccountId, f.CapitalizationId, f.Label, amount, currency = "EGP" }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return f;
        }
        internal static FinancialPostingService Posting(ApplicationDbContext db) => new(db, NullLogger<FinancialPostingService>.Instance);
        internal AffiliateSettlementOrchestrator Orchestrator(ApplicationDbContext db, bool failBeforeCommit = false)
        {
            var events = new Mock<IEventPublisher>();
            events.Setup(e => e.PublishAsync(It.IsAny<AffiliateCommissionPaidEvent>(), It.IsAny<CancellationToken>()))
                .Returns<AffiliateCommissionPaidEvent, CancellationToken>((e, _) => {
                    Assert.NotNull(db.Database.CurrentTransaction); Assert.Equal(Store, e.StoreTenantId);
                    Assert.Equal(Request.Id, e.SettlementReferenceId); Assert.Single(e.PaidItems);
                    if (failBeforeCommit) throw new InvalidOperationException("P512_INJECTED_OUTBOX_FAILURE_BEFORE_COMMIT");
                    return Task.CompletedTask;
                });
            return new(db, new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance), events.Object,
                Posting(db), new AccountResolver(db, NullLogger<AccountResolver>.Instance),
                new CashboxLedgerAccountService(db, NullLogger<CashboxLedgerAccountService>.Instance),
                NullLogger<AffiliateSettlementOrchestrator>.Instance, Options.Create(new AffiliateSettlementOptions()));
        }
        internal AdminManualPayoutCommand Command => new(CashboxId, Request.ApprovedAmount ?? Request.RequestedAmount,
            "EGP", AffiliateSettlementOrchestrator.DestinationFingerprint(Request), "Cash", true,
            AffiliateManualPayout.AttestationText, Label, "P512 INTERNAL TEST — NO EXTERNAL TRANSFER",
            AffiliateManualCashSource.Fingerprint(CashboxId, Db.TreasuryAccounts.IgnoreQueryFilters().Include(t => t.Account)
                .Single(t => t.AccountId == SourceAccountId)));
        internal async Task AssertNoPayout()
        {
            await using var db = Open();
            Assert.False(await db.AffiliateManualPayouts.AnyAsync(m => m.RequestId == Request.Id));
            Assert.False(await db.AffiliateBalanceLedger.IgnoreQueryFilters().AnyAsync(l => l.ReferenceId == Request.Id));
            Assert.False(await db.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(l => l.AffiliateAccountId == Affiliate.Id && l.EntryType == CommissionEntryType.Paid));
            Assert.False(await db.JournalEntries.IgnoreQueryFilters().AnyAsync(j => j.SourceId == Request.Id));
            Assert.Equal(500m, await db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.Id == SourceAccountId).Select(a => a.CurrentBalance).SingleAsync());
        }
        internal async Task AssertPaid()
        {
            await using var db = Open();
            var receipt = await db.AffiliateManualPayouts.SingleAsync(m => m.RequestId == Request.Id);
            var amount = Request.ApprovedAmount!.Value;
            Assert.Equal(Actor, receipt.AdministratorId); Assert.Equal("Owner", receipt.AdministratorRole);
            Assert.Equal(AffiliateManualPayout.AttestationText, receipt.Attestation); Assert.Equal(SourceAccountId, receipt.SourceAccountId);
            Assert.Equal(Affiliate.UserId, receipt.AffiliateUserId); Assert.Equal(Command.DestinationFingerprint, receipt.DestinationFingerprint);
            Assert.Single(await db.AffiliateBalanceLedger.IgnoreQueryFilters().Where(l => l.ReferenceId == Request.Id && l.Amount == -amount).ToListAsync());
            var journal = await db.JournalEntries.IgnoreQueryFilters().Include(j => j.LedgerEntries).SingleAsync(j => j.SourceId == Request.Id);
            Assert.Equal(amount, journal.LedgerEntries.Sum(l => l.CreditAmount)); Assert.Equal(amount, journal.LedgerEntries.Sum(l => l.DebitAmount));
            Assert.Equal(amount, journal.LedgerEntries.Single(l => l.AccountId == SourceAccountId).CreditAmount);
            Assert.Equal(Actor, journal.CreatedByUserId);
            Assert.Equal(500m - amount, await db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.Id == SourceAccountId).Select(a => a.CurrentBalance).SingleAsync());
            Assert.Single(await db.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.OrderId == OrderId && e.EntryType == CommissionEntryType.Paid).ToListAsync());
            Assert.Equal(PayoutRequestStatus.Paid, (await db.AffiliatePayoutRequests.SingleAsync(r => r.Id == Request.Id)).Status);
            Assert.False(await db.AffiliatePayoutTransferAllocations.IgnoreQueryFilters().AnyAsync(a => a.ReferenceId == Request.Id));
            Assert.False(await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().AnyAsync(e => e.StoreTenantId == Store));
            Assert.Single(await db.AuditLogs.IgnoreQueryFilters().Where(a => a.EntityId == receipt.Id.ToString() && a.ActionType == "AffiliateAdminManualPayout").ToListAsync());
        }
        public ValueTask DisposeAsync() => _ownedFixture is null ? Db.DisposeAsync() : DisposeGuardedAsync();
        private async ValueTask DisposeGuardedAsync()
        {
            if (Db is not null) await Db.DisposeAsync();
            if (_ownedFixture is not null) await _ownedFixture.DisposeAsync();
        }
    }

    [PrimaryAffiliateFact]
    public async Task ValidManualPayout_BalancedSelectedCashbox_ExactReplay_NoExternalEvidenceOrSimulator()
    {
        await using var f = await Fixture.Create();
        var first = await f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command);
        Assert.False(first.WasIdempotentSkip); Assert.NotNull(first.ManualPayoutId); await f.AssertPaid();
        await using var replayDb = f.Open();
        var replay = await f.Orchestrator(replayDb).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command);
        Assert.True(replay.WasIdempotentSkip); Assert.Equal(first.ManualPayoutId, replay.ManualPayoutId); Assert.Equal(first.ExecutedAtUtc, replay.ExecutedAtUtc);
        await f.AssertPaid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(replayDb).ExecuteAdminManualPayoutAsync(
            f.Request.Id, f.Store, f.Actor, f.Command with { InternalReference = "DIFFERENT RETRY PAYLOAD" }));
        var current = await replayDb.AffiliateAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == f.Affiliate.Id);
        current.UpdatePayoutWallet(current.PayoutWalletNumber!, current.PayoutWalletType!);
        await replayDb.SaveChangesAsync(); replayDb.ChangeTracker.Clear();
        Assert.True((await f.Orchestrator(replayDb).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command)).WasIdempotentSkip);
        var immutable = await replayDb.AffiliateManualPayouts.SingleAsync(m => m.RequestId == f.Request.Id);
        replayDb.Entry(immutable).Property(m => m.Notes).CurrentValue = "REWRITE MUST FAIL";
        await Assert.ThrowsAsync<InvalidOperationException>(() => replayDb.SaveChangesAsync()); replayDb.ChangeTracker.Clear();
        await f.AssertPaid();
    }

    [PrimaryAffiliateFact]
    public async Task InvalidAttestationFundingSourceDestinationStateCurrencyAndActor_LeaveNoEffects()
    {
        await using var f = await Fixture.Create();
        foreach (var command in new[] { f.Command with { PerformedManually = false }, f.Command with { Attestation = "reference only" },
            f.Command with { CashboxId = f.EmptyCashboxId }, f.Command with { CashboxId = Guid.NewGuid() },
            f.Command with { DestinationFingerprint = new string('0', 64) }, f.Command with { Amount = 151 },
            f.Command with { Currency = "USD" }, f.Command with { Method = "Wallet" } })
        {
            await using var db = f.Open();
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command));
            await f.AssertNoPayout();
        }
        await Assert.ThrowsAsync<SecurityException>(() => f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, Guid.NewGuid(), f.Command));
        await Assert.ThrowsAsync<SecurityException>(() => f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, Guid.NewGuid(), f.Actor, f.Command));
        await f.AssertNoPayout();
        await new AffiliatePayoutService(f.Db, NullLogger<AffiliatePayoutService>.Instance).RejectPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Label);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command));
        await f.AssertNoPayout();
    }

    [PrimaryAffiliateFact]
    public async Task FailureAfterPostingBeforeCommit_RollsEverythingBack_ThenRetrySucceeds()
    {
        await using var f = await Fixture.Create();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(f.Db, true).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command));
        Assert.Contains("P512_INJECTED_OUTBOX_FAILURE_BEFORE_COMMIT", error.Message); await f.AssertNoPayout();
        await using var db = f.Open();
        await f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command); await f.AssertPaid();
    }

    [PrimaryAffiliateFact]
    public async Task ConcurrentFirstExecutions_OneDebit_CompetitorCanReplay()
    {
        await using var f = await Fixture.Create();
        async Task<Exception?> Attempt()
        {
            await using var db = f.Open();
            try { await f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command); return null; }
            catch (Exception e) { return e; }
        }
        var outcomes = await Task.WhenAll(Attempt(), Attempt()); Assert.Contains(outcomes, x => x == null);
        await f.AssertPaid();
        await using var fresh = f.Open();
        Assert.True((await f.Orchestrator(fresh).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command)).WasIdempotentSkip);
    }

    [PrimaryAffiliateFact]
    public async Task ChangedDestination_BlocksManual_ProviderStillRequiresTrustedEvidence()
    {
        await using var f = await Fixture.Create();
        var affiliate = await f.Db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == f.Affiliate.Id);
        affiliate.UpdatePayoutWallet(affiliate.PayoutWalletNumber!, affiliate.PayoutWalletType!);
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command));
        Assert.Contains("WALLET_CHANGED", error.Message); await f.AssertNoPayout();
        await using var provider = await Fixture.Create();
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Orchestrator(provider.Db).ExecuteAffiliatePayoutAsync(
            provider.Affiliate.Id, provider.Store, 150, SettlementSource.WithdrawalEngine, "P512-ARBITRARY-REFERENCE",
            payoutRequestId: provider.Request.Id, paidByUserId: provider.Actor));
        Assert.StartsWith("AFFILIATE_PAYOUT_VERIFIED_EXTERNAL_EVIDENCE_REQUIRED", error.Message); await provider.AssertNoPayout();
    }

    [PrimaryAffiliateFact]
    public async Task IneligibleLineageAndNonExactFifoAllocation_CannotPay()
    {
        await using var synthetic = await Fixture.Create();
        var customer = new OnlineCustomer { Id = Guid.NewGuid(), TenantId = synthetic.Store, Email = $"p512-{Guid.NewGuid():N}@example.invalid", CreatedAtUtc = DateTime.UtcNow };
        var product = await synthetic.Db.OrderItems.IgnoreQueryFilters().Where(i => i.OrderId == synthetic.OrderId).Select(i => i.ProductId).SingleAsync();
        var link = AffiliatePromotionLink.Create(synthetic.Store, synthetic.Affiliate.UserId, product, Guid.NewGuid().ToString("N")[..10], synthetic.Affiliate.Id); link.Id = Guid.NewGuid();
        var run = new SyntheticQaRun { Id = Guid.NewGuid(), TenantId = synthetic.Store, ProductId = product, CustomerId = customer.Id,
            PromotionLinkId = link.Id, CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
        synthetic.Db.AddRange(customer, link, run, new SyntheticQaOrder { OrderId = synthetic.OrderId, TenantId = synthetic.Store, RunId = run.Id, EnrolledAtUtc = DateTime.UtcNow });
        synthetic.Db.Add(new SyntheticQaCommissionPosting { Id = Guid.NewGuid(), OrderId = synthetic.OrderId,
            Amount = 150m, Currency = "EGP", PostedAtUtc = DateTime.UtcNow });
        await synthetic.Db.SaveChangesAsync(); synthetic.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => synthetic.Orchestrator(synthetic.Db).ExecuteAdminManualPayoutAsync(
            synthetic.Request.Id, synthetic.Store, synthetic.Actor, synthetic.Command));
        await synthetic.AssertNoPayout();
        await using var exact = await Fixture.Create(amount: 300m);
        var requests = new AffiliatePayoutService(exact.Db, NullLogger<AffiliatePayoutService>.Instance);
        await requests.RejectPayoutAsync(exact.Request.Id, exact.Store, exact.Actor, exact.Label);
        exact.Request = await requests.RequestPayoutAsync(exact.Affiliate.Id, 150m, exact.Store);
        await requests.ApprovePayoutAsync(exact.Request.Id, exact.Store, exact.Actor, exact.Label); exact.Db.ChangeTracker.Clear();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => exact.Orchestrator(exact.Db).ExecuteAdminManualPayoutAsync(
            exact.Request.Id, exact.Store, exact.Actor, exact.Command));
        Assert.StartsWith("AFFILIATE_PAYOUT_AMOUNT_NOT_EXACTLY_PAYABLE", error.Message); await exact.AssertNoPayout();
    }

    [PrimaryAffiliateFact]
    public async Task PendingAndNonAdminCannotPay_ManualEnumCannotBypassPublicProviderEntry()
    {
        await using var f = await Fixture.Create(approved: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(f.Db).ExecuteAffiliatePayoutAsync(f.Affiliate.Id, f.Store, 150m,
            SettlementSource.AdminManualPayout, "NOT-EVIDENCE", payoutRequestId: f.Request.Id, paidByUserId: f.Actor));
        var user = await f.Db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == f.Actor);
        user.Role = UserRole.Cashier; await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SecurityException>(() => f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command));
        await f.AssertNoPayout();
    }

    [PrimaryAffiliateFact]
    public async Task PrepareFreshBrowserJourney_RetainPendingRequestWithoutPayout()
    {
        if (Environment.GetEnvironmentVariable("P512_PREPARE_BROWSER") != "1") return;
        await using var f = await Fixture.Create(browser: true, approved: false);
        await f.AssertNoPayout();
    }
}
