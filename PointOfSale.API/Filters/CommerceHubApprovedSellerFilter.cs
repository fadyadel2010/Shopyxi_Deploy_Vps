using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PointOfSale.API.Services.Governance;
using PointOfSale.Core.Interfaces;

namespace PointOfSale.API.Filters;

public sealed class CommerceHubApprovedSellerFilter : IAsyncActionFilter
{
    private readonly ITenantContext _tenantContext;
    private readonly IUserGovernanceService _governanceService;

    public CommerceHubApprovedSellerFilter(
        ITenantContext tenantContext,
        IUserGovernanceService governanceService)
    {
        _tenantContext = tenantContext;
        _governanceService = governanceService;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        try
        {
            await _governanceService.EnsureVendorListingAllowedAsync(
                _tenantContext.RequiredTenantId,
                context.HttpContext.RequestAborted);
        }
        catch (UserGovernanceException exception)
        {
            context.Result = new ObjectResult(new
            {
                code = exception.Code,
                message = exception.Message
            })
            {
                StatusCode = exception.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden
                    ? exception.StatusCode
                    : StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}
