using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Cart;
using PointOfSale.API.Services.Affiliate;
using PointOfSale.API.Services.Cart;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.Services;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Existing primary only. Task-owned checkout leads, no orders, attribution
/// writes, financial operations, database creation, migrations or cleanup.</summary>
public sealed class AffiliateShoppingJourneyPrimaryTests
{
    private static DefaultHttpContext Guest(string session)
    {
        var auth = new Mock<IAuthenticationService>();
        auth.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>()))
            .ReturnsAsync(AuthenticateResult.NoResult());
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(auth.Object).BuildServiceProvider()
        };
        context.Request.Headers["X-Session-Id"] = session;
        return context;
    }

    private static CheckoutSessionService Sessions(ApplicationDbContext db) => new(db,
        Mock.Of<IEventPublisher>(), new CommerceReliabilityTelemetry(), NullLogger<CheckoutSessionService>.Instance);

    [PrimaryAffiliateFact]
    public async Task Primary_ConcurrentSessionAdmission_SnapshotRoundtrip_AbandonmentAndTenantOwnerIsolation()
    {
        await using var db = await AffiliateP53ePrimarySupport.Open("P510-journey-primary");
        var link = await db.AffiliatePromotionLinks.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(l => l.TenantId == AffiliateP53ePrimarySupport.Store && l.IsActive && !l.IsDeleted && l.AffiliateAccountId != null);
        var tenantId = link.TenantId;
        var journey = new AffiliateShoppingJourney(tenantId, link.AffiliateAccountId!.Value, link.ReferralCode, link.Id);
        var cart = new CartDto { CartId = Guid.NewGuid(), TenantId = tenantId, AffiliateJourney = journey,
            Items = [new CartItemDto { ProductId = link.ProductId, VariantId = Guid.NewGuid(), Name = "P510 NO ORDER checkout lead", Quantity = 1, UnitPrice = 1 }] };
        var serialize = typeof(CheckoutSessionController).GetMethod("BuildAuthoritativeItemsSnapshotJson", BindingFlags.Static | BindingFlags.NonPublic)!;
        var snapshot = (string)serialize.Invoke(null, [cart])!;
        Assert.Contains("affiliateJourney", snapshot);
        var owner = "p510-" + Guid.NewGuid().ToString("N");
        async Task<Guid> Admit()
        {
            await using var scoped = await AffiliateP53ePrimarySupport.Open("P510-concurrent-session");
            return (await Sessions(scoped).CreateOrResumeSessionAsync(tenantId, cart.CartId, owner, null,
                snapshot, 1, "EGP", "Desktop", "P510 NO ORDER")).Id;
        }
        var admissions = await Task.WhenAll(Admit(), Admit());
        Assert.Single(admissions.Distinct());
        Assert.Equal(1, await db.CheckoutSessions.IgnoreQueryFilters().CountAsync(s => s.CartId == cart.CartId && s.TenantId == tenantId));
        var carts = new Mock<ICartService>();
        var service = new AffiliateShoppingJourneyService(carts.Object, db);
        Assert.Equal(journey, await service.GetActiveAsync(Guest(owner), tenantId, default));
        Assert.Null(await service.GetActiveAsync(Guest("p510-other-" + Guid.NewGuid()), tenantId, default));
        Assert.Null(await service.GetActiveAsync(Guest(owner), Guid.NewGuid(), default));
        var spoof = Guest("p510-other-" + Guid.NewGuid());
        spoof.Request.Headers["X-Customer-Id"] = Guid.NewGuid().ToString();
        Assert.Null(await service.GetActiveAsync(spoof, tenantId, default));
        await Sessions(db).MarkAsAbandonedAsync(admissions[0]);
        Assert.Equal(journey, await service.GetActiveAsync(Guest(owner), tenantId, default));
        // Fixture clock positioning, not a new production timeout: the existing
        // lifecycle refuses to abandon a session until 30 minutes of inactivity.
        var idleLead = await db.CheckoutSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == admissions[0]);
        idleLead.LastActivityAtUtc = DateTime.UtcNow.AddMinutes(-31);
        await db.SaveChangesAsync();
        await Sessions(db).MarkAsAbandonedAsync(admissions[0]);
        Assert.Null(await service.GetActiveAsync(Guest(owner), tenantId, default));
        // Abandoning the checkout form alone does not end a still-live cart.
        carts.Setup(c => c.GetJourneyCartAsync(tenantId, It.IsAny<CartOwner>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        Assert.Equal(journey, await service.GetActiveAsync(Guest(owner), tenantId, default));
        var saved = await db.CheckoutSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == admissions[0]);
        Assert.Equal(CheckoutSessionStatus.Abandoned, saved.Status);
        Assert.Null(saved.ConvertedOrderId);
        Assert.Equal(snapshot, saved.ItemsSnapshotJson);
        // Close only this test's interrupted earlier attempts, through the same
        // lifecycle service. Retain the leads as evidence; never delete rows.
        var interrupted = await db.CheckoutSessions.IgnoreQueryFilters().AsNoTracking().Where(s =>
            s.TenantId == tenantId && s.Source == "P510 NO ORDER" && s.Status == CheckoutSessionStatus.Active &&
            s.SessionId != null && s.SessionId.StartsWith("p510-") && s.ConvertedOrderId == null).Select(s => s.Id).ToListAsync();
        foreach (var id in interrupted)
        {
            var ownLead = await db.CheckoutSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == id);
            ownLead.LastActivityAtUtc = DateTime.UtcNow.AddMinutes(-31);
            await db.SaveChangesAsync();
            await Sessions(db).MarkAsAbandonedAsync(id);
        }
    }

    [PrimaryAffiliateFact]
    public async Task Primary_ExistingOrderIsTerminalAuthority_EvenWhenRedisCleanupOrConversionHasNotRun()
    {
        await using var db = await AffiliateP53ePrimarySupport.Open("P510-terminal-readonly");
        var order = await db.Orders.IgnoreQueryFilters().AsNoTracking().FirstAsync(o =>
            o.TenantId == AffiliateP53ePrimarySupport.Store && o.CartId != null && o.AffiliateAccountId != null);
        var cart = new CartDto { CartId = order.CartId!.Value, TenantId = order.TenantId,
            AffiliateJourney = new(order.TenantId, order.AffiliateAccountId!.Value, order.AffiliateReferralCode!, order.PromotionLinkId),
            Items = [new CartItemDto { Quantity = 1 }] };
        var carts = new Mock<ICartService>();
        carts.Setup(c => c.GetJourneyCartAsync(order.TenantId, It.IsAny<CartOwner>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        var service = new AffiliateShoppingJourneyService(carts.Object, db);
        Assert.Null(await service.GetActiveAsync(Guest("p510-terminal-" + Guid.NewGuid()), order.TenantId, default));
        Assert.Empty(db.ChangeTracker.Entries());
    }
}
