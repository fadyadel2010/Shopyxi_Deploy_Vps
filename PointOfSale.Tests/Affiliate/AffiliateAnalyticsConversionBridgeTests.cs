using System.Reflection;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Checkout;
using PointOfSale.API.Security;
using PointOfSale.API.Services.Affiliate;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Infrastructure.Consumers.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.MongoDB;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateAnalyticsConversionBridgeTests
{
    private static AffiliateAttributionEvidenceTokenService Tokens() => new(
        new EphemeralDataProtectionProvider(), TimeProvider.System,
        NullLogger<AffiliateAttributionEvidenceTokenService>.Instance);

    [Fact]
    public void ProtectedEvidence_RoundTripsExactServerClickProductAndTenant()
    {
        var service = Tokens();
        var tenant = Guid.NewGuid(); var profile = Guid.NewGuid();
        var link = Guid.NewGuid(); var click = Guid.NewGuid(); var product = Guid.NewGuid();
        var token = service.CreateWithClick(tenant, profile, " abc123 ", link, click, product);
        Assert.True(service.TryValidate(token, tenant, out var evidence));
        Assert.Equal((tenant, profile, "ABC123", link, click, product),
            (evidence.TenantId, evidence.AffiliateAccountId, evidence.ReferralCode,
             evidence.PromotionLinkId, evidence.ReferralClickId, evidence.ReferralProductId));
        Assert.False(service.TryValidate(token, Guid.NewGuid(), out _));
        Assert.False(service.TryValidate(token + "forged", tenant, out _));
    }

    [Fact]
    public void AttributionFactory_PersistsExactClickAndRejectsPartialCorrelation()
    {
        var click = Guid.NewGuid(); var product = Guid.NewGuid();
        var attribution = AffiliateAttribution.CreateV2(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "ABC123", AttributionSource.ReferralLink, Guid.NewGuid(), click, product);
        Assert.Equal(click, attribution.ReferralClickId);
        Assert.Equal(product, attribution.ReferralProductId);
        Assert.Throws<ArgumentException>(() => AffiliateAttribution.Create(Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "ABC123", AttributionSource.ReferralLink, null, product));
    }

    [Fact]
    public void Journey_CarriesCorrelationOnlyAsServerStampedState()
    {
        var click = Guid.NewGuid(); var product = Guid.NewGuid();
        var journey = new AffiliateShoppingJourney(Guid.NewGuid(), Guid.NewGuid(), "ABC123", Guid.NewGuid())
            { ReferralClickId = click, ReferralProductId = product };
        Assert.Equal(click, journey.ReferralClickId);
        Assert.Equal(product, journey.ReferralProductId);
        Assert.DoesNotContain(typeof(CheckoutRequestDto).GetProperties(), p =>
            p.Name is "ReferralClickId" or "AffiliateAccountId" or "PromotionLinkId");
    }

    [Fact]
    public void SqlModel_HasOneOrderAndOneClickAttributionUniqueness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ApplicationDbContext(options);
        var entity = db.Model.FindEntityType(typeof(AffiliateAttribution))!;
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "OrderId" }));
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "ReferralClickId" }));
    }

    [Fact]
    public void DurableConsumer_UsesOrderPlacedEventNotPaymentOrCommission()
    {
        Assert.Contains(typeof(AffiliateOrderConversionConsumer).GetInterfaces(), i => i.IsGenericType &&
            i.GetGenericTypeDefinition() == typeof(IConsumer<>) &&
            i.GenericTypeArguments.Single() == typeof(AffiliateOrderPlacedEvent));
        Assert.DoesNotContain(typeof(AffiliateOrderConversionConsumer).GetInterfaces(), i =>
            i.GenericTypeArguments.Any(t => t.Name.Contains("Payment") || t.Name.Contains("Commission")));
    }

    [Fact]
    public void ClickConsumer_RequiresServerIssuedClickIdentity()
    {
        var root = TestConfiguration.GetSolutionRoot();
        var consumer = File.ReadAllText(Path.Combine(root,
            "PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateAnalyticsConsumers.cs"));
        Assert.Contains("!e.ClickId.HasValue", consumer);
        Assert.Contains("Id = e.ClickId.Value", consumer);
        Assert.DoesNotContain("Id = e.ClickId ?? Guid.NewGuid()", consumer);
    }

    [Fact]
    public void ProjectionDocument_HasNoFinancialFulfillmentOrCustomerData()
    {
        var names = typeof(AffiliateOrderConversionDocument).GetProperties().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "OrderId", "TenantId", "AffiliateAccountId", "AttributionId", "ClickId", "ProductId", "ConvertedAtUtc" }, names);
    }

    [Fact]
    public void ProductionIndexValidation_RequiresExactPartialClickFilter()
    {
        var keys = new BsonDocument("clickId", 1);
        var partial = new BsonDocument("clickId", new BsonDocument("$type", "string"));
        var index = new BsonDocument { { "key", keys }, { "unique", true }, { "partialFilterExpression", partial } };
        var options = new CreateIndexOptions { Unique = true };
        Assert.True(MongoIndexManagementPolicy.Matches(index, keys, options, partial));
        Assert.False(MongoIndexManagementPolicy.Matches(index, keys, options));
        Assert.False(MongoIndexManagementPolicy.Matches(index, keys, options,
            new BsonDocument("clickId", new BsonDocument("$exists", true))));
    }

    [Theory]
    [InlineData(1, "2026-11-15T00:00:00Z")]
    [InlineData(30, "2026-10-17T00:00:00Z")]
    [InlineData(999, "2025-11-16T00:00:00Z")]
    public void DateWindow_IsInclusiveUtcCalendarDays(int range, string expectedSince)
    {
        var window = AffiliateConversionAnalyticsQuery.Window(range,
            DateTime.Parse("2026-11-15T21:00:00Z").ToUniversalTime());
        Assert.Equal(DateTime.Parse(expectedSince).ToUniversalTime(), window.Since);
        Assert.Equal(DateTime.Parse("2026-11-16T00:00:00Z").ToUniversalTime(), window.Until);
    }

    [Fact]
    public void RepairController_IsTenantDerivedAdminOnlyAndExplicitOrderOnly()
    {
        var type = typeof(AdminAffiliateConversionReconciliationController);
        Assert.Equal("Owner,Admin", type.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        var post = type.GetMethod("Repair")!;
        Assert.Contains(post.GetParameters(), p => p.Name == "orderId" && p.ParameterType == typeof(Guid));
        Assert.DoesNotContain(post.GetParameters(), p => p.Name is "tenantId" or "affiliateId" or "clickId");
    }

    [Fact]
    public void LegacyUnsafeLatestClickAndPaymentConversionPathsAreRemoved()
    {
        var root = TestConfiguration.GetSolutionRoot();
        var analytics = File.ReadAllText(Path.Combine(root,"PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateAnalyticsConsumers.cs"));
        var payment = File.ReadAllText(Path.Combine(root,"PointOfSale.Infrastructure/Consumers/Affiliate/AffiliatePaymentCapturedConsumers.cs"));
        Assert.DoesNotContain("Sort.Descending", analytics);
        Assert.DoesNotContain("most recent unconverted click", analytics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FindOneAndUpdateAsync", payment);
        Assert.DoesNotContain("TotalConversions incremented", payment);
    }

    [Fact]
    public void CheckoutEvent_IsExplicitlyStoredInsideTransaction()
    {
        var root = TestConfiguration.GetSolutionRoot();
        var publisher = File.ReadAllText(Path.Combine(root,"PointOfSale.Infrastructure/EventBus/MassTransitEventPublisher.cs"));
        Assert.Contains("AffiliateOrderPlacedEvent", publisher);
        Assert.Contains("_dbContext.OutboxMessages.Add", publisher);
        var checkout = File.ReadAllText(Path.Combine(root,"PointOfSale.API/Services/Checkout/CheckoutService.cs"));
        var emit = checkout.IndexOf("EmitOrderCreatedEventAsync(", StringComparison.Ordinal);
        Assert.True(emit >= 0 && emit < checkout.IndexOf("transaction.CommitAsync", emit, StringComparison.Ordinal));
    }

    [Fact]
    public void ConsumedClick_RotatesOnlyOnExplicitProductEntry()
    {
        var root = TestConfiguration.GetSolutionRoot();
        var middleware = File.ReadAllText(Path.Combine(root,
            "PointOfSale.API/Middleware/AffiliateReferralMiddleware.cs"));
        Assert.Contains("evidenceClickAlreadyAttributed", middleware);
        Assert.Contains("isProductEntry &&", middleware);
        var resolver = middleware[middleware.IndexOf("ResolveClickJourneyAsync(", StringComparison.Ordinal)..];
        Assert.DoesNotContain("ReferralClickId == clickId", resolver);
    }

    [Fact]
    public void UiStatesExactMetricSemanticsAndUnknownDenominator()
    {
        var root = TestConfiguration.GetSolutionRoot();
        var ui = File.ReadAllText(Path.Combine(root,"PointOfSale.Web/src/modules/affiliate/pages/portal/AnalyticsTab.jsx"));
        var i18n = File.ReadAllText(Path.Combine(root,"PointOfSale.Web/src/modules/affiliate/i18n/translations.js"));
        Assert.Contains("summary?.conversionRate == null", ui);
        Assert.Contains("Conversions are committed attributed orders", i18n);
        Assert.Contains("Click events are not unique visitors", i18n);
        Assert.Contains("التحويلات طلبات منسوبة محفوظة", i18n);
        Assert.Contains("name={ta('attributedOrders')}", ui);
        var controller = File.ReadAllText(Path.Combine(root,
            "PointOfSale.API/Controllers/AffiliateAnalyticsController.cs"));
        Assert.Contains("clicks = productId.HasValue ? (long?)null", controller);
    }
}
