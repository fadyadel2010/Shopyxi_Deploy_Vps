using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.Core.Interfaces;

namespace PointOfSale.API.Controllers;

[ApiController]
[Authorize(Roles = "Owner,Admin")]
[Route("api/admin/commercehub/seller-application")]
public sealed class AdminCommerceHubSellerApplicationsController : ControllerBase
{
    private readonly ITenantContext _tenantContext;
    private readonly ICommerceHubSellerApplicationService _service;

    public AdminCommerceHubSellerApplicationsController(
        ITenantContext tenantContext,
        ICommerceHubSellerApplicationService service)
    {
        _tenantContext = tenantContext;
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.GetMerchantStatusAsync(
                _tenantContext.RequiredTenantId,
                RequireCurrentUserId(),
                cancellationToken));
        }
        catch (CommerceHubSellerApplicationException exception)
        {
            return StatusCode(exception.StatusCode, new { code = exception.Code, message = exception.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Submit(
        [FromBody] CommerceHubSellerApplicationSubmitRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.SubmitAsync(
                _tenantContext.RequiredTenantId,
                RequireCurrentUserId(),
                request,
                cancellationToken));
        }
        catch (CommerceHubSellerApplicationException exception)
        {
            return StatusCode(exception.StatusCode, new { code = exception.Code, message = exception.Message });
        }
    }

    private Guid RequireCurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(raw, out var userId) || userId == Guid.Empty)
        {
            throw new CommerceHubSellerApplicationException(
                "MERCHANT_ACTOR_REQUIRED",
                "A valid merchant actor is required.",
                StatusCodes.Status403Forbidden);
        }
        return userId;
    }
}
