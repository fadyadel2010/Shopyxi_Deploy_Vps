using System.Data;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Services.Governance;
using PointOfSale.API.SuperAdmin.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Pagination;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services.CommerceHub;

public interface ICommerceHubSellerApplicationService
{
    Task<CommerceHubSellerApplicationStatusDto> GetMerchantStatusAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
    Task<CommerceHubSellerApplicationStatusDto> SubmitAsync(Guid tenantId, Guid userId, CommerceHubSellerApplicationSubmitRequest request, CancellationToken cancellationToken = default);
    Task<CommerceHubSellerApplicationSearchResultDto> SearchAsync(CommerceHubSellerApplicationListQuery query, CancellationToken cancellationToken = default);
    Task<CommerceHubSellerApplicationDetailsDto?> GetDetailsAsync(Guid applicationId, CancellationToken cancellationToken = default);
    Task<CommerceHubSellerApplicationDetailsDto> ApproveAsync(Guid applicationId, Guid reviewerId, string? reviewerIdentity, CancellationToken cancellationToken = default);
    Task<CommerceHubSellerApplicationDetailsDto> RejectAsync(Guid applicationId, Guid reviewerId, string? reviewerIdentity, string reason, CancellationToken cancellationToken = default);
}

public sealed class CommerceHubSellerApplicationException : Exception
{
    public CommerceHubSellerApplicationException(string code, string message, int statusCode) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}

public sealed class CommerceHubSellerApplicationService : ICommerceHubSellerApplicationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IUserGovernanceService _governanceService;
    private readonly ISuperAdminAuditService _auditService;

    public CommerceHubSellerApplicationService(
        ApplicationDbContext dbContext,
        IUserGovernanceService governanceService,
        ISuperAdminAuditService auditService)
    {
        _dbContext = dbContext;
        _governanceService = governanceService;
        _auditService = auditService;
    }

    public async Task<CommerceHubSellerApplicationStatusDto> GetMerchantStatusAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tenantId, cancellationToken)
            ?? throw new CommerceHubSellerApplicationException("TENANT_NOT_FOUND", "Merchant tenant was not found.", StatusCodes.Status404NotFound);

        var actor = await _dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == userId && item.TenantId == tenantId, cancellationToken)
            ?? throw new CommerceHubSellerApplicationException("MERCHANT_ACTOR_NOT_FOUND", "Authenticated merchant actor was not found.", StatusCodes.Status403Forbidden);

        var history = await _dbContext.CommerceHubSellerApplications
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .OrderByDescending(item => item.Revision)
            .ToListAsync(cancellationToken);

        var governance = await _dbContext.UserGovernanceProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.SubjectId == tenantId && item.Role == UserGovernanceRole.Vendor,
                cancellationToken);

        var publishingEnabled = governance is
        {
            Status: UserGovernanceStatus.Active,
            IsKycVerified: true,
            IsMarketplaceApproved: true
        } && tenant.Status == TenantStatus.Active && tenant.DeletedAtUtc == null;
        var current = history.FirstOrDefault();
        var status = publishingEnabled
            ? "Approved"
            : current == null
                ? "NotApplied"
                : MapMerchantStatus(current.Status);

        return new CommerceHubSellerApplicationStatusDto
        {
            TenantId = tenantId,
            Status = status,
            PublishingEnabled = publishingEnabled,
            CanSubmit = current == null && !publishingEnabled,
            CanResubmit = current?.Status == CommerceHubSellerApplicationStatus.Rejected && !publishingEnabled,
            ExistingVerificationRequired = governance?.IsKycVerified != true,
            Prefill = new CommerceHubSellerApplicationPrefillDto
            {
                StoreDisplayName = tenant.Name,
                ContactName = actor.FullName.Trim(),
                ContactPhone = actor.Phone ?? string.Empty,
                ContactEmail = !string.IsNullOrWhiteSpace(tenant.ContactEmail) ? tenant.ContactEmail : actor.Email,
                StorefrontUrl = tenant.StorefrontBaseUrl
            },
            CurrentApplication = current == null ? null : Map(current),
            History = history.Select(Map).ToList()
        };
    }

    public async Task<CommerceHubSellerApplicationStatusDto> SubmitAsync(
        Guid tenantId,
        Guid userId,
        CommerceHubSellerApplicationSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantEligible = await _dbContext.Tenants
            .AsNoTracking()
            .AnyAsync(
                item => item.Id == tenantId &&
                        item.Status == TenantStatus.Active &&
                        item.DeletedAtUtc == null,
                cancellationToken);
        if (!tenantEligible)
        {
            throw new CommerceHubSellerApplicationException(
                "MERCHANT_NOT_ELIGIBLE",
                "Merchant tenant no longer exists or is not active.",
                StatusCodes.Status409Conflict);
        }

        var actorEligible = await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                item => item.Id == userId &&
                        item.TenantId == tenantId &&
                        item.IsActive &&
                        !item.IsDeleted,
                cancellationToken);
        if (!actorEligible)
        {
            throw new CommerceHubSellerApplicationException(
                "MERCHANT_ACTOR_NOT_FOUND",
                "Authenticated merchant actor was not found.",
                StatusCodes.Status403Forbidden);
        }

        var marketplaceApproved = await _dbContext.UserGovernanceProfiles
            .AsNoTracking()
            .AnyAsync(
                item => item.SubjectId == tenantId &&
                        item.Role == UserGovernanceRole.Vendor &&
                        item.IsMarketplaceApproved,
                cancellationToken);
        if (marketplaceApproved)
        {
            throw new CommerceHubSellerApplicationException(
                "SELLER_APPLICATION_ALREADY_APPROVED",
                "This merchant already has marketplace publishing approval.",
                StatusCodes.Status409Conflict);
        }

        var normalized = NormalizeRequest(request);
        var applications = _dbContext.CommerceHubSellerApplications.Where(item => item.TenantId == tenantId);
        if (await applications.AnyAsync(item => item.Status == CommerceHubSellerApplicationStatus.Submitted, cancellationToken))
        {
            throw new CommerceHubSellerApplicationException(
                "SELLER_APPLICATION_ALREADY_PENDING",
                "A seller application is already waiting for review.",
                StatusCodes.Status409Conflict);
        }

        var latest = await applications
            .OrderByDescending(item => item.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest?.Status == CommerceHubSellerApplicationStatus.Approved)
        {
            throw new CommerceHubSellerApplicationException(
                "SELLER_APPLICATION_ALREADY_APPROVED",
                "This merchant already has an approved seller application.",
                StatusCodes.Status409Conflict);
        }

        var revision = (latest?.Revision ?? 0) + 1;
        var now = DateTime.UtcNow;
        var application = CommerceHubSellerApplication.Submit(
            tenantId,
            CreateReference(now),
            revision,
            normalized.StoreDisplayName,
            normalized.BusinessCategory,
            normalized.BusinessDescription,
            normalized.ContactName,
            normalized.ContactPhone,
            normalized.ContactEmail,
            JsonSerializer.Serialize(normalized.ProductCategories),
            normalized.StorefrontUrl,
            userId,
            now);

        _dbContext.CommerceHubSellerApplications.Add(application);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new CommerceHubSellerApplicationException(
                "SELLER_APPLICATION_SUBMISSION_CONFLICT",
                "The seller application changed while it was being submitted. Refresh and try again.",
                StatusCodes.Status409Conflict);
        }

        return await GetMerchantStatusAsync(tenantId, userId, cancellationToken);
    }

    public async Task<CommerceHubSellerApplicationSearchResultDto> SearchAsync(
        CommerceHubSellerApplicationListQuery request,
        CancellationToken cancellationToken = default)
    {
        var page = PaginationGuard.NormalizePage(request.Page);
        var pageSize = PaginationGuard.NormalizePageSize(request.PageSize);
        var query =
            from application in _dbContext.CommerceHubSellerApplications.AsNoTracking()
            join tenant in _dbContext.Tenants.AsNoTracking().IgnoreQueryFilters()
                on application.TenantId equals tenant.Id
            select new { application, tenant };

        var status = (request.Status ?? "Pending").Trim().ToLowerInvariant();
        query = status switch
        {
            "pending" or "submitted" => query.Where(row => row.application.Status == CommerceHubSellerApplicationStatus.Submitted),
            "approved" => query.Where(row => row.application.Status == CommerceHubSellerApplicationStatus.Approved),
            "rejected" => query.Where(row => row.application.Status == CommerceHubSellerApplicationStatus.Rejected),
            "all" => query,
            _ => throw new CommerceHubSellerApplicationException("INVALID_STATUS_FILTER", "Seller application status filter is invalid.", StatusCodes.Status400BadRequest)
        };

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLowerInvariant();
            query = query.Where(row =>
                row.application.StoreDisplayName.ToLower().Contains(search) ||
                row.application.ReferenceNumber.ToLower().Contains(search) ||
                row.application.BusinessCategory.ToLower().Contains(search) ||
                (row.tenant.Slug != null && row.tenant.Slug.ToLower().Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(row => row.application.SubmittedAtUtc)
            .ThenBy(row => row.application.ReferenceNumber)
            .Skip(PaginationGuard.GetSkip(page, pageSize))
            .Take(pageSize)
            .Select(row => new CommerceHubSellerApplicationListItemDto
            {
                Id = row.application.Id,
                TenantId = row.application.TenantId,
                TenantReference = row.tenant.Slug ?? row.tenant.Id.ToString(),
                MerchantName = row.application.StoreDisplayName,
                BusinessCategory = row.application.BusinessCategory,
                ReferenceNumber = row.application.ReferenceNumber,
                Revision = row.application.Revision,
                Status = row.application.Status.ToString(),
                SubmittedAtUtc = row.application.SubmittedAtUtc,
                ReviewedAtUtc = row.application.ReviewedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new CommerceHubSellerApplicationSearchResultDto
        {
            Applications = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize)
        };
    }

    public async Task<CommerceHubSellerApplicationDetailsDto?> GetDetailsAsync(
        Guid applicationId,
        CancellationToken cancellationToken = default)
    {
        var application = await _dbContext.CommerceHubSellerApplications
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == applicationId, cancellationToken);
        if (application == null)
        {
            return null;
        }

        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(item => item.Id == application.TenantId, cancellationToken);
        var governance = await _dbContext.UserGovernanceProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.SubjectId == application.TenantId && item.Role == UserGovernanceRole.Vendor,
                cancellationToken);
        var history = await _dbContext.CommerceHubSellerApplications
            .AsNoTracking()
            .Where(item => item.TenantId == application.TenantId)
            .OrderByDescending(item => item.Revision)
            .ToListAsync(cancellationToken);

        return new CommerceHubSellerApplicationDetailsDto
        {
            Application = Map(application),
            TenantName = tenant.Name,
            TenantSlug = tenant.Slug,
            TenantStatus = tenant.Status.ToString(),
            IsKycVerified = governance?.IsKycVerified == true,
            IsMarketplaceApproved = governance?.IsMarketplaceApproved == true,
            History = history.Select(Map).ToList()
        };
    }

    public Task<CommerceHubSellerApplicationDetailsDto> ApproveAsync(
        Guid applicationId,
        Guid reviewerId,
        string? reviewerIdentity,
        CancellationToken cancellationToken = default) =>
        ReviewAsync(applicationId, reviewerId, reviewerIdentity, approve: true, reason: null, cancellationToken);

    public Task<CommerceHubSellerApplicationDetailsDto> RejectAsync(
        Guid applicationId,
        Guid reviewerId,
        string? reviewerIdentity,
        string reason,
        CancellationToken cancellationToken = default) =>
        ReviewAsync(applicationId, reviewerId, reviewerIdentity, approve: false, reason, cancellationToken);

    private async Task<CommerceHubSellerApplicationDetailsDto> ReviewAsync(
        Guid applicationId,
        Guid reviewerId,
        string? reviewerIdentity,
        bool approve,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (reviewerId == Guid.Empty)
        {
            throw new CommerceHubSellerApplicationException("SUPER_ADMIN_CONTEXT_REQUIRED", "A valid Super Admin reviewer is required.", StatusCodes.Status403Forbidden);
        }

        var normalizedReviewer = string.IsNullOrWhiteSpace(reviewerIdentity) ? "CommerceHub Super Admin" : reviewerIdentity.Trim();
        var normalizedReason = approve ? null : NormalizeRejectionReason(reason);
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        var application = await _dbContext.CommerceHubSellerApplications
            .SingleOrDefaultAsync(item => item.Id == applicationId, cancellationToken)
            ?? throw new CommerceHubSellerApplicationException("SELLER_APPLICATION_NOT_FOUND", "Seller application was not found.", StatusCodes.Status404NotFound);
        if (application.Status != CommerceHubSellerApplicationStatus.Submitted)
        {
            throw new CommerceHubSellerApplicationException("SELLER_APPLICATION_ALREADY_RESOLVED", "Seller application has already been resolved.", StatusCodes.Status409Conflict);
        }

        var tenantExists = await _dbContext.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(
                item => item.Id == application.TenantId && item.Status == TenantStatus.Active && item.DeletedAtUtc == null,
                cancellationToken);
        if (!tenantExists)
        {
            throw new CommerceHubSellerApplicationException("MERCHANT_NOT_ELIGIBLE", "Merchant tenant no longer exists or is not active.", StatusCodes.Status409Conflict);
        }

        var previousState = application.Status.ToString();
        var reviewedAtUtc = DateTime.UtcNow;
        try
        {
            if (approve)
            {
                application.Approve(reviewerId, normalizedReviewer, reviewedAtUtc);
                await _governanceService.SetVendorMarketplaceApprovalAsync(
                    application.TenantId,
                    approved: true,
                    $"Seller application {application.ReferenceNumber} approved.",
                    reviewerId,
                    normalizedReviewer,
                    cancellationToken);
            }
            else
            {
                application.Reject(reviewerId, normalizedReviewer, normalizedReason!, reviewedAtUtc);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CommerceHubSellerApplicationException("SELLER_APPLICATION_REVIEW_CONFLICT", "Another Super Admin resolved this application first.", StatusCodes.Status409Conflict);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            throw new CommerceHubSellerApplicationException("SELLER_APPLICATION_REVIEW_CONFLICT", "Another Super Admin resolved this application first.", StatusCodes.Status409Conflict);
        }

        await _auditService.LogActionAsync(
            action: approve ? "COMMERCEHUB_SELLER_APPLICATION_APPROVED" : "COMMERCEHUB_SELLER_APPLICATION_REJECTED",
            entityType: "CommerceHubSellerApplication",
            entityId: application.Id.ToString(),
            description: approve ? "Super Admin approved a CommerceHub seller application." : "Super Admin rejected a CommerceHub seller application.",
            success: true,
            details: new
            {
                applicationId = application.Id,
                tenantId = application.TenantId,
                previousState,
                newState = application.Status.ToString(),
                reviewerId,
                decisionTimestampUtc = reviewedAtUtc,
                decisionReason = normalizedReason,
                applicationRevision = application.Revision,
                applicationReference = application.ReferenceNumber
            },
            actorId: reviewerId,
            actorEmail: normalizedReviewer,
            cancellationToken: cancellationToken);

        return await GetDetailsAsync(application.Id, cancellationToken)
            ?? throw new InvalidOperationException("Reviewed seller application could not be reloaded.");
    }

    private static CommerceHubSellerApplicationSubmitRequest NormalizeRequest(CommerceHubSellerApplicationSubmitRequest request)
    {
        var categories = request.ProductCategories
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Where(item => item.Length <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (categories.Count == 0 || categories.Count > 10)
        {
            throw new CommerceHubSellerApplicationException("INVALID_PRODUCT_CATEGORIES", "Provide between 1 and 10 product categories.", StatusCodes.Status400BadRequest);
        }

        var email = request.ContactEmail.Trim();
        try
        {
            _ = new MailAddress(email);
        }
        catch (FormatException)
        {
            throw new CommerceHubSellerApplicationException("INVALID_CONTACT_EMAIL", "Provide a valid business contact email.", StatusCodes.Status400BadRequest);
        }

        string? storefrontUrl = null;
        if (!string.IsNullOrWhiteSpace(request.StorefrontUrl))
        {
            storefrontUrl = request.StorefrontUrl.Trim();
            if (!Uri.TryCreate(storefrontUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new CommerceHubSellerApplicationException("INVALID_STOREFRONT_URL", "Storefront URL must be an absolute HTTP or HTTPS URL.", StatusCodes.Status400BadRequest);
            }
        }

        return new CommerceHubSellerApplicationSubmitRequest
        {
            StoreDisplayName = request.StoreDisplayName.Trim(),
            BusinessCategory = request.BusinessCategory.Trim(),
            BusinessDescription = request.BusinessDescription.Trim(),
            ContactName = request.ContactName.Trim(),
            ContactPhone = request.ContactPhone.Trim(),
            ContactEmail = email,
            ProductCategories = categories,
            StorefrontUrl = storefrontUrl
        };
    }

    private static string NormalizeRejectionReason(string? reason)
    {
        var normalized = reason?.Trim() ?? string.Empty;
        if (normalized.Length < 10 || normalized.Length > 1000)
        {
            throw new CommerceHubSellerApplicationException("REJECTION_REASON_REQUIRED", "Rejection reason must be between 10 and 1000 characters.", StatusCodes.Status400BadRequest);
        }
        return normalized;
    }

    private static string CreateReference(DateTime submittedAtUtc) =>
        $"CHS-{submittedAtUtc:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(5))}";

    private static string MapMerchantStatus(CommerceHubSellerApplicationStatus status) => status switch
    {
        CommerceHubSellerApplicationStatus.Submitted => "PendingReview",
        CommerceHubSellerApplicationStatus.Approved => "Approved",
        CommerceHubSellerApplicationStatus.Rejected => "Rejected",
        _ => "NotApplied"
    };

    private static CommerceHubSellerApplicationDto Map(CommerceHubSellerApplication application) => new()
    {
        Id = application.Id,
        TenantId = application.TenantId,
        ReferenceNumber = application.ReferenceNumber,
        Revision = application.Revision,
        Status = MapMerchantStatus(application.Status),
        StoreDisplayName = application.StoreDisplayName,
        BusinessCategory = application.BusinessCategory,
        BusinessDescription = application.BusinessDescription,
        ContactName = application.ContactName,
        ContactPhone = application.ContactPhone,
        ContactEmail = application.ContactEmail,
        ProductCategories = JsonSerializer.Deserialize<List<string>>(application.ProductCategoriesJson) ?? new List<string>(),
        StorefrontUrl = application.StorefrontUrl,
        SubmittedAtUtc = application.SubmittedAtUtc,
        ReviewedBySuperAdminId = application.ReviewedBySuperAdminId,
        ReviewerIdentity = application.ReviewerIdentity,
        ReviewedAtUtc = application.ReviewedAtUtc,
        RejectionReason = application.RejectionReason
    };
}
