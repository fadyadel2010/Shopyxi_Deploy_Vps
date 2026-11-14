using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events.WhatsApp;
using PointOfSale.Core.Interfaces.WhatsApp;
using PointOfSale.Infrastructure.Consumers.WhatsApp;
using PointOfSale.Infrastructure.MongoDB.Documents;
using PointOfSale.Infrastructure.WhatsApp.Interfaces;

namespace PointOfSale.Tests.WhatsApp;

// Compiled consumer with strict no-network collaborators. No provider message,
// MongoDB, SQL authority, financial operation or delivery proof is simulated.
public sealed class WhatsAppConfirmationTenantSnapshotContractTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("disabled")]
    [InlineData("no-account")]
    [InlineData("valid")]
    [InlineData("mutate-null")]
    [InlineData("mutate-foreign")]
    public async Task ConsumerBindsOneNonEmptyTenantBeforeAwaitedFeatureAdmission(string state)
    {
        var tenantId = Guid.NewGuid(); var accountId = Guid.NewGuid(); var orderId = Guid.NewGuid();
        var message = new WhatsAppOrderConfirmationRequestedEvent
        { TenantId = state == "missing" ? null : state == "empty" ? Guid.Empty : tenantId,
            OrderId = orderId, OrderNumber = "FICTIONAL-ORDER", CustomerPhone = "fictional-not-dialable" };
        var gate = new Mock<IWhatsAppFeatureGateService>(MockBehavior.Strict);
        var logs = new Mock<IWhatsAppMessageLogService>(MockBehavior.Strict);
        var dispatcher = new Mock<IWhatsAppMessageDispatcher>(MockBehavior.Strict);
        WhatsAppMessageLogDocument? captured = null;
        if (state is not "missing" and not "empty")
        {
            gate.Setup(g => g.IsOrderConfirmationEnabledAsync(tenantId, default))
                .ReturnsAsync(() =>
                {
                    if (state == "mutate-null") message.TenantId = null;
                    if (state == "mutate-foreign") message.TenantId = Guid.NewGuid();
                    return state != "disabled";
                });
            if (state != "disabled")
                gate.Setup(g => g.GetActiveAccountAsync(tenantId, WhatsAppUseCase.OrderConfirmation, default))
                    .ReturnsAsync(state == "no-account" ? null : new WhatsAppAccount { Id = accountId, TenantId = tenantId });
        }
        var expectedLog = state is "valid" or "mutate-null" or "mutate-foreign";
        if (expectedLog)
        {
            logs.Setup(l => l.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()))
                .ReturnsAsync((WhatsAppMessageLogDocument log) =>
                { captured = log; log.Id = "fictional-no-network-log"; return log; });
            dispatcher.Setup(d => d.DispatchAsync("fictional-no-network-log", default)).Returns(Task.CompletedTask);
        }
        var context = new Mock<ConsumeContext<WhatsAppOrderConfirmationRequestedEvent>>(MockBehavior.Strict);
        context.SetupGet(c => c.Message).Returns(message);
        await new WhatsAppOrderConfirmationConsumer(gate.Object, dispatcher.Object, logs.Object,
            NullLogger<WhatsAppOrderConfirmationConsumer>.Instance).Consume(context.Object);
        if (expectedLog)
        {
            var actual = Assert.IsType<WhatsAppMessageLogDocument>(captured);
            Assert.Equal(tenantId.ToString(), actual.TenantId); Assert.Equal(accountId.ToString(), actual.AccountId);
            Assert.Equal(orderId.ToString(), actual.RelatedEntityId);
            Assert.Equal($"whatsapp:order-confirmation:{tenantId:N}:{orderId:N}", actual.IdempotencyKey);
            logs.Verify(l => l.CreateLogAsync(It.IsAny<WhatsAppMessageLogDocument>()), Times.Once);
            dispatcher.Verify(d => d.DispatchAsync("fictional-no-network-log", default), Times.Once);
        }
        else { Assert.Null(captured); logs.VerifyNoOtherCalls(); dispatcher.VerifyNoOtherCalls(); }
        if (state is "missing" or "empty") gate.VerifyNoOtherCalls();
    }
}
