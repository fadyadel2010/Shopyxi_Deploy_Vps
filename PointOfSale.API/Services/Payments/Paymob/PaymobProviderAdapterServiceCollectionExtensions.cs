using PointOfSale.API.Services.Payments.ProviderContracts;

namespace PointOfSale.API.Services.Payments.Paymob;

public static class PaymobProviderAdapterServiceCollectionExtensions
{
    public static IServiceCollection AddPaymobProviderAdapter(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IPaymobPaymentAdapter)))
        {
            throw new InvalidOperationException("Paymob provider adapter duplicate registration is not allowed.");
        }

        services.AddSingleton<IPaymobSensitiveArtifactProtector, PaymobSensitiveArtifactProtector>();
        services.AddHostedService<PaymobSensitiveArtifactBackfillHostedService>();
        services.AddScoped<IPaymobAdapterCredentialHandoff, PaymobAdapterCredentialHandoff>();
        services.AddScoped<IPaymobOfficialApiClient, PaymobOfficialApiHttpClient>();
        services.AddScoped<IPaymobProviderCheckpointStore, PaymobProviderCheckpointStore>();
        services.AddScoped<IPaymobPaymentAdapter, PaymobPaymentAdapter>();
        services.AddScoped<IPaymobAdvancedOperationsAdapter, PaymobAdvancedOperationsAdapter>();
        services.AddScoped<IPaymobCapabilityProvider, PaymobCapabilityProvider>();
        services.AddSingleton<IPaymobWebhookVerifier, PaymobWebhookVerifier>();
        services.AddScoped<IPaymobPaymentProvider, PaymobPaymentProvider>();
#if DEBUG
        services.AddScoped<IPaymentProviderAdapter>(sp =>
        {
            var adapter = ActivatorUtilities.CreateInstance<PaymobAdapter>(sp);
            var environment = sp.GetRequiredService<IWebHostEnvironment>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            return environment.IsDevelopment() &&
                configuration.GetValue<bool>("CommerceHub:DevelopmentAcceptance:PaymentSessions")
                ? new PointOfSale.API.Development.CommerceHub.P520DevelopmentPaymentAdapter(
                    adapter, sp.GetRequiredService<PointOfSale.Infrastructure.Data.ApplicationDbContext>(), environment, configuration)
                : adapter;
        });
#else
        services.AddScoped<IPaymentProviderAdapter, PaymobAdapter>();
#endif

        return services;
    }
}
