using MassTransit;
using PointOfSale.Core.Events.Affiliate;

var user = Environment.GetEnvironmentVariable("REPLAY_RABBIT_USER") ?? throw new InvalidOperationException("Missing broker user");
var password = Environment.GetEnvironmentVariable("REPLAY_RABBIT_PASSWORD") ?? throw new InvalidOperationException("Missing broker password");
var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
{
    cfg.Host(new Uri("rabbitmq://affiliate-full-rabbit/"), host =>
    {
        host.Username(user);
        host.Password(password);
    });
});
await bus.StartAsync();
try
{
    var eventId = Guid.NewGuid();
    var endpoint = await bus.GetSendEndpoint(new Uri("rabbitmq://affiliate-full-rabbit/affiliate-order-conversion"));
    await endpoint.Send(new AffiliateOrderPlacedEvent
    {
        EventId = eventId,
        OccurredAtUtc = DateTime.UtcNow,
        CorrelationId = eventId.ToString(),
        IdempotencyKey = $"isolated-replay-{eventId}",
        EventVersion = 2,
        Version = 2,
        TenantId = Guid.Parse("15150000-2026-4015-a000-000000000001"),
        StoreTenantId = Guid.Parse("15150000-2026-4015-a000-000000000001"),
        OrderId = Guid.Parse("932bccae-3f92-4c55-bb7d-4e3cb2ef7c1d"),
        OrderNumber = "ORD-1515-2026-01001",
        AffiliateAccountId = Guid.Parse("01a1df98-5257-7282-9e19-f9f8ae5b6b74"),
        ReferralCode = "WWC3NC7R9B",
        OrderTotal = 1110m,
        Currency = "EGP",
        PaymentMethodCode = "COD",
        ItemCount = 1,
        IsGuestCheckout = true,
        PromotionLinkId = Guid.Parse("01a1dfa0-8334-755b-aeb8-c037243e70c0"),
        AffiliateUserId = Guid.Parse("01a1df98-5293-7c63-85ab-9b4176abb7ac")
    });
    Console.WriteLine(eventId);
}
finally
{
    await bus.StopAsync();
}
