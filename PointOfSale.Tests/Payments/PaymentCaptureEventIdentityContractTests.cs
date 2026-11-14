using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Marketplace;
using PointOfSale.Core.Interfaces.Payments;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Repositories;
using PointOfSale.Infrastructure.Observability;
using PointOfSale.Infrastructure.Payments;

namespace PointOfSale.Tests.Payments;

// Invoke only the actual event-emission boundary with strict recorders. No
// payment transition, SQL connection, posting, provider or bus operation runs.
public sealed class PaymentCaptureEventIdentityContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(-1)]
    public void SharedReferenceResolverPreservesExistingPriorityWithoutInventingFallback(int first)
    {
        var order = new Order { PaymentReference = first < 0 ? null : "reference-5" };
        var request = new HandlePaymentResultRequest
        { PaymentReference = first == 0 ? "reference-0" : null,
            ProviderPaymentId = first is >= 0 and <= 2 ? "reference-2" : null };
        PaymentIntent? intent = first is >= 0 and <= 4 ? new PaymentIntent
        { ProviderIntentId = first <= 1 ? "reference-1" : null,
            ProviderPaymentId = first <= 3 ? "reference-3" : null, PaymentReference = "reference-4" } : null;
        var method = typeof(PaymentLifecycleService).GetMethod("ResolveSuccessfulPaymentReference", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(first < 0 ? null : "reference-" + first, method.Invoke(null, [order, intent, request]));
    }

    [Fact]
    public void ActualSuccessCallerPassesOneResolvedIdentityToAccountingAndEvent()
    {
        var root = CSharpSyntaxTree.ParseText(PointOfSale.Tests.Architecture.CertificationSource.ReadRepoFile(
            "PointOfSale.Infrastructure/Payments/PaymentLifecycleService.cs")).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "HandleSuccessAsync");
        var declarations = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(d => d.Identifier.ValueText == "resolvedPaymentReference");
        var reference = Assert.Single(declarations);
        Assert.StartsWith("ResolveSuccessfulPaymentReference(", reference.Initializer!.Value.ToString(), StringComparison.Ordinal);
        var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
        var accounting = Assert.Single(calls, c => c.Expression.ToString() == "EnqueueSuccessOutboxAsync");
        var emission = Assert.Single(calls, c => c.Expression.ToString() == "PublishSuccessDomainEventAsync");
        Assert.Equal("resolvedPaymentReference", accounting.ArgumentList.Arguments[5].Expression.ToString());
        Assert.Equal("resolvedPaymentReference", emission.ArgumentList.Arguments[4].Expression.ToString());
    }

    [Fact]
    public void MissingOnlineIdentityRefusalPrecedesSuccessfulStateAndInventoryMutations()
    {
        var root = CSharpSyntaxTree.ParseText(PointOfSale.Tests.Architecture.CertificationSource.ReadRepoFile(
            "PointOfSale.Infrastructure/Payments/PaymentLifecycleService.cs")).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "HandleSuccessAsync");
        var refusal = Assert.Single(method.DescendantNodes().OfType<IfStatementSyntax>(),
            statement => statement.Condition.ToString().Contains("IsNullOrWhiteSpace(resolvedPaymentReference)", StringComparison.Ordinal));
        Assert.Contains("PAYMENT_REFERENCE_REQUIRED", refusal.Statement.ToString(), StringComparison.Ordinal);
        foreach (var name in new[] { "UpdatePaymentIntentSucceeded", "ApplySuccessfulPaymentAllocation", "_stockRepository.CommitOrderReservationsAsync" })
        {
            var call = Assert.Single(method.DescendantNodes().OfType<InvocationExpressionSyntax>(), c => c.Expression.ToString() == name);
            Assert.True(refusal.SpanStart < call.SpanStart);
        }
    }

    [Theory]
    [InlineData("request")]
    [InlineData("order")]
    [InlineData("provider-resolved")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("request-priority")]
    public async Task OnlineCaptureEventRequiresExactResolvedReferenceBeforePublishing(string state)
    {
        var order = new Order { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), OrderNumber = "FICTIONAL-EVENT-ONLY",
            PaymentReference = state is "order" or "request-priority" ? "order-reference" : null };
        var request = new HandlePaymentResultRequest { OrderId = order.Id, Source = PaymentLifecycleSource.GatewayWebhook,
            Gateway = "STRIPE", Currency = "EGP", PaymentReference = state switch
            { "request" or "request-priority" => "request-reference", "empty" => "", "whitespace" => " ", _ => null } };
        var resolved = state == "provider-resolved" ? "provider-only-reference" : request.PaymentReference ?? order.PaymentReference;
        OnlinePaymentCapturedEvent? captured = null;
        var publisher = new Mock<IEventPublisher>(MockBehavior.Strict);
        publisher.Setup(p => p.PublishAsync(It.IsAny<OnlinePaymentCapturedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<OnlinePaymentCapturedEvent, CancellationToken>((message, _) => captured = message).Returns(Task.CompletedTask);
        var correlation = new Mock<ICorrelationIdAccessor>(MockBehavior.Strict);
        correlation.Setup(c => c.GetCorrelationId()).Returns("fictional-trace");
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var outbox = new Mock<IAccountingOutboxService>(MockBehavior.Strict);
        var accounting = new Mock<ICommerceAccountingService>(MockBehavior.Strict);
        var marketplace = new Mock<IMarketplaceEventOutboxService>(MockBehavior.Strict);
        var stock = new Mock<IProductVariantStockRepository>(MockBehavior.Strict);
        var metrics = new Mock<IInventoryReservationMetricsService>(MockBehavior.Strict);
        var coupon = new Mock<ICouponValidationService>(MockBehavior.Strict);
        var service = new PaymentLifecycleService(db, outbox.Object, accounting.Object, publisher.Object, marketplace.Object,
            correlation.Object, stock.Object, metrics.Object, coupon.Object, NullLogger<PaymentLifecycleService>.Instance,
            Options.Create(new PaymentLifecycleSettings()));
        var method = typeof(PaymentLifecycleService).GetMethod("PublishSuccessDomainEventAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var now = DateTime.UtcNow;
        // Retain the same case IDs across the pre-repair and repaired boundary.
        // Older signature cannot accept the already resolved accounting identity.
        object?[] arguments = method.GetParameters().Length == 5
            ? [order, request, 7m, now, CancellationToken.None]
            : [order, request, 7m, now, resolved, CancellationToken.None];
        Task Emit() => Assert.IsAssignableFrom<Task>(method.Invoke(service, arguments));
        if (string.IsNullOrWhiteSpace(resolved))
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(Emit);
            Assert.Empty(publisher.Invocations);
        }
        else
        {
            await Emit();
            var actual = Assert.IsType<OnlinePaymentCapturedEvent>(captured);
            Assert.Equal(resolved, actual.PaymentIntentId);
            Assert.Equal(order.TenantId, actual.TenantId); Assert.Equal(order.Id, actual.OrderId);
            Assert.Equal(7m, actual.AmountCaptured); Assert.Equal(now, actual.CapturedAt); Assert.Equal("EGP", actual.Currency);
            publisher.Verify(p => p.PublishAsync(It.IsAny<OnlinePaymentCapturedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        Assert.False(db.ChangeTracker.HasChanges());
        outbox.VerifyNoOtherCalls(); accounting.VerifyNoOtherCalls(); marketplace.VerifyNoOtherCalls();
        stock.VerifyNoOtherCalls(); metrics.VerifyNoOtherCalls(); coupon.VerifyNoOtherCalls();
    }
}
