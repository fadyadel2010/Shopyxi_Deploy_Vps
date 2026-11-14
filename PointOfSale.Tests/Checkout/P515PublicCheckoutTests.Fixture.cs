using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PointOfSale.API.DTOs;
using PointOfSale.API.Services;
using PointOfSale.Core.Application.ProductAuthoring;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.ProductAuthoring;
using PointOfSale.Infrastructure.Consumers;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.MongoDB.Documents;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Checkout;

public sealed partial class P515PublicCheckoutTests
{
    private sealed record Fixture(Guid Tenant, Guid Actor, Guid Warehouse, Guid Shipping, Guid PaymentMethod, Guid Product, Guid Variant, string Slug, string ProductSlug);

    private static async Task<Fixture> CreateFixture(IsolatedFactory factory)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("P515_API_PAUSED"));
        Assert.Empty(Process.GetProcessesByName("PointOfSale.API")); // Shared outbox workers must not see isolated test events.
        if (Guid.TryParse(Environment.GetEnvironmentVariable("P515_RESUME_TENANT"), out var resumeTenant))
        {
            var saved = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(Path.Combine(TestConfiguration.GetSolutionRoot(), $".ai/reports/p515-fixture-{resumeTenant}.json")))!;
            Assert.Equal(resumeTenant, saved.Tenant);
            Assert.Equal("p515-" + resumeTenant.ToString("N"), saved.Slug);
            using var resumeScope = factory.Services.CreateScope();
            var resumeDb = resumeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var resumeStore = await resumeDb.Tenants.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == saved.Tenant);
            ValidateResumeTenant(resumeStore, saved.Tenant, saved.Slug);
            var config = await resumeDb.TenantPaymentMethods.IgnoreQueryFilters().SingleAsync(m => m.Id == saved.PaymentMethod && m.TenantId == saved.Tenant && m.IsSandbox && m.Code == "STRIPE");
            using var keys = JsonDocument.Parse(resumeScope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("PaymentMethodSecrets").Unprotect(config.ConfigEncrypted!));
            factory.PublishableKey = keys.RootElement.GetProperty("publishableKey").GetString()!;
            factory.SecretKey = keys.RootElement.GetProperty("secretKey").GetString()!;
            factory.WebhookSecret = keys.RootElement.GetProperty("webhookSecret").GetString()!;
            Assert.StartsWith("pk_test_p515_", factory.PublishableKey);
            Assert.StartsWith("sk_test_p515_", factory.SecretKey);
            Assert.StartsWith("whsec_p515_", factory.WebhookSecret);
            factory.FixtureTenant = saved.Tenant;
            return saved;
        }
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var warehouse = Guid.NewGuid(); var shipping = Guid.NewGuid();
        var slug = "p515-" + tenant.ToString("N"); var label = "P515 INTERNAL NO DELIVERY NO EXTERNAL PAYMENT " + tenant.ToString("N");
        factory.FixtureTenant = tenant;
        await File.WriteAllTextAsync(Path.Combine(TestConfiguration.GetSolutionRoot(), $".ai/reports/p515-fixture-start-{tenant}.json"),
            JsonSerializer.Serialize(new { tenant, actor, warehouse, shipping, slug }));
        ((TenantContext)services.GetRequiredService<ITenantContext>()).SetTenantId(tenant);
        var db = services.GetRequiredService<ApplicationDbContext>();
        await db.Database.OpenConnectionAsync();
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT current_database(),host(inet_server_addr()),current_setting('transaction_read_only')";
            await using var reader = await cmd.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal("PosEcommerceDb_Baseline", reader.GetString(0)); Assert.Equal("192.168.1.5", reader.GetString(1)); Assert.Equal("off", reader.GetString(2));
        }
        var category = new Category { Id = Guid.NewGuid(), TenantId = tenant, Name = label };
        var unit = new PointOfSale.Core.Entities.Unit { Id = Guid.NewGuid(), TenantId = tenant, Name = "Piece", Symbol = "pc", IsActive = true };
        var payment = new TenantPaymentMethod { Id = Guid.NewGuid(), TenantId = tenant, Code = "STRIPE", DisplayName = "P515 isolated test card", IsEnabled = true,
            IsSandbox = true, SupportsOnline = true, Version = 1,
            ConfigEncrypted = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("PaymentMethodSecrets").Protect(JsonSerializer.Serialize(new {
                publishableKey = factory.PublishableKey, secretKey = factory.SecretKey, webhookSecret = factory.WebhookSecret })) };
        db.AddRange(new Tenant { Id = tenant, Name = label, Slug = slug, Currency = "EGP", Country = "EG", IsActive = true, Status = TenantStatus.Active },
            new User { Id = actor, TenantId = tenant, Username = slug, Email = slug + "@example.invalid", NormalizedEmail = (slug + "@example.invalid").ToUpperInvariant(),
                NormalizedUsername = slug.ToUpperInvariant(), Role = UserRole.Owner, IsActive = true, FirstName = "P515", LastName = "INTERNAL TEST", PasswordHash = "NO LOGIN - TEST FIXTURE ONLY" },
            category, unit, payment, new Warehouse { Id = warehouse, TenantId = tenant, Name = label, IsActive = true, IsDefault = true },
            new ShippingMethod { Id = shipping, TenantId = tenant, Name = "P515 manual shipping — NEVER DISPATCH", IsActive = true, BaseRate = 10m, FulfillmentWarehouseId = warehouse },
            new StoreConfig { Id = Guid.NewGuid(), TenantId = tenant, StoreName = label, StorefrontStatus = "live", CommerceAiEnabled = false });
        await db.SaveChangesAsync();
        await new TenantAccountSeeder(db, services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TenantAccountSeeder>>()).SeedAsync(tenant);
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "Owner"), new Claim("tenant_id", tenant.ToString())], "P515 fixture")) };
        var productSlug = slug + "-product";
        var command = new ProductAuthoringCommand(ProductAuthoringOperation.Create,
            new(tenant, null, category.Id, "P515-" + tenant.ToString("N"), null),
            new(label, null, "Internal checkout test. No real funds or delivery.", null, "P515", null, [], [], []),
            new(10m, 25m, 25m, false, 0m), new(PublishStatus.Published, true, true, true, false),
            new(productSlug, label, label, null), new(1m, 1m, 1m, 1m, "kg", "cm"),
            new("Piece", 0m, true, false, 0, false), new(unit.Id, unit.Id, [new(null, unit.Id, 1m, 10m, 25m, null, true, true)]), [],
            new(false, null, null, null, slug, slug + "-author", null, new Dictionary<string, string>()));
        var result = await services.GetRequiredService<IProductAuthoringOrchestrator>().AuthorAsync(command);
        Assert.True(result.Status == ProductAuthoringStatus.Accepted, JsonSerializer.Serialize(result));
        var product = result.ProductId!.Value;
        var variant = await db.ProductVariants.IgnoreQueryFilters().Where(v => v.ProductId == product && v.IsActive).Select(v => v.Id).SingleAsync();
        var opening = await services.GetRequiredService<IInventoryService>().SaveOpeningStockAsync(new SaveOpeningStockRequest { WarehouseId = warehouse,
            Items = [new() { VariantId = variant, Quantity = 20m, CostPrice = 10m, Notes = label }] });
        Assert.All(opening.Results, r => Assert.True(r.Success, r.Message));
        var write = await services.GetRequiredService<IProductSqlAuthorityProjectionWriter>().ApplyAsync(new(tenant, product,
            new ProductDocument { ProductId = product, TenantId = tenant }, Guid.NewGuid(), DateTime.UtcNow, checked((int)result.MutationVersion!.Value)));
        Assert.True(write.Applied);
        var shipEvent = new ShippingMethodUpdatedEvent { TenantId = tenant, ShippingMethodId = shipping, BaseRate = 10m, EventVersion = 1 };
        await services.GetRequiredService<ShippingMethodProjectionConsumer>().Consume(Mock.Of<ConsumeContext<ShippingMethodUpdatedEvent>>(c => c.Message == shipEvent && c.CancellationToken == CancellationToken.None));
        var paymentEvent = new PaymentMethodCreatedEvent { TenantId = tenant, PaymentMethodId = payment.Id, Code = "STRIPE", Version = 1 };
        await services.GetRequiredService<PaymentMethodProjectionConsumer>().Consume(Mock.Of<ConsumeContext<PaymentMethodCreatedEvent>>(c => c.Message == paymentEvent && c.CancellationToken == CancellationToken.None));
        var fixture = new Fixture(tenant, actor, warehouse, shipping, payment.Id, product, variant, slug, productSlug);
        await File.WriteAllTextAsync(Path.Combine(TestConfiguration.GetSolutionRoot(), $".ai/reports/p515-fixture-{tenant}.json"), JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
        return fixture;
    }

    internal static void ValidateResumeTenant(Tenant store, Guid expectedTenant, string expectedSlug)
    {
        // Completed/suspended fixtures are immutable acceptance evidence, not a pool
        // for another callback/order run. Check this before reading signing material.
        if (store.Id != expectedTenant || store.Slug != expectedSlug ||
            expectedSlug != "p515-" + expectedTenant.ToString("N") ||
            !store.Name.StartsWith("P515 INTERNAL NO DELIVERY NO EXTERNAL PAYMENT ", StringComparison.Ordinal) ||
            !store.IsActive || !store.IsOperational)
            throw new InvalidOperationException("P515 resume requires an active dedicated fixture. Retained suspended fixtures must not be reused.");
    }
}
