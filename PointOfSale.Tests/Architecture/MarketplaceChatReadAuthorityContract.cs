using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Exceptions;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Architecture;

// Current service predicate/result behavior, NOT PostgreSQL locking or send atomicity.
// This replaces a historical PASS-prose obligation with executable read authority.
internal static class MarketplaceChatReadAuthorityContract
{
    internal static async Task AssertReadAuthorityAsync()
    {
        foreach (var phase in new[] { "pending", "accepted", "provider-completed", "buyer-completed", "rejected", "cancelled", "deleted" })
        {
            var tenant = Guid.NewGuid(); var buyer = Guid.NewGuid(); var provider = Guid.NewGuid();
            var scope = new TestTenantContext(tenant);
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, scope);
            var request = ServiceRequest.Create(tenant, Guid.NewGuid(), buyer, provider, Guid.NewGuid(),
                "Current read-authority fixture", null, null, null, null, null);
            if (phase is "accepted" or "provider-completed" or "buyer-completed" or "deleted") request.Accept();
            if (phase is "provider-completed" or "buyer-completed") request.MarkCompletedByProvider();
            if (phase == "buyer-completed") request.ConfirmCompletedByBuyer();
            if (phase == "rejected") request.Reject();
            if (phase == "cancelled") request.Cancel();
            if (phase == "deleted") request.IsDeleted = true;
            var owned = ServiceRequestMessage.CreateText(tenant, request.Id, buyer, SenderRole.Buyer, "Owned history");
            var foreign = ServiceRequestMessage.CreateText(Guid.NewGuid(), request.Id, buyer, SenderRole.Buyer, "Foreign tenant");
            var otherRequest = ServiceRequestMessage.CreateText(tenant, Guid.NewGuid(), buyer, SenderRole.Buyer, "Other request");
            var deleted = ServiceRequestMessage.CreateText(tenant, request.Id, buyer, SenderRole.Buyer, "Deleted message");
            deleted.IsDeleted = true;
            db.AddRange(request, owned, foreign, otherRequest, deleted);
            await db.SaveChangesAsync();
            var service = new ChatService(db, scope);
            foreach (var actor in new[] { buyer, provider, Guid.NewGuid() })
            {
                var participant = actor == buyer || actor == provider;
                var history = participant && phase is not ("pending" or "deleted");
                Assert.Equal(participant && phase is ("accepted" or "provider-completed"),
                    await service.CanUserParticipateAsync(request.Id, actor, tenant));
                Assert.Equal(history, await service.CanUserViewConversationAsync(request.Id, actor, tenant));
                Assert.False(await service.CanUserParticipateAsync(request.Id, actor, Guid.NewGuid()));
                Assert.False(await service.CanUserViewConversationAsync(request.Id, actor, Guid.NewGuid()));
                await Assert.ThrowsAsync<MarketplaceDomainException>(() => service.GetMessagesAsync(request.Id, actor, Guid.NewGuid()));
                if (history)
                {
                    Assert.Equal(owned.Id, Assert.Single(await service.GetMessagesAsync(request.Id, actor, tenant)).Id);
                    await Assert.ThrowsAsync<MarketplaceDomainException>(() => service.GetMessagesAsync(
                        request.Id, actor, tenant, beforeMessageId: otherRequest.Id));
                    await Assert.ThrowsAsync<MarketplaceDomainException>(() => service.GetMessagesAsync(
                        request.Id, actor, tenant, beforeMessageId: foreign.Id));
                }
                else await Assert.ThrowsAsync<MarketplaceDomainException>(() => service.GetMessagesAsync(request.Id, actor, tenant));
            }
            Assert.False(await service.CanUserViewConversationAsync(Guid.NewGuid(), buyer, tenant));
            Assert.False(await service.CanUserParticipateAsync(Guid.NewGuid(), provider, tenant));
        }
    }
}
