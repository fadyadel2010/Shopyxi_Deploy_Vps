using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Infrastructure.Consumers.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Pure regressions: no test host, database, broker, provider or alternate environment.</summary>
public sealed class AffiliateP52RetryAndProjectionTests
{
    private static Exception PostgreSql(string state, int wrappers)
    {
        Exception error = new PostgresException("P5.2_TEST_NO_REAL_MONEY", "ERROR", "ERROR", state);
        if (wrappers > 0) error = new DbUpdateException("Save failed", error);
        if (wrappers > 1) error = new InvalidOperationException("Npgsql non-retrying strategy wrapper", error);
        return error;
    }

    [Theory]
    [InlineData("40001", 0, true)]
    [InlineData("40001", 1, true)]
    [InlineData("40001", 2, true)]
    [InlineData("40P01", 0, true)]
    [InlineData("40P01", 2, true)]
    [InlineData("23505", 2, false)]
    [InlineData("08006", 2, false)]
    [InlineData("57014", 2, false)]
    [InlineData("XX000", 2, false)]
    public void OnlyTransactionAbortingPostgresStatesAreRetried(string state, int wrappers, bool expected) =>
        Assert.Equal(expected, AffiliateFinancialRetry.IsRolledBackTransaction(PostgreSql(state, wrappers)));

    [Fact]
    public async Task WrappedSerializationThenDeadlock_RetriesWholeDelegateWithinBudget()
    {
        var attempts = new List<int>();
        var result = await AffiliateFinancialRetry.ExecuteAsync(number =>
        {
            attempts.Add(number);
            if (number < 3) throw PostgreSql(number == 1 ? "40001" : "40P01", 2);
            return Task.FromResult("authoritative fresh-scope result");
        });
        Assert.Equal([1, 2, 3], attempts);
        Assert.Equal("authoritative fresh-scope result", result);
    }

    [Fact]
    public async Task Exhaustion_IsControlled409AndPreservesCause()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AffiliateFinancialRetry.ExecuteAsync<int>(_ =>
        { calls++; throw PostgreSql("40001", 2); }));
        Assert.Equal(AffiliateFinancialRetry.MaximumAttempts, calls);
        Assert.NotNull(error.InnerException);
        AssertContract(error, "AFFILIATE_CONCURRENCY_CONFLICT");
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    [InlineData("domain")]
    [InlineData("unique")]
    [InlineData("unknown-wrapper")]
    public async Task UncertainOutcomesAndUnrelatedFailures_AreNeverBlindlyRetried(string kind)
    {
        Exception error = kind switch
        {
            "timeout" => new TimeoutException("post-commit outcome unknown"),
            "cancellation" => new OperationCanceledException(),
            "domain" => new InvalidOperationException("AFFILIATE_PAYOUT_REQUEST_STATUS_INVALID"),
            "unique" => PostgreSql("23505", 2),
            _ => new ApplicationException("unrelated failure", PostgreSql("40001", 2))
        };
        var calls = 0;
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => AffiliateFinancialRetry.ExecuteAsync<int>(_ =>
        { calls++; throw error; }));
        Assert.Same(error, actual);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("Approved", "approve", true)]
    [InlineData("Rejected", "reject", true)]
    [InlineData("Rejected", "approve", true)]
    [InlineData("Approved", "reject", false)] // Existing supported state machine MUST remain.
    [InlineData("Paid", "reject", true)]
    [InlineData("Paid", "approve", true)]
    public void TerminalTransitions_UseExistingConflictContractWithoutMutatingAuthority(string status, string action, bool conflict)
    {
        var admin = Guid.NewGuid();
        var request = AffiliatePayoutRequest.Create(Guid.NewGuid(), Guid.NewGuid(), 150, 150, "EGP", "01000000000", "VodafoneCash", null);
        if (status is "Approved" or "Paid") request.Approve(admin);
        if (status == "Rejected") request.Reject(admin);
        if (status == "Paid") request.MarkPaid(admin, "P5_SIMULATED_NO_MONEY");
        var before = (request.Status, request.RemainingAmount, request.ApprovedAtUtc, request.RejectedAtUtc, request.PaidAtUtc);
        void Apply() { if (action == "approve") request.Approve(admin); else request.Reject(admin); }
        if (!conflict) { Apply(); Assert.Equal(PayoutRequestStatus.Rejected, request.Status); return; }
        AssertContract(Assert.Throws<InvalidOperationException>(Apply), "AFFILIATE_PAYOUT_REQUEST_STATUS_INVALID");
        Assert.Equal(before, (request.Status, request.RemainingAmount, request.ApprovedAtUtc, request.RejectedAtUtc, request.PaidAtUtc));
    }

    private static void AssertContract(Exception error, string code)
    {
        var type = typeof(AdminAffiliateController).Assembly.GetType("PointOfSale.API.Controllers.AffiliateFinancialErrorContract")!;
        var contract = type.GetMethod("From", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [error])!;
        Assert.Equal(409, contract.GetType().GetProperty("StatusCode")!.GetValue(contract));
        Assert.Equal(code, contract.GetType().GetProperty("Code")!.GetValue(contract));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("placeholder")]
    [InlineData("affiliate")]
    [InlineData("store")]
    [InlineData("currency")]
    [InlineData("amount")]
    [InlineData("item-count")]
    [InlineData("unapproved")]
    [InlineData("marker-without-effect")]
    public void MissingOrCorruptUpstream_IsVisibleFailureNotSuccessfulZero(string kind)
    {
        var (order, evt) = Canonical();
        switch (kind)
        {
            case "missing": order = null!; break;
            case "placeholder": order.Items.Clear(); order.TotalCommission = 0; break;
            case "affiliate": order.AffiliateAccountId = Guid.NewGuid(); break;
            case "store": order.TenantId = Guid.NewGuid(); break;
            case "currency": order.Currency = "USD"; break;
            case "amount": evt.TotalPaid++; break;
            case "item-count": evt.ItemCount++; break;
            case "unapproved": order.Items[0].SettlementStatus = "Earned"; break;
            case "marker-without-effect": order.ProcessedEventIds.Add(evt.EventId.ToString()); break;
        }
        Assert.Contains("AFFILIATE_PAID_PROJECTION_UPSTREAM_INVALID", Assert.Throws<InvalidOperationException>(() =>
            AffiliateCommissionPaidProjectionConsumer.ValidatePaidUpstream(order, evt, evt.EventId.ToString())).Message);
    }

    [Fact]
    public void CanonicalApprovedAndMatchingPaidReplay_HaveValidLineage()
    {
        var (order, evt) = Canonical();
        AffiliateCommissionPaidProjectionConsumer.ValidatePaidUpstream(order, evt, evt.EventId.ToString());
        order.Items[0].SettlementStatus = "Paid"; order.TotalPaid = evt.TotalPaid;
        order.ProcessedEventIds.Add(evt.EventId.ToString());
        AffiliateCommissionPaidProjectionConsumer.ValidatePaidUpstream(order, evt, evt.EventId.ToString());
        Assert.Equal(150, order.TotalPaid);
        Assert.Single(order.ProcessedEventIds);
    }

    private static (AffiliateOrderDocument Order, AffiliateCommissionPaidEvent Event) Canonical()
    {
        var evt = new AffiliateCommissionPaidEvent { StoreTenantId = Guid.NewGuid(), AffiliateAccountId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(), TotalPaid = 150, ItemCount = 1, Currency = "EGP" };
        return (new AffiliateOrderDocument { TenantId = evt.StoreTenantId, AffiliateAccountId = evt.AffiliateAccountId,
            OrderId = evt.OrderId, Currency = evt.Currency, TotalCommission = 150, TotalApproved = 150,
            Items = [new AffiliateOrderItemSubDocument { OrderItemId = Guid.NewGuid(), SettlementStatus = "Approved", CommissionAmount = 150 }] }, evt);
    }
}
