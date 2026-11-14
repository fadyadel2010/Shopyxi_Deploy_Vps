using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MassTransit;
using Moq;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.EventBus;
using PointOfSale.API.Configuration;
using PointOfSale.API.Services.Payments.Stripe;
using PointOfSale.Tests.Affiliate;
using Stripe;

namespace PointOfSale.Tests.Checkout;

/// <summary>Opt-in production HTTP pipeline, verified development DB, no background workers or external HTTP.</summary>
[Collection("P515 isolated HTTP host")]
public sealed partial class P515PublicCheckoutTests
{
    [PrimaryAffiliateFact]
    public async Task IsolatedHost_UsesProductionPipeline_WithoutExternalHttpOrHostedWorkers()
    {
        await using var db = await AffiliateP53ePrimarySupport.Open("P515-verify-before-host");
        using var factory = new IsolatedFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(factory.Services.GetServices<IHostedService>(), worker => Assert.Equal("GenericWebHostService", worker.GetType().Name));
    }

    internal sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string? _priorDeterministicHost = Environment.GetEnvironmentVariable("REQUIRE_REAL_POSTGRES_TESTS");
        internal IsolatedFactory() => Environment.SetEnvironmentVariable("REQUIRE_REAL_POSTGRES_TESTS", "1");

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { Environment.SetEnvironmentVariable("REQUIRE_REAL_POSTGRES_TESTS", _priorDeterministicHost); }
        }

        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); }
            finally { Environment.SetEnvironmentVariable("REQUIRE_REAL_POSTGRES_TESTS", _priorDeterministicHost); }
        }
        internal Guid? FixtureTenant { get; set; }
        internal string PublishableKey { get; set; } = "pk_test_p515_" + Guid.NewGuid().ToString("N");
        internal string SecretKey { get; set; } = "sk_test_p515_" + Guid.NewGuid().ToString("N");
        internal string WebhookSecret { get; set; } = "whsec_p515_" + Guid.NewGuid().ToString("N");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // This existing switch removes hosted services and disables Hangfire storage initialization.
            builder.UseEnvironment("Development");
            builder.UseContentRoot(Path.Combine(Infrastructure.TestConfiguration.GetSolutionRoot(), "PointOfSale.API"));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["CommerceHub:Payments:Stripe:Enabled"] = "false",
                ["CommerceHub:Payments:Stripe:SandboxMode"] = "true",
                ["CommerceHub:Payments:Stripe:Environment"] = "Sandbox",
                ["CommerceHub:Payments:Stripe:FeatureFlags:Enabled"] = "true",
                ["CommerceHub:Payments:Stripe:FeatureFlags:SandboxEnabled"] = "true",
                ["CommerceHub:Payments:Stripe:FeatureFlags:ProductionEnabled"] = "false",
                ["CommerceHub:Payments:Stripe:FeatureFlags:PaymentIntentsEnabled"] = "true",
                ["CommerceHub:Payments:Stripe:FeatureFlags:WebhooksEnabled"] = "true",
            }));
            builder.ConfigureTestServices(services =>
            {
                for (var i = services.Count - 1; i >= 0; --i)
                    if (services[i].ServiceType == typeof(IHostedService) || services[i].ServiceType == typeof(IStripePaymentIntentClient)) services.RemoveAt(i);
                services.AddScoped<IStripePaymentIntentClient, IsolatedStripeClient>();
                services.RemoveAll<IEventPublisher>();
                services.AddScoped<IEventPublisher, InMemoryEventPublisher>();
                services.RemoveAll<IPublishEndpoint>();
                var endpoint = new Mock<IPublishEndpoint>(); endpoint.SetReturnsDefault(Task.CompletedTask);
                services.AddSingleton(endpoint.Object);
                services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new RejectOutboundHttp()));
            });
        }
    }

    private sealed class RejectOutboundHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("P515: external HTTP is forbidden in the isolated acceptance host.");
    }

    private sealed class IsolatedStripeClient(IStripeRuntimeCredentialAccessor credentials) : IStripePaymentIntentClient
    {
        public Task<Stripe.PaymentIntent> CreateAsync(PaymentIntentCreateOptions options, RequestOptions requestOptions, CancellationToken cancellationToken)
        {
            Assert.NotNull(options.Amount); Assert.True(options.Amount > 0); Assert.NotEmpty(requestOptions.IdempotencyKey);
            var id = "pi_p515_" + Guid.NewGuid().ToString("N");
            var intent = new Stripe.PaymentIntent { Id = id, ClientSecret = id + "_secret_INTERNAL_ONLY", Amount = options.Amount!.Value,
                Currency = options.Currency, Status = "requires_payment_method", Metadata = options.Metadata, Livemode = false, Created = DateTime.UtcNow };
            credentials.SetPaymentIntentArtifact(new(intent.Id, intent.ClientSecret, intent.Status, intent.Amount, intent.Currency));
            return Task.FromResult(intent);
        }
    }
}

[CollectionDefinition("P515 isolated HTTP host", DisableParallelization = true)]
public sealed class P515IsolatedHostCollection { }
