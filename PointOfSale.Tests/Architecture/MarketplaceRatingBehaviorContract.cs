using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.Core.DTOs.Marketplace;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Exceptions;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Architecture;

// Actual backend command/domain/interceptor/query behavior. InMemory is not
// relational uniqueness,concurrency,outbox atomicity or provider certification.
internal static class MarketplaceRatingBehaviorContract
{
    internal static async Task AssertCommandAndImmutabilityAsync()
    {
        foreach (var scenario in new[] { "valid", "foreign-actor", "foreign-tenant", "pending",
                     "provider-completed", "duplicate", "bad-rating", "marketing-missing" })
        {
            var tenant = Guid.NewGuid(); var buyer = Guid.NewGuid(); var provider = Guid.NewGuid();
            var scope = new TestTenantContext(scenario == "foreign-tenant" ? Guid.NewGuid() : tenant);
            await using var db = Context(scope);
            var profile = MarketplaceProfile.Create(tenant, provider, "Synthetic provider");
            var service = Service.Create(tenant, profile.Id, "Synthetic service", "Fixture",
                scenario == "marketing-missing" ? "Marketing" : "Design");
            var request = Request(tenant, service.Id, buyer, provider, profile.Id,
                scenario == "pending" ? ServiceRequestStatus.Pending : scenario == "provider-completed" ?
                    ServiceRequestStatus.CompletedByProvider : ServiceRequestStatus.CompletedByBuyer);
            db.AddRange(profile, service, request);
            if (scenario == "duplicate") db.Add(ServiceReview.Create(tenant, request.Id, service.Id, buyer, provider, 4));
            await db.SaveChangesAsync();
            var publisher = new Mock<IEventPublisher>(MockBehavior.Strict);
            publisher.Setup(p => p.PublishAsync(It.IsAny<ServiceReviewCreatedEvent>(), It.IsAny<CancellationToken>()))
                .Callback<object, CancellationToken>((value, _) => {
                    var message = Assert.IsType<ServiceReviewCreatedEvent>(value);
                    Assert.Equal(tenant, message.TenantId); Assert.Equal(request.Id, message.ServiceRequestId);
                    Assert.Equal(service.Id, message.ServiceId); Assert.Equal(profile.Id, message.ProviderProfileId);
                    Assert.Equal(buyer, message.ReviewerUserId); Assert.Equal(provider, message.ProviderUserId);
                    Assert.Equal(5, message.Rating);
                }).Returns(Task.CompletedTask);
            var command = new ServiceReviewService(db, scope, publisher.Object, NullLogger<ServiceReviewService>.Instance);
            var dto = new CreateReviewDto(request.Id, scenario == "bad-rating" ? 6 : 5, "Synthetic immutable review");
            Task<ServiceReview> Submit() => command.CreateReviewAsync(scenario == "foreign-actor" ? Guid.NewGuid() : buyer, dto);
            if (scenario == "valid")
            {
                var created = await Submit(); Assert.NotEqual(Guid.Empty, created.Id);
                Assert.Equal(service.Id, created.ServiceId); Assert.Equal(request.Id, created.ServiceRequestId);
                Assert.Equal(buyer, created.ReviewerUserId); Assert.Equal(provider, created.ProviderUserId);
                Assert.Equal(5, created.Rating);
                Assert.Equal(created.Id, Assert.Single(await db.Set<ServiceReview>().ToListAsync()).Id);
                var trust = await new ProviderTrustService(db).GetTrustAggregateAsync(profile.Id);
                Assert.NotNull(trust); Assert.Equal(1, trust.TotalReviews); Assert.Equal(5m, trust.AverageRating);
            }
            else if (scenario == "bad-rating") await Assert.ThrowsAsync<ArgumentException>(Submit);
            else await Assert.ThrowsAsync<MarketplaceDomainException>(Submit);
            publisher.Verify(p => p.PublishAsync(It.IsAny<ServiceReviewCreatedEvent>(), It.IsAny<CancellationToken>()),
                scenario == "valid" ? Times.Once() : Times.Never());
            if (scenario != "valid") Assert.Equal(scenario == "duplicate" ? 1 : 0,
                await db.Set<ServiceReview>().IgnoreQueryFilters().CountAsync());
        }

        foreach (var property in new[] { nameof(ServiceReview.Rating), nameof(ServiceReview.Comment),
                     nameof(ServiceReview.ReviewerUserId), nameof(ServiceReview.ServiceRequestId), nameof(ServiceReview.TenantId), "delete" })
        {
            var tenant = Guid.NewGuid(); await using var db = Context(new TestTenantContext(tenant));
            var review = ServiceReview.Create(tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5, "Original");
            db.Add(review); await db.SaveChangesAsync();
            if (property == "delete") db.Remove(review);
            else
            {
                var entry = db.Entry(review).Property(property);
                entry.CurrentValue = entry.Metadata.ClrType == typeof(Guid) ? Guid.NewGuid() :
                    entry.Metadata.ClrType == typeof(int) ? 1 : "Forged";
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Throws<InvalidOperationException>(() => ServiceRequest.Create(Guid.NewGuid(), Guid.NewGuid(),
            Guid.Empty, Guid.Empty, Guid.NewGuid(), "Self request", null, null, null, null, null));
    }

    internal static async Task AssertTrustRejectsPoisonedPairAsync(string poison)
    {
        var tenant = Guid.NewGuid(); var provider = Guid.NewGuid(); var buyer = Guid.NewGuid();
        await using var db = Context(new TestTenantContext(tenant));
        var profile = MarketplaceProfile.Create(tenant, provider, "Synthetic provider");
        var service = Service.Create(tenant, profile.Id, "Synthetic service", "Fixture");
        var good = Request(tenant, service.Id, buyer, provider, profile.Id, ServiceRequestStatus.CompletedByBuyer);
        var bad = Request(poison == "foreign-tenant" ? Guid.NewGuid() : tenant, service.Id, Guid.NewGuid(), provider,
            profile.Id, poison == "nonfinal" ? ServiceRequestStatus.Pending : ServiceRequestStatus.CompletedByBuyer);
        db.AddRange(profile, service, good, bad,
            ServiceReview.Create(tenant, good.Id, service.Id, buyer, provider, 5),
            ServiceReview.Create(bad.TenantId, bad.Id, poison == "wrong-service" ? Guid.NewGuid() : service.Id,
                poison == "wrong-reviewer" ? Guid.NewGuid() : bad.BuyerUserId,
                poison == "wrong-provider" ? Guid.NewGuid() : provider, 1));
        await db.SaveChangesAsync();
        var trust = await new ProviderTrustService(db).GetTrustAggregateAsync(profile.Id);
        Assert.NotNull(trust); Assert.Equal(tenant, trust.TenantId);
        Assert.Equal(1, trust.TotalReviews); Assert.Equal(5m, trust.AverageRating);
        Assert.Equal(1, trust.FiveStarCount); Assert.Equal(0, trust.OneStarCount);
    }

    internal static async Task AssertRankingRejectsForeignTenantInputsAsync()
    {
        var tenant = Guid.NewGuid(); await using var db = Context(new TestTenantContext(tenant));
        var legitimate = MarketplaceProfile.Create(tenant, Guid.NewGuid(), "Legitimate provider");
        var poisoned = MarketplaceProfile.Create(tenant, Guid.NewGuid(), "Poisoned provider");
        var validService = Service.Create(tenant, legitimate.Id, "Valid service", "Fixture");
        var poisonedService = Service.Create(tenant, poisoned.Id, "Other service", "Fixture");
        db.AddRange(legitimate, poisoned, validService, poisonedService);
        for (var i = 0; i < 6; i++)
        {
            var good = Request(tenant, validService.Id, Guid.NewGuid(), legitimate.UserId, legitimate.Id, ServiceRequestStatus.CompletedByBuyer);
            var bad = Request(Guid.NewGuid(), poisonedService.Id, Guid.NewGuid(), poisoned.UserId, poisoned.Id, ServiceRequestStatus.CompletedByBuyer);
            db.AddRange(good, bad,
                ServiceReview.Create(tenant, good.Id, validService.Id, good.BuyerUserId, legitimate.UserId, 4),
                ServiceReview.Create(bad.TenantId, bad.Id, poisonedService.Id, bad.BuyerUserId, poisoned.UserId, 5));
        }
        await db.SaveChangesAsync();
        // Filtering only after Take would still let the forged five-star group
        // displace the legitimate provider,even if its later aggregate is empty.
        var winner = Assert.Single(await new ProviderTrustService(db).GetTopRatedProvidersAsync(1));
        Assert.Equal(legitimate.Id, winner.ProviderProfileId); Assert.Equal(6, winner.TotalReviews); Assert.Equal(4m, winner.AverageRating);
    }

    private static ApplicationDbContext Context(TestTenantContext scope) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).AddInterceptors(new MarketplaceInvariantInterceptor()).Options, scope);

    private static ServiceRequest Request(Guid tenant, Guid service, Guid buyer, Guid provider, Guid profile, ServiceRequestStatus phase)
    {
        var request = ServiceRequest.Create(tenant, service, buyer, provider, profile, "Synthetic service", "Design", null, null, null, null);
        if (phase != ServiceRequestStatus.Pending) { request.Accept(); request.MarkCompletedByProvider(); }
        if (phase == ServiceRequestStatus.CompletedByBuyer) request.ConfirmCompletedByBuyer();
        return request;
    }
}
