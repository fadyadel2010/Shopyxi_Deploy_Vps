using Microsoft.EntityFrameworkCore;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Infrastructure.Fulfillment;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Affiliate;

namespace PointOfSale.Tests.Architecture;

/// <summary>
/// PASS AF-PA5.5 - Commission Approval, Withdrawable Balance, and Financial Recovery freeze.
/// </summary>
public sealed class AffiliateCommissionApprovalWithdrawableBalanceCertificationTests
{
    [Fact]
    public void CommissionApproval_MustBeBackendDeliveryFinancialLifecycleOnly()
    {
        var service = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");
        var consumer = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateSettlementConsumers.cs");
        var approvalMethod = Slice(service, "public async Task ApproveCommissionForDeliveredOrderAsync", "private async Task<Order> LoadAffiliateApprovalOrderAuthorityAsync");
        var reconciliationMethod = Slice(service, "private async Task ReconcileDeliveredCommissionApprovalsAsync", "private async Task<bool> ShouldRetryMissingEarnedCommissionForRefundAsync");

        Assert.Contains("IConsumer<OrderDeliveredEvent>", consumer, StringComparison.Ordinal);
        Assert.Contains("IConsumer<FulfillmentDeliveredEvent>", consumer, StringComparison.Ordinal);
        Assert.Contains("EnsureAffiliateDeliveryApprovalAuthority", approvalMethod, StringComparison.Ordinal);
        Assert.Contains("BeginTransactionAsync(IsolationLevel.Serializable", approvalMethod, StringComparison.Ordinal);
        Assert.Contains("AffiliateCommissionLedger.CreateApproved(earned)", approvalMethod, StringComparison.Ordinal);

        Assert.Contains("EnsureAffiliateDeliveryApprovalAuthority", reconciliationMethod, StringComparison.Ordinal);
        Assert.Contains("RecordCommissionDeliveredOrderReconciliation", reconciliationMethod, StringComparison.Ordinal);
        Assert.Contains("deliveryReference: order.DeliveryReference", reconciliationMethod, StringComparison.Ordinal);
        Assert.Contains("proofOfDeliveryUrl: order.ProofOfDeliveryUrl", reconciliationMethod, StringComparison.Ordinal);
        Assert.Contains("AffiliateCommissionLedger.CreateApproved(earned)", reconciliationMethod, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualDeliveryEvidence_MustBePersistedForLaterFinancialRevalidation()
    {
        await using var f = new AffiliatePhase4CommissionLifecycleTests.Fixture(manualDispatched: true);
        await f.Service.RecordCommissionForOrderAsync(f.Order.Id);
        var fulfillment = new OrderFulfillmentService(f.Db, f.Events.Object, NullLogger<OrderFulfillmentService>.Instance);
        var result = await fulfillment.MarkDeliveredAsync(f.Order.Id, FulfillmentRequestSource.Manual,
            "  P53A-NO-MONEY  ", "  https://example.invalid/p53a-proof  ");
        Assert.True(result.Success, result.ErrorMessage);
        await using (var reload = f.CreateReadContext())
        {
            var persisted = await reload.Orders.SingleAsync(o => o.Id == f.Order.Id);
            Assert.Equal("P53A-NO-MONEY", persisted.DeliveryReference);
            Assert.Equal("https://example.invalid/p53a-proof", persisted.ProofOfDeliveryUrl);
        }
        f.Db.ChangeTracker.Clear();
        var conflict = await fulfillment.MarkDeliveredAsync(f.Order.Id, FulfillmentRequestSource.Manual,
            "substituted", "https://example.invalid/substituted");
        Assert.False(conflict.Success);
        Assert.Contains("MANUAL_DELIVERY_EVIDENCE_CONFLICT", conflict.ErrorMessage);

        Task Approve(string reference) => f.Service.ApproveCommissionForDeliveredOrderAsync(f.Order.Id,
            f.Order.TenantId, DateTime.UtcNow, nameof(FulfillmentDeliveredEvent), 1,
            deliveryReference: reference, proofOfDeliveryUrl: "https://example.invalid/p53a-proof");
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => Approve("substituted"));
        Assert.Contains("AFFILIATE_MANUAL_DELIVERY_EVIDENCE_REQUIRED", mismatch.Message);
        await Assert.ThrowsAsync<AffiliateCommissionDeferredException>(() => Approve("P53A-NO-MONEY"));
        Assert.DoesNotContain(await f.Ledger(), e => e.EntryType == CommissionEntryType.Approved);

        var actor = new User { Id = Guid.NewGuid(), TenantId = f.Order.TenantId, Username = "P53A-test",
            Email = "p53a@example.invalid", Role = UserRole.Admin, IsActive = true };
        f.Db.Users.Add(actor); await f.Db.SaveChangesAsync();
        Assert.True((await fulfillment.MarkCompletedAsync(f.Order.Id, FulfillmentRequestSource.Manual, actor.Id.ToString())).Success);
        f.Db.ChangeTracker.Clear();
        await Approve("P53A-NO-MONEY");
        await Approve("P53A-NO-MONEY");
        Assert.Single((await f.Ledger()).Where(e => e.EntryType == CommissionEntryType.Approved));
    }

    [Fact]
    public void PostgresApprovalLockQuery_MustProjectXminConcurrencyToken()
    {
        var service = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");
        var authorityLoader = CSharpSyntaxTree.ParseText(service).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "LoadAffiliateApprovalOrderAuthorityAsync").ToFullString();

        Assert.Contains("SELECT *, xmin", authorityLoader, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", authorityLoader, StringComparison.Ordinal);
        Assert.Contains("FromSqlInterpolated", authorityLoader, StringComparison.Ordinal);
        using var db = new PointOfSale.Infrastructure.Data.ApplicationDbContext(
            new DbContextOptionsBuilder<PointOfSale.Infrastructure.Data.ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only;Username=model_only").Options);
        var token = db.Model.FindEntityType(typeof(Order))!.FindProperty("xmin")!;
        Assert.True(token.IsConcurrencyToken);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate, token.ValueGenerated);
    }

    [Fact]
    public void DeliveryBeforeCodCollection_MustDeferToAuthoritativeAccountingReconciliation()
    {
        var service = CertificationSource.ReadRepoFile(
            "PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");
        var consumer = CertificationSource.ReadRepoFile(
            "PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateSettlementConsumers.cs");

        Assert.Contains("AFFILIATE_DELIVERY_PAYMENT_AUTHORITY_NOT_CONFIRMED", service, StringComparison.Ordinal);
        Assert.Contains("AffiliateCommissionDeferredException", service, StringComparison.Ordinal);
        Assert.Contains("catch (AffiliateCommissionDeferredException ex)", consumer, StringComparison.Ordinal);
        Assert.Contains("Deferred to payment/accounting reconciliation", consumer, StringComparison.Ordinal);
    }

    [Fact]
    public void CommissionReversalProjection_MustRemainReadableAcrossLegacyGuidRepresentations()
    {
        var document = CertificationSource.ReadRepoFile(
            "PointOfSale.Infrastructure/MongoDB/Documents/OrderDocument.cs");
        var consumer = CertificationSource.ReadRepoFile(
            "PointOfSale.Infrastructure/Consumers/OrderProjectionConsumer.cs");

        Assert.Contains("BsonSerializer(typeof(NullableGuidStringSerializer))", document, StringComparison.Ordinal);
        Assert.Contains("{ \"LastEventId\", @event.EventId.ToString(\"D\") }", consumer, StringComparison.Ordinal);
        Assert.Contains("{ \"LastAffiliateCommissionEventId\", @event.EventId.ToString(\"D\") }", consumer, StringComparison.Ordinal);
        Assert.DoesNotContain("new BsonBinaryData(@event.EventId", consumer, StringComparison.Ordinal);
    }

    [Fact]
    public void WithdrawableBalance_MustBeginAtNetApprovedLedgerOnly()
    {
        var payoutService = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliatePayoutService.cs");
        var balanceMethod = Slice(payoutService, "private async Task<AffiliateAvailableBalanceResult> GetAvailableBalanceCoreAsync", "private async Task<List<string>> ResolveAuthoritativeBalanceCurrenciesAsync");

        var eligibility = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateCommissionEligibility.cs");
        Assert.Contains("AffiliateCommissionEligibility.LoadAsync", balanceMethod, StringComparison.Ordinal);
        Assert.Contains("CommissionEntryType.Approved", eligibility, StringComparison.Ordinal);
        Assert.Contains("CommissionEntryType.Reversed", eligibility, StringComparison.Ordinal);
        Assert.Contains("var netApproved", balanceMethod, StringComparison.Ordinal);
        Assert.Contains("var netApproved = eligibility.NetApproved", balanceMethod, StringComparison.Ordinal);
        Assert.Contains("Math.Max(0m,", eligibility, StringComparison.Ordinal);
        Assert.Contains("SyntheticQaOrders", eligibility, StringComparison.Ordinal);
        Assert.Contains("OriginalEntryId", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("CommissionEntryType.Earned", balanceMethod, StringComparison.Ordinal);
        Assert.Contains("Available = NetApproved + PayoutReductions + Withdrawals (negative) - Pending", balanceMethod, StringComparison.Ordinal);
        Assert.Contains("RawAvailable", balanceMethod, StringComparison.Ordinal);
        Assert.Contains("AvailableForWithdrawal", balanceMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryAndReplay_MustBeAppendOnlyAndIdempotent()
    {
        var commissionLedger = CertificationSource.ReadRepoFile("PointOfSale.Core/Entities/Affiliate/AffiliateCommissionLedger.cs");
        var commissionConfig = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Data/Configurations/Affiliate/AffiliateCommissionLedgerConfiguration.cs");
        var balanceLedger = CertificationSource.ReadRepoFile("PointOfSale.Core/Entities/Affiliate/AffiliateBalanceLedger.cs");
        var balanceConfig = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Data/Configurations/Affiliate/AffiliateBalanceLedgerConfiguration.cs");
        var service = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");

        Assert.Contains("Append-only commission ledger entry", commissionLedger, StringComparison.Ordinal);
        Assert.Contains("APPEND ONLY", commissionLedger, StringComparison.Ordinal);
        Assert.Contains("builder.HasIndex(e => e.IdempotencyKey)", commissionConfig, StringComparison.Ordinal);
        Assert.Contains("IsUnique()", commissionConfig, StringComparison.Ordinal);

        Assert.Contains("Account-level financial movements ledger", balanceLedger, StringComparison.Ordinal);
        Assert.Contains("APPEND ONLY", balanceLedger, StringComparison.Ordinal);
        Assert.Contains("builder.HasIndex(e => e.IdempotencyKey)", balanceConfig, StringComparison.Ordinal);
        Assert.Contains("IsUnique()", balanceConfig, StringComparison.Ordinal);

        Assert.Contains("IsUniqueViolation(ex)", service, StringComparison.Ordinal);
        Assert.Contains("RecordIdempotentSkip", service, StringComparison.Ordinal);
    }

    [Fact]
    public void EarnedAccountingReplay_MustAcceptEquivalentJournalAcrossProcessingDates()
    {
        var consumer = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateAccountingConsumer.cs");
        var earnedEquivalence = Slice(
            consumer,
            "EnsureEquivalentEarnedJournal(",
            "public class AffiliateCommissionReversedAccountingConsumer");

        Assert.Contains("GetJournalByIdempotencyKeyAsync", consumer, StringComparison.Ordinal);
        Assert.Contains("existingJournal.SourceId != request.SourceId", earnedEquivalence, StringComparison.Ordinal);
        Assert.Contains("!existingLines.SequenceEqual(requestedLines)", earnedEquivalence, StringComparison.Ordinal);
        Assert.DoesNotContain("existingJournal.JournalDate != request.JournalDate", earnedEquivalence, StringComparison.Ordinal);
    }

    [Fact]
    public void FrontendAndCaches_MustNotOwnApprovalWithdrawableBalanceOrLedgerTotals()
    {
        var frontendFiles = Directory.EnumerateFiles(FindRepoRoot(), "*.*", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}PointOfSale.Web{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                           path.Contains($"{Path.DirectorySeparatorChar}PointOfSale.Storefront{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                           path.Contains($"{Path.DirectorySeparatorChar}PointOfSale.CommerceHub{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var combinedFrontend = string.Join('\n', frontendFiles.Select(File.ReadAllText));

        Assert.DoesNotContain("AffiliateCommissionLedger.CreateApproved", combinedFrontend, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateApproved(earned", combinedFrontend, StringComparison.Ordinal);
        Assert.DoesNotContain("CommissionEntryType.Approved", combinedFrontend, StringComparison.Ordinal);
        Assert.DoesNotContain("AffiliateBalanceLedger", combinedFrontend, StringComparison.Ordinal);
        Assert.DoesNotContain("RawAvailable =", combinedFrontend, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PointOfSale.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be found.");
    }
}
