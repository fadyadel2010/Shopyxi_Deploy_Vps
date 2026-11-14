#if DEBUG
namespace PointOfSale.API.Development;

// Registration only: never instantiate/start a driver or resolve SQL/services.
// Keep the same explicit local-file Development gate used by Program.
internal static class AffiliatePhase52LocalDriverRegistration
{
    internal static void AddIfEnabled(IServiceCollection services, IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment() && File.Exists(Path.Combine(environment.ContentRootPath,
                "..", ".ai", "evidence", "affiliate-p52-20261022", "runtime.enabled")))
            services.AddHostedService<AffiliatePhase52LocalDriver>();
    }
}
#endif
