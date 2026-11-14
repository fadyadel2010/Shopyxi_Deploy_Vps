using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Payments;
using PointOfSale.Infrastructure.Observability;

namespace PointOfSale.Tests.Payments;

// Pure production predicates: no host, provider, journal posting or money flow.
// Paid remains the public legacy name for numeric value 2; removing redundant
// alias patterns must not change classification, COD or reservation boundaries.
public sealed class PaymentAliasCompatibilityBehaviorTests
{
    private static T[] Values<T>() where T : struct, Enum => Enum.GetValues<T>()
        .Concat([Cast<T>(-1), Cast<T>(int.MaxValue)]).Distinct().ToArray();
    private static T Cast<T>(int value) where T : struct, Enum => (T)Enum.ToObject(typeof(T), value);
    private static T?[] NullableValues<T>() where T : struct, Enum => Values<T>().Select(value => (T?)value).Prepend(null).ToArray();

    [Fact]
    public void LegacyPaidNameRetainsNumericParsingAndSerializationCompatibility()
    {
        var legacyField = typeof(PaymentStatus).GetField("Paid");
        Assert.NotNull(legacyField);
        Assert.Equal(2, legacyField.GetRawConstantValue());
        Assert.Equal(2, (int)PaymentStatus.Collected);
        var legacy = Enum.Parse<PaymentStatus>("Paid");
        Assert.Equal(PaymentStatus.Collected, legacy);
        Assert.Equal("2", System.Text.Json.JsonSerializer.Serialize(legacy));
        Assert.Equal(PaymentStatus.Collected, Enum.Parse<PaymentStatus>("2"));
    }

    [Fact]
    public void PaidStateClassificationCoversAllDeclaredStatusesAndUnknownValues()
    {
        foreach (var status in Values<PaymentStatus>().Concat(Enumerable.Range(-2, 20).Select(Cast<PaymentStatus>)).Distinct())
            Assert.Equal((int)status is 2 or 9, PaymentStatusAuthority.IsPaidState(status));
    }

    [Fact]
    public void OperationalRulesPreserveCollectionAccountingCodAndCompletionAcrossAllStatuses()
    {
        foreach (var status in Values<PaymentStatus>())
        foreach (var posting in Values<FinancialPostingStatus>())
        foreach (var total in new[] { -1m, 0m, 100m })
        foreach (var method in new[] { null, "", "STRIPE", "COD", "cod", "CASHONDELIVERY", "CASH_ON_DELIVERY", "CASH-ON-DELIVERY", " COD " })
        {
            var order = new Order { Total = total, PaymentMethodCode = method ?? string.Empty, PaymentStatus = status, FinancialPostingStatus = posting };
            if (method is null)
            {
                // Explicit corrupt/legacy-null behavior, without weakening the
                // production property's non-null application contract.
                var methodProperty = typeof(Order).GetProperty(nameof(Order.PaymentMethodCode));
                Assert.NotNull(methodProperty); methodProperty.SetValue(order, null);
            }
            var cod = method is not null && new[] { "COD", "CASHONDELIVERY", "CASH_ON_DELIVERY", "CASH-ON-DELIVERY" }
                .Contains(method, StringComparer.OrdinalIgnoreCase);
            var recognized = (int)status is 2 or 6 or 9 or 13;
            var accounted = posting is FinancialPostingStatus.Posted or FinancialPostingStatus.NotRequired;
            var needsCollection = total > 0m && !cod && !recognized;
            var needsPosting = total > 0m && !cod && !accounted && ((int)status is 2 or 6 or 9);
            Assert.Equal(cod, OrderOperationalFinancialAuthority.IsCashOnDelivery(order));
            Assert.Equal(needsCollection, OrderOperationalFinancialAuthority.RequiresRecognizedPaymentBeforeOutboundProgress(order));
            Assert.Equal(needsPosting, OrderOperationalFinancialAuthority.RequiresPostedCollectionAccountingBeforeOutboundProgress(order));
            Assert.Equal(!needsCollection && !needsPosting, OrderOperationalFinancialAuthority.CanAdvanceOutboundProgress(order));
            Assert.Equal(needsCollection ? "Cannot advance fulfillment until payment is confirmed." :
                needsPosting ? "Cannot advance fulfillment until the payment collection accounting journal is posted." : null,
                OrderOperationalFinancialAuthority.GetOutboundProgressBlockReason(order));
            Assert.Equal(total <= 0m ? null : !recognized ? "Cannot complete order until payment is collected." :
                !accounted ? "Cannot complete order until the payment collection accounting journal is posted." : null,
                OrderOperationalFinancialAuthority.GetCompletionBlockReason(order));
        }
    }

    [Fact]
    public void ReservationRulesPreserveEveryStatusNullTerminalAndExactTimeBoundary()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var active = now.AddMinutes(-15); var reconciliation = now.AddHours(-2);
        DateTime?[] times = [null, active.AddTicks(-1), active, active.AddTicks(1), reconciliation.AddTicks(-1), reconciliation];
        var protectedOrders = new[] { OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.Shipped,
            OrderStatus.Delivered, OrderStatus.Completed, OrderStatus.OnHold };
        (string? Intent, string? Transaction, string? Failure, bool Blocked)[] providerStates =
        [
            (null, null, null, false), ("fixture-intent", null, null, false),
            (null, "fixture-transaction", null, false), (" ", " ", "unrelated failure", false),
            (null, null, "PROVIDER_SESSION_OUTCOME_UNKNOWN:fixture", false),
            (null, null, "PROVIDER_SESSION_COMPENSATION_REQUIRED:fixture", false),
            (null, null, null, true)
        ];
        foreach (var order in NullableValues<OrderStatus>())
        foreach (var payment in NullableValues<PaymentStatus>())
        foreach (var expired in new[] { false, true })
        {
            foreach (var intent in NullableValues<PaymentIntentStatus>())
            foreach (var created in times)
            {
                var recent = intent is PaymentIntentStatus.Pending or PaymentIntentStatus.Authorized or PaymentIntentStatus.AwaitingVerification &&
                    created.HasValue && created.Value >= active;
                var expected = !order.HasValue || (!protectedOrders.Contains(order.Value) &&
                    !((int?)payment is 2 or 9) && intent != PaymentIntentStatus.Paid && (!recent || expired));
                Assert.Equal(expected, InventoryReservationReleaseAuthority.CanReleaseOrderReservation(order, payment, intent, created, active, expired));
            }
            foreach (var record in NullableValues<CommerceHubPaymentRecordStatus>())
            foreach (var created in times)
            foreach (var provider in providerStates)
            {
                var terminal = order is OrderStatus.Cancelled or OrderStatus.Refunded;
                var recognized = (int?)payment is 2 or 9 or 10 || record == CommerceHubPaymentRecordStatus.Paid || provider.Blocked;
                var unresolvedProvider = !string.IsNullOrWhiteSpace(provider.Intent) || !string.IsNullOrWhiteSpace(provider.Transaction) ||
                    (provider.Failure?.StartsWith("PROVIDER_SESSION_OUTCOME_UNKNOWN:", StringComparison.Ordinal) ?? false) ||
                    (provider.Failure?.StartsWith("PROVIDER_SESSION_COMPENSATION_REQUIRED:", StringComparison.Ordinal) ?? false);
                var gap = record == CommerceHubPaymentRecordStatus.Pending && created.HasValue && created.Value >= reconciliation && unresolvedProvider;
                var recent = record == CommerceHubPaymentRecordStatus.Pending && created.HasValue && created.Value >= active;
                var expected = !order.HasValue || (!protectedOrders.Contains(order.Value) &&
                    (terminal || (!recognized && !gap && (!recent || expired))));
                Assert.Equal(expected, InventoryReservationReleaseAuthority.CanReleaseCommerceHubReservation(order, payment, record, created,
                    provider.Intent, provider.Transaction, provider.Failure, provider.Blocked, active, reconciliation, expired));
            }
        }
    }

    [Theory]
    [InlineData("IsOrderSuccessfulTerminal")]
    [InlineData("IsTerminalSqlPaymentTruth")]
    [InlineData("IsCollectionAccountingEligibleFromSqlTruth")]
    [InlineData("NormalizeSuccessfulStatus")]
    public void LifecyclePurePredicatesPreserveLegacyNumericAndUnknownStatusBehavior(string methodName)
    {
        // Invoke ONLY private static predicates, without constructing the service
        // or running any payment, accounting, journal or external operation.
        var method = typeof(PointOfSale.Infrastructure.Payments.PaymentLifecycleService).GetMethod(methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        foreach (var status in Values<PaymentStatus>().Concat(Enumerable.Range(-2, 20).Select(Cast<PaymentStatus>)).Distinct())
        {
            object input = methodName is "IsOrderSuccessfulTerminal" or "IsCollectionAccountingEligibleFromSqlTruth"
                ? new Order { PaymentStatus = status } : status;
            var actual = method.Invoke(null, [input]);
            if (methodName == "NormalizeSuccessfulStatus") Assert.Equal(status, Assert.IsType<PaymentStatus>(actual));
            else Assert.Equal(methodName switch
            {
                "IsOrderSuccessfulTerminal" => (int)status is 2 or 9,
                "IsTerminalSqlPaymentTruth" => (int)status is 2 or 4 or 5 or 6 or 8 or 9 or 11 or 12,
                _ => (int)status is 2 or 5 or 6 or 9
            }, Assert.IsType<bool>(actual));
        }
    }
}
