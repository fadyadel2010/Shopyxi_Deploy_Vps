using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.SuperAdmin.Models;

namespace PointOfSale.API.SuperAdmin.Controllers;

[Route("api/super-admin/commercehub/seller-applications")]
public sealed class SuperAdminSellerApplicationsController : SuperAdminBaseController
{
    private readonly ICommerceHubSellerApplicationService _service;

    public SuperAdminSellerApplicationsController(ICommerceHubSellerApplicationService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] CommerceHubSellerApplicationListQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.SearchAsync(query, cancellationToken);
            return OkResponse(result, new Dictionary<string, object?>
            {
                ["page"] = result.Page,
                ["pageSize"] = result.PageSize,
                ["totalCount"] = result.TotalCount,
                ["totalPages"] = result.TotalPages
            });
        }
        catch (CommerceHubSellerApplicationException exception)
        {
            return ErrorResponse(exception.StatusCode, exception.Code, exception.Message);
        }
    }

    [HttpGet("{applicationId:guid}")]
    public async Task<IActionResult> Get(Guid applicationId, CancellationToken cancellationToken)
    {
        var result = await _service.GetDetailsAsync(applicationId, cancellationToken);
        return result == null
            ? ErrorResponse(StatusCodes.Status404NotFound, "SELLER_APPLICATION_NOT_FOUND", "Seller application was not found.")
            : OkResponse(result);
    }

    [HttpPost("{applicationId:guid}/approve")]
    [EnableRateLimiting(SuperAdminSecurityConstants.GovernanceWriteRateLimitPolicyName)]
    public Task<IActionResult> Approve(Guid applicationId, CancellationToken cancellationToken) =>
        ExecuteReviewAsync(applicationId, approve: true, reason: null, cancellationToken);

    [HttpPost("{applicationId:guid}/reject")]
    [EnableRateLimiting(SuperAdminSecurityConstants.GovernanceWriteRateLimitPolicyName)]
    public Task<IActionResult> Reject(
        Guid applicationId,
        [FromBody] CommerceHubSellerApplicationRejectRequest request,
        CancellationToken cancellationToken) =>
        ExecuteReviewAsync(applicationId, approve: false, request.Reason, cancellationToken);

    private async Task<IActionResult> ExecuteReviewAsync(
        Guid applicationId,
        bool approve,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!CurrentSuperAdminId.HasValue || CurrentSuperAdminId.Value == Guid.Empty)
        {
            return ErrorResponse(StatusCodes.Status403Forbidden, "SUPER_ADMIN_CONTEXT_REQUIRED", "A valid Super Admin reviewer is required.");
        }

        try
        {
            var result = approve
                ? await _service.ApproveAsync(applicationId, CurrentSuperAdminId.Value, CurrentSuperAdminEmail, cancellationToken)
                : await _service.RejectAsync(applicationId, CurrentSuperAdminId.Value, CurrentSuperAdminEmail, reason ?? string.Empty, cancellationToken);
            return OkResponse(result);
        }
        catch (CommerceHubSellerApplicationException exception)
        {
            return ErrorResponse(exception.StatusCode, exception.Code, exception.Message);
        }
        catch (PointOfSale.API.Services.Governance.UserGovernanceException exception)
        {
            return ErrorResponse(exception.StatusCode, exception.Code, exception.Message);
        }
    }
}
