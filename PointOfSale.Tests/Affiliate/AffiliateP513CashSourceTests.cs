using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Tests.Infrastructure;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Tests.Affiliate;

/// <summary>In-memory source validation only; never executes a payout or connects to PostgreSQL.</summary>
public sealed class AffiliateP513CashSourceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ManualAccounting_UsesSelectedSource_OneActualPayoutMovement(int orders)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = Guid.NewGuid(); var actor = Guid.NewGuid();
        var source = Account.Create(store, "1010-SELECTED", "Selected", AccountType.Asset); source.Id = Guid.NewGuid();
        var payable = Account.Create(store, "2105", "Payable", AccountType.Liability); payable.Id = Guid.NewGuid();
        var resolver = new Mock<IAccountResolver>(MockBehavior.Strict);
        resolver.Setup(x => x.GetByCodesAsync(store, It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(new Dictionary<string, Account> { ["2105"] = payable });
        resolver.Setup(x => x.GetByCodeAsync(store, source.AccountCode, default)).ReturnsAsync(source);
        resolver.Setup(x => x.GetByCodeAsync(store, "2105", default)).ReturnsAsync(payable);
        var cashboxes = new Mock<ICashboxLedgerAccountService>(MockBehavior.Strict);
        var posting = new Mock<IFinancialPostingService>(MockBehavior.Strict);
        var requests = new List<PostingRequest>();
        posting.Setup(x => x.PostJournalAsync(It.IsAny<PostingRequest>(), default))
            .Callback<PostingRequest, CancellationToken>((r, _) => requests.Add(r)).ReturnsAsync(new JournalEntry());
        var orchestrator = new AffiliateSettlementOrchestrator(db, null!, null!, posting.Object, resolver.Object, cashboxes.Object,
            NullLogger<AffiliateSettlementOrchestrator>.Instance, Options.Create(new AffiliateSettlementOptions()));
        var plan = Enumerable.Range(0, orders).ToDictionary(_ => Guid.NewGuid(), _ => (Total: 150m / orders, Count: 1, Currency: "EGP"));
        var post = typeof(AffiliateSettlementOrchestrator).GetMethod("PostAffiliatePaidAccountingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)post.Invoke(orchestrator, [store, Guid.NewGuid(), Guid.NewGuid(), plan, CancellationToken.None, source, actor])!;
        Assert.Single(requests);
        Assert.Equal(150m, requests.Sum(r => r.Lines.Sum(l => l.CreditAmount)));
        Assert.All(requests, r => {
            Assert.Equal(actor, r.UserId);
            Assert.Equal(150m, Assert.Single(r.Lines.Where(l => l.AccountId == payable.Id)).DebitAmount);
            Assert.Equal(150m, Assert.Single(r.Lines.Where(l => l.AccountId == source.Id)).CreditAmount);
        });
        cashboxes.VerifyNoOtherCalls();
        Assert.Empty(await db.JournalEntries.IgnoreQueryFilters().ToListAsync()); // Mock capture only, no money posted.
    }

    [Fact]
    public void LegacyReceiptFingerprint_RemainsStable_WithoutExecutingReplay()
    {
        var command = new AdminManualPayoutCommand(Guid.Parse("73d01e16-a07d-49c4-9642-d1d77a89f10d"), 150m, "EGP",
            "0730AF7A36CA7FC569E18F93DFCA1BD4FC8411E97077E2A1DFC78681309986F1", "Cash", true,
            AffiliateManualPayout.AttestationText, "P512-INTERNAL-BOOK-TEST", "P512 internal development acceptance — NO REAL EXTERNAL TRANSFER");
        var actor = new User { Id = Guid.Parse("c30d0840-15a1-4138-b39b-3db26a274548") };
        var type = typeof(AffiliateSettlementOrchestrator).GetNestedType("ManualExecution", BindingFlags.NonPublic)!;
        object Execution(AdminManualPayoutCommand c) => Activator.CreateInstance(type,
            [actor, c, Guid.NewGuid(), DateTime.UtcNow, null])!;
        var fingerprint = typeof(AffiliateSettlementOrchestrator).GetMethod("CommandFingerprint", BindingFlags.Static | BindingFlags.NonPublic)!;
        var legacy = fingerprint.Invoke(null, [Execution(command)]);
        Assert.Equal("5AC6C1FE8572DF68E8BAAF333F507ECC5E1FB9FF2D1B82AA6CDCB2AC053AE85E", legacy);
        Assert.NotEqual(legacy, fingerprint.Invoke(null, [Execution(command with { SourceFingerprint = new string('A', 64) })]));
    }

    [Fact]
    public void MigrationSql_IsAdditiveAndLimited_WithoutOpeningDatabase()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=192.168.1.5;Database=PosEcommerceDb_Baseline;Username=unused").Options);
        var script = db.GetService<IMigrator>().GenerateScript("20261023150000_AffiliateAttributionPromotionCodeLength",
            "20261026140345_AffiliateAdminManualPayout");
        Assert.Contains("CREATE TABLE \"AffiliateManualPayouts\"", script);
        Assert.Contains("BEFORE UPDATE OR DELETE", script);
        Assert.DoesNotContain("AffiliateLandingPageDesigns", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("TRUNCATE", script);
        Assert.DoesNotContain("UPDATE \"", script);
        Assert.Throws<NotSupportedException>(() => db.GetService<IMigrator>().GenerateScript(
            "20261026140345_AffiliateAdminManualPayout", "20261023150000_AffiliateAttributionPromotionCodeLength"));
        // This is a SQL-generation assertion, not an evidence regeneration command.
        // Never overwrite the retained P513 migration artifact during a test run.
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("inactive-cashbox")]
    [InlineData("cross-store")]
    [InlineData("inactive-treasury")]
    [InlineData("inactive-ledger")]
    [InlineData("clearing")]
    [InlineData("liability")]
    [InlineData("negative-allowed")]
    [InlineData("generic")]
    [InlineData("unmapped")]
    [InlineData("shared-account")]
    [InlineData("duplicate-mapping")]
    public async Task Resolver_RequiresExclusiveCanonicalSameStorePhysicalSource(string scenario)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = Guid.NewGuid();
        var cashbox = new Cashbox { Id = Guid.NewGuid(), TenantId = store, Name = "In-memory only", IsActive = true };
        var parent = Account.Create(store, AccountCodes.CashboxMain, "Cash", AccountType.Asset);
        parent.Id = Guid.NewGuid();
        var account = Account.Create(store, $"1010-{cashbox.Id.ToString("N")[24..].ToUpperInvariant()}",
            "Dedicated", AccountType.Asset, parent.Id, preventNegative: true);
        account.Id = Guid.NewGuid();
        var treasury = TreasuryAccount.CreateCashbox(store, account.Id, "Dedicated");
        treasury.Id = Guid.NewGuid();
        treasury.ReferenceCode = cashbox.Id.ToString("N"); treasury.Account = account;
        switch (scenario)
        {
            case "inactive-cashbox": cashbox.IsActive = false; break;
            case "cross-store": account.TenantId = Guid.NewGuid(); break;
            case "inactive-treasury": treasury.IsActive = false; break;
            case "inactive-ledger": account.IsActive = false; break;
            case "clearing": account.IsClearing = true; break;
            case "liability": account.SetAccountType(AccountType.Liability); break;
            case "negative-allowed": account.PreventNegative = false; break;
            case "generic": account.SetAccountCode("1010"); break;
            case "unmapped": treasury.ReferenceCode = null; break;
            case "shared-account": db.Add(TreasuryAccount.CreateBank(store, account.Id, "Other source")); break;
            case "duplicate-mapping":
                var duplicate = TreasuryAccount.CreateCashbox(store, parent.Id, "Duplicate");
                duplicate.ReferenceCode = treasury.ReferenceCode; db.Add(duplicate); break;
        }
        db.AddRange(cashbox, parent, account, treasury); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var resolved = await AffiliateManualCashSource.ResolveAsync(db, store, cashbox.Id);
        if (scenario == "valid") Assert.Equal(treasury.Id, resolved!.Id); else Assert.Null(resolved);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task MissingOrChangedReviewedSource_FailsBeforeAnyFinancialEffects()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = Guid.NewGuid(); var cashbox = new Cashbox { Id = Guid.NewGuid(), TenantId = store, Name = "Memory", IsActive = true };
        var parent = Account.Create(store, "1010", "Cash", AccountType.Asset);
        parent.Id = Guid.NewGuid();
        var account = Account.Create(store, $"1010-{cashbox.Id.ToString("N")[24..].ToUpperInvariant()}", "Dedicated",
            AccountType.Asset, parent.Id, preventNegative: true);
        account.Id = Guid.NewGuid();
        var treasury = TreasuryAccount.CreateCashbox(store, account.Id, "Memory"); treasury.ReferenceCode = cashbox.Id.ToString("N"); treasury.Account = account;
        treasury.Id = Guid.NewGuid();
        db.AddRange(cashbox, parent, account, treasury); await db.SaveChangesAsync();
        var original = AffiliateManualCashSource.Fingerprint(cashbox.Id, treasury);
        treasury.Id = Guid.NewGuid();
        var changed = AffiliateManualCashSource.Fingerprint(cashbox.Id, treasury);
        Assert.NotEqual(original, changed);
        db.ChangeTracker.Clear();
        var orchestrator = new AffiliateSettlementOrchestrator(db, null!, null!, null!, null!, null!,
            NullLogger<AffiliateSettlementOrchestrator>.Instance, Options.Create(new AffiliateSettlementOptions()));
        var load = typeof(AffiliateSettlementOrchestrator).GetMethod("LoadManualFundingAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var fingerprint in new string?[] { null, changed })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)load.Invoke(orchestrator,
                [store, cashbox.Id, 150m, fingerprint, CancellationToken.None])!);
            Assert.StartsWith("AFFILIATE_MANUAL_REQUEST_CHANGED:", error.Message);
        }
        Assert.Empty(await db.JournalEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AffiliateManualPayouts.ToListAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
