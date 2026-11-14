using Microsoft.AspNetCore.Http;
using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.API.Middleware;

public sealed class PrivateInternalRequestAdmissionMiddleware(
    RequestDelegate next,
    RuntimeAdmissionPolicy admission)
{
    public static bool AllowsProbe(string method, PathString path) =>
        (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) &&
        (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("/health/live", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("/health/marketplace", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("/metrics", StringComparison.OrdinalIgnoreCase));

    public Task InvokeAsync(HttpContext context)
    {
        if (admission.BusinessProcessingEnabled || AllowsProbe(context.Request.Method, context.Request.Path))
            return next(context);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync("Private internal deployment; business admission is closed.");
    }
}
