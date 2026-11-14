using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PointOfSale.API.DTOs.GeographicAdministration;
using PointOfSale.API.Services.Admin;
using PointOfSale.Core.Entities.Geographic;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Security;
using PointOfSale.API.SuperAdmin.Models;
using PointOfSale.API.SuperAdmin.Infrastructure;
using PointOfSale.API.Attributes;

namespace PointOfSale.API.Controllers;

[ApiController]
[Route("api/admin/geographic")]
[Authorize]
public sealed class AdminGeographicAdministrationController : ControllerBase
{
    private readonly IGeographicAdministrationService _service;
    private readonly ITenantContext? _tenant;

    public AdminGeographicAdministrationController(IGeographicAdministrationService service, ITenantContext? tenant = null)
    {
        _service = service;
        _tenant = tenant;
    }

    [HttpPost("catalog/drafts")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> CreateCatalogDraft(CreateCatalogDraftRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.CreateCatalogDraftAsync(request, actorId, role, cancellationToken));

    [HttpPost("catalog/import/preview")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    [RequestSizeLimit(4_194_304)]
    public async Task<IActionResult> PreviewCatalogImport(CatalogImportManifest request, CancellationToken cancellationToken) =>
        Ok(await _service.PreviewCatalogImportAsync(request, cancellationToken));

    [HttpPost("catalog/import/drafts")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    [RequestSizeLimit(4_194_304)]
    public Task<IActionResult> StageCatalogImport(StageCatalogImportRequest request, CancellationToken cancellationToken) =>
        Execute((actorId, role) => _service.StageCatalogImportAsync(request, actorId, role, cancellationToken));

    [HttpGet("catalog/releases")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public async Task<IActionResult> ListCatalogReleases(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await _service.ListCatalogReleasesAsync(cancellationToken));
    }

    [HttpGet("catalog/releases/{catalogReleaseId:guid}")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public async Task<IActionResult> ReadCatalogRelease(Guid catalogReleaseId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await _service.ReadCatalogReleaseAsync(catalogReleaseId, cancellationToken);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPut("catalog/drafts/{catalogReleaseId:guid}")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> EditCatalogDraft(Guid catalogReleaseId, EditCatalogDraftRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.EditCatalogDraftAsync(catalogReleaseId, request, actorId, role, cancellationToken));

    [HttpPost("catalog/places")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> AddCatalogPlace(CatalogPlaceRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.AddCatalogPlaceAsync(request, actorId, role, cancellationToken));

    [HttpPost("catalog/localizations")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> AddLocalization(LocalizationRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.AddLocalizationAsync(request, actorId, role, cancellationToken));

    [HttpPost("catalog/aliases")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> AddAlias(AliasRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.AddAliasAsync(request, actorId, role, cancellationToken));

    [HttpPost("catalog/country-profiles")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> AddCountryProfile(CountryProfileRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.AddCountryProfileAsync(request, actorId, role, cancellationToken));

    [HttpPost("catalog/hierarchy-edges")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> AddHierarchyEdge(CatalogHierarchyEdgeRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.AddHierarchyEdgeAsync(request, actorId, role, cancellationToken));

    [HttpGet("catalog/releases/{catalogReleaseId:guid}/validation")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public async Task<IActionResult> InspectCatalogDraft(Guid catalogReleaseId, CancellationToken cancellationToken) =>
        Ok(await _service.InspectCatalogDraftAsync(catalogReleaseId, cancellationToken));

    [HttpPost("catalog/releases/{catalogReleaseId:guid}/validate")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> ValidateCatalogDraft(Guid catalogReleaseId, CatalogValidationRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.ValidateCatalogDraftAsync(catalogReleaseId, request, actorId, role, cancellationToken));

    [HttpPost("catalog/merges")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> AddMerge(PlaceMergeRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.AddMergeAsync(request, actorId, role, cancellationToken));

    [HttpPost("catalog/deprecations")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> AddDeprecation(PlaceDeprecationRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.AddDeprecationAsync(request, actorId, role, cancellationToken));

    [HttpPost("catalog/releases/{catalogReleaseId:guid}/publish")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> PublishCatalogRelease(Guid catalogReleaseId, ReleaseActionRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.PublishCatalogReleaseAsync(catalogReleaseId, request, actorId, role, cancellationToken));

    [HttpPost("catalog/releases/{targetCatalogReleaseId:guid}/rollback")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName), AllowAnonymousTenant]
    public Task<IActionResult> RollbackCatalogRelease(Guid targetCatalogReleaseId, ReleaseActionRequest request, CancellationToken cancellationToken) => Execute((actorId, role) => _service.RollbackCatalogReleaseAsync(targetCatalogReleaseId, request, actorId, role, cancellationToken));

    [HttpPost("carrier-mappings/drafts")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public Task<IActionResult> CreateMappingDraft(CreateMappingDraftRequest request, CancellationToken cancellationToken) =>
        _tenant == null || request.TenantId != _tenant.RequiredTenantId || request.AccountScopeId != request.TenantId || request.AccountScopeKind != CarrierGeographicAccountScopeKind.TenantProviderAccount
            ? Task.FromResult<IActionResult>(Forbid()) : Execute((actorId, role) => _service.CreateMappingDraftAsync(request, actorId, role, cancellationToken));

    [HttpPost("carrier-mappings")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> AddMapping(MappingRequest request, CancellationToken cancellationToken) =>
        !await OwnsMapping(request.MappingReleaseId, cancellationToken) ? Forbid() : await Execute((actorId, role) => _service.AddMappingAsync(request, actorId, role, cancellationToken));

    [HttpPost("carrier-mappings/releases/{mappingReleaseId:guid}/publish")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> PublishMappingRelease(Guid mappingReleaseId, ReleaseActionRequest request, CancellationToken cancellationToken) =>
        !await OwnsMapping(mappingReleaseId, cancellationToken) ? Forbid() : await Execute((actorId, role) => _service.PublishMappingReleaseAsync(mappingReleaseId, request, actorId, role, cancellationToken));

    [HttpGet("carrier-mappings/releases/{mappingReleaseId:guid}/validation")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> InspectMappingDraft(Guid mappingReleaseId, CancellationToken cancellationToken) =>
        !await OwnsMapping(mappingReleaseId, cancellationToken) ? Forbid() : Ok(await _service.InspectMappingDraftAsync(mappingReleaseId, cancellationToken));

    [HttpPost("carrier-mappings/releases/{mappingReleaseId:guid}/validate")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> ValidateMappingDraft(Guid mappingReleaseId, MappingValidationRequest request, CancellationToken cancellationToken) =>
        !await OwnsMapping(mappingReleaseId, cancellationToken) ? Forbid() : await Execute((actorId, role) => _service.ValidateMappingDraftAsync(mappingReleaseId, request, actorId, role, cancellationToken));

    [HttpPost("carrier-mappings/releases/{targetMappingReleaseId:guid}/rollback")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> RollbackMappingRelease(Guid targetMappingReleaseId, ReleaseActionRequest request, CancellationToken cancellationToken) =>
        !await OwnsMapping(targetMappingReleaseId, cancellationToken) ? Forbid() : await Execute((actorId, role) => _service.RollbackMappingReleaseAsync(targetMappingReleaseId, request, actorId, role, cancellationToken));

    private Task<bool> OwnsMapping(Guid id, CancellationToken ct) => _tenant == null ? Task.FromResult(false) : _service.MappingBelongsToTenantAsync(id, _tenant.RequiredTenantId, ct);

    [HttpPost("sync/runs")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public Task<IActionResult> RecordSyncRun(SyncRunRequest request, CancellationToken cancellationToken) =>
        !OwnsScope(request.TenantId, request.AccountScopeId, request.AccountScopeKind) ? Task.FromResult<IActionResult>(Forbid()) :
            Execute((actorId, role) => _service.RecordSyncRunAsync(request, actorId, role, cancellationToken));

    [HttpPost("sync/candidates")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> AddSyncCandidate(SyncCandidateRequest request, CancellationToken cancellationToken) =>
        !await OwnsSync(request.SyncRunId, false, cancellationToken) ? Forbid() : await Execute((actorId, role) => _service.AddSyncCandidateAsync(request, actorId, role, cancellationToken));

    [HttpPost("sync/candidates/{syncCandidateId:guid}/approve")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> ApproveSyncCandidate(Guid syncCandidateId, CandidateReviewRequest request, CancellationToken cancellationToken) =>
        !await OwnsSync(syncCandidateId, true, cancellationToken) ? Forbid() : await Execute((actorId, role) => _service.ReviewSyncCandidateAsync(syncCandidateId, CarrierGeographicSyncCandidateStatus.Approved, request, actorId, role, cancellationToken));

    [HttpPost("sync/candidates/{syncCandidateId:guid}/reject")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> RejectSyncCandidate(Guid syncCandidateId, CandidateReviewRequest request, CancellationToken cancellationToken) =>
        !await OwnsSync(syncCandidateId, true, cancellationToken) ? Forbid() : await Execute((actorId, role) => _service.ReviewSyncCandidateAsync(syncCandidateId, CarrierGeographicSyncCandidateStatus.Rejected, request, actorId, role, cancellationToken));

    [HttpPost("overrides")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public Task<IActionResult> CreateOverride(OverrideRequest request, CancellationToken cancellationToken) =>
        !OwnsScope(request.TenantId, request.AccountScopeId, request.AccountScopeKind) ? Task.FromResult<IActionResult>(Forbid()) :
            Execute((actorId, role) => _service.CreateOverrideAsync(request, actorId, role, cancellationToken));

    [HttpPost("overrides/{overrideId:guid}/approve")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> ApproveOverride(Guid overrideId, ReleaseActionRequest request, CancellationToken cancellationToken) =>
        _tenant == null || !await _service.OverrideBelongsToTenantAsync(overrideId, _tenant.RequiredTenantId, cancellationToken) ? Forbid() :
            await Execute((actorId, role) => _service.ApproveOverrideAsync(overrideId, request, actorId, role, cancellationToken));

    [HttpGet("audit")]
    [Authorize(Roles = "Owner,Admin,Manager", Policy = PosPermissions.ShippingManage)]
    public async Task<IActionResult> GetAudit([FromQuery] int limit = 100, CancellationToken cancellationToken = default)
    {
        return _tenant == null ? Forbid() : Ok(await _service.GetTenantAuditAsync(_tenant.RequiredTenantId, limit, cancellationToken));
    }

    private bool OwnsScope(Guid? tenantId, Guid? scopeId, CarrierGeographicAccountScopeKind kind) =>
        _tenant != null && tenantId == _tenant.RequiredTenantId && scopeId == tenantId && kind == CarrierGeographicAccountScopeKind.TenantProviderAccount;

    private Task<bool> OwnsSync(Guid id, bool candidate, CancellationToken ct) => _tenant == null ? Task.FromResult(false) :
        _service.SyncBelongsToTenantAsync(id, _tenant.RequiredTenantId, candidate, ct);

    private async Task<IActionResult> Execute(Func<Guid, string, Task<GeographicOperationResultDto>> action)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Forbid();
        }

        var result = await action(actorId, ResolveActorRole());
        return result.Succeeded ? Ok(result) : BadRequest(result);
    }

    private Guid ResolveActorId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("userId");
        return Guid.TryParse(value, out var actorId) ? actorId : Guid.Empty;
    }

    private string ResolveActorRole()
    {
        if (SuperAdminTokenClaimsValidator.HasRequiredClaims(User)) return SuperAdminSecurityConstants.Role;
        if (User.IsInRole("Owner")) return "Owner";
        if (User.IsInRole("Admin")) return "Admin";
        if (User.IsInRole("Manager")) return "Manager";
        return "POSAdmin";
    }
}
