using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Architecture;

// Domain/interceptor behavior only. No claim of relational constraints, row locks,
// concurrent business commands, audit/outbox atomicity or historical certification.
internal static class MarketplaceRequestInvariantContract
{
    internal static async Task AssertLifecycleTransitionsAsync()
    {
        foreach (var from in Enum.GetValues<ServiceRequestStatus>())
        foreach (var to in Enum.GetValues<ServiceRequestStatus>())
        {
            var tenant = Guid.NewGuid();
            await using var db = Context(tenant);
            var request = Request(tenant, from);
            db.Add(request); await db.SaveChangesAsync();
            var allowed = (from, to) switch {
                (ServiceRequestStatus.Pending, ServiceRequestStatus.Accepted or ServiceRequestStatus.Rejected or ServiceRequestStatus.Cancelled) => true,
                (ServiceRequestStatus.Accepted, ServiceRequestStatus.CompletedByProvider or ServiceRequestStatus.Cancelled) => true,
                (ServiceRequestStatus.CompletedByProvider, ServiceRequestStatus.CompletedByBuyer) => true,
                _ => from == to
            };
            db.Entry(request).Property(nameof(ServiceRequest.Status)).CurrentValue = to;
            if (from != to)
            {
                var stamp = to switch {
                    ServiceRequestStatus.Accepted => nameof(ServiceRequest.AcceptedAt),
                    ServiceRequestStatus.Rejected => nameof(ServiceRequest.RejectedAt),
                    ServiceRequestStatus.Cancelled => nameof(ServiceRequest.CancelledAt),
                    ServiceRequestStatus.CompletedByProvider => nameof(ServiceRequest.CompletedByProviderAt),
                    ServiceRequestStatus.CompletedByBuyer => nameof(ServiceRequest.CompletedByBuyerAt),
                    _ => null
                };
                if (stamp is not null) db.Entry(request).Property(stamp).CurrentValue = DateTime.UtcNow;
            }
            if (allowed) await db.SaveChangesAsync();
            else await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        var domain = Request(Guid.NewGuid(), ServiceRequestStatus.Pending);
        Assert.False(domain.CanBeReviewed()); Assert.False(domain.AllowsInteraction());
        Assert.Throws<InvalidOperationException>(domain.ConfirmCompletedByBuyer);
        domain.Accept(); Assert.NotNull(domain.AcceptedAt);
        Assert.True(domain.AllowsInteraction()); Assert.False(domain.CanBeReviewed());
        domain.MarkCompletedByProvider(); Assert.NotNull(domain.CompletedByProviderAt);
        Assert.True(domain.AllowsInteraction()); Assert.False(domain.CanBeReviewed());
        domain.ConfirmCompletedByBuyer(); Assert.NotNull(domain.CompletedByBuyerAt);
        Assert.False(domain.AllowsInteraction()); Assert.True(domain.CanBeReviewed());
        Assert.Throws<InvalidOperationException>(domain.MarkCompletedByProvider);
        Assert.Throws<InvalidOperationException>(() => domain.Cancel());
    }

    internal static async Task AssertIdentityAndSnapshotImmutabilityAsync()
    {
        foreach (var property in new[] { nameof(ServiceRequest.TenantId), nameof(ServiceRequest.ServiceId),
            nameof(ServiceRequest.BuyerUserId), nameof(ServiceRequest.ProviderUserId), nameof(ServiceRequest.ProviderProfileId),
            nameof(ServiceRequest.ServiceTitleSnapshot) })
        {
            var tenant = Guid.NewGuid();
            await using var db = Context(tenant);
            var request = Request(tenant, ServiceRequestStatus.Accepted);
            db.Add(request); await db.SaveChangesAsync();
            var entry = db.Entry(request).Property(property);
            entry.CurrentValue = entry.Metadata.ClrType == typeof(Guid) ? Guid.NewGuid() : "Forged snapshot";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        foreach (var property in new[] { nameof(ServiceRequest.AcceptedAt), nameof(ServiceRequest.CompletedByProviderAt), nameof(ServiceRequest.CompletedByBuyerAt) })
        {
            var tenant = Guid.NewGuid();
            await using var db = Context(tenant);
            var request = Request(tenant, ServiceRequestStatus.Accepted);
            db.Add(request); await db.SaveChangesAsync();
            db.Entry(request).Property(property).CurrentValue = DateTime.UtcNow.AddHours(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    private static ApplicationDbContext Context(Guid tenant) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).AddInterceptors(new MarketplaceInvariantInterceptor()).Options,
        new TestTenantContext(tenant));

    private static ServiceRequest Request(Guid tenant, ServiceRequestStatus phase)
    {
        var request = ServiceRequest.Create(tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Frozen request fixture", "Fixed category", 12.5m, null, "EGP", null, "Original message");
        if (phase is ServiceRequestStatus.Accepted or ServiceRequestStatus.CompletedByProvider or ServiceRequestStatus.CompletedByBuyer) request.Accept();
        if (phase is ServiceRequestStatus.CompletedByProvider or ServiceRequestStatus.CompletedByBuyer) request.MarkCompletedByProvider();
        if (phase == ServiceRequestStatus.CompletedByBuyer) request.ConfirmCompletedByBuyer();
        if (phase == ServiceRequestStatus.Rejected) request.Reject();
        if (phase == ServiceRequestStatus.Cancelled) request.Cancel();
        return request;
    }
}
