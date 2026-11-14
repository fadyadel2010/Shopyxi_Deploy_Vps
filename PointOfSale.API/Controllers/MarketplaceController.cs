using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenSearch.Client;
using PointOfSale.Core.Abstractions;
using PointOfSale.Core.DTOs.Marketplace;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Exceptions;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Security;
using PointOfSale.API.Services;
using PointOfSale.API.SuperAdmin.Models;
using PointOfSale.API.SuperAdmin.Infrastructure;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Elasticsearch.Configuration;
using PointOfSale.Infrastructure.Elasticsearch.Documents;
using PointOfSale.Infrastructure.Instrumentation;

namespace PointOfSale.API.Controllers;

[Authorize]
[ApiController]
[Route("api/marketplace")]
public class MarketplaceController : ControllerBase
{
    private const long MaxChatAttachmentBytes = 10 * 1024 * 1024;

    private readonly IMarketplaceSearchService _searchService;
    private readonly IMarketplaceProfileService _profileService;
    private readonly IServiceCatalogService _catalogService;
    private readonly IServiceRequestService _requestService;
    private readonly IServiceReviewService _reviewService;
    private readonly IChatService _chatService;
    private readonly IProviderTrustService _trustService;
    private readonly IFileValidationService _fileValidationService;
    private readonly IR2StorageService _storageService;
    private readonly ApplicationDbContext _context;
    private readonly IOpenSearchClient _elasticClient;
    private readonly IWebHostEnvironment _environment;
    private readonly IMarketplaceTelemetry _telemetry;
    private readonly ILogger<MarketplaceController> _logger;
    private readonly IMarketplaceSearchRepairAuthority _repairAuthority;
    private readonly PointOfSale.Infrastructure.Marketplace.Generation.IMarketplaceGenerationReads _generationReads;

    public MarketplaceController(
        IMarketplaceSearchService searchService,
        IMarketplaceProfileService profileService,
        IServiceCatalogService catalogService,
        IServiceRequestService requestService,
        IServiceReviewService reviewService,
        IChatService chatService,
        IProviderTrustService trustService,
        IFileValidationService fileValidationService,
        IR2StorageService storageService,
        ApplicationDbContext context,
        IOpenSearchClient elasticClient,
        IWebHostEnvironment environment,
        IMarketplaceTelemetry telemetry,
        ILogger<MarketplaceController> logger,
        IMarketplaceSearchRepairAuthority repairAuthority,
        PointOfSale.Infrastructure.Marketplace.Generation.IMarketplaceGenerationReads? generationReads = null)
    {
        _searchService = searchService;
        _profileService = profileService;
        _catalogService = catalogService;
        _requestService = requestService;
        _reviewService = reviewService;
        _chatService = chatService;
        _trustService = trustService;
        _fileValidationService = fileValidationService;
        _storageService = storageService;
        _context = context;
        _elasticClient = elasticClient;
        _environment = environment;
        _telemetry = telemetry;
        _logger = logger;
        _repairAuthority = repairAuthority;
        _generationReads = generationReads ?? PointOfSale.Infrastructure.Marketplace.Generation.InactiveMarketplaceGenerationReads.Instance;
    }

    /// <summary>
    /// Get current user's marketplace profile status.
    /// Returns capability flags for UI rendering.
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetMyProfile()
    {
        var userId = GetUserId();
        var profile = await _profileService.GetProfileByUserIdAsync(userId);

        return Ok(new
        {
            hasMarketplaceProfile = profile != null,
            profileId = profile?.Id,
            displayName = profile?.DisplayName,
            isActive = profile?.IsActive ?? false,
            bio = profile?.Bio,
            profileImageUrl = profile?.ProfileImageUrl,
            coverImageUrl = profile?.CoverImageUrl,
            location = profile?.Location,
            skills = profile?.Skills,
            portfolioLinks = profile?.PortfolioLinks
        });
    }

    /// <summary>
    /// Get profile statistics.
    /// </summary>
    [HttpGet("me/stats")]
    public async Task<IActionResult> GetMyProfileStats()
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();
        var profile = await _profileService.GetProfileByUserIdAsync(userId);

        if (profile == null || profile.TenantId != tenantId)
        {
            return NotFound(new { error = "MARKETPLACE_PROFILE_NOT_FOUND" });
        }

        var hasCurrentProviderVisibility =
            profile.IsActive &&
            !profile.IsDeleted &&
            await _context.Tenants
                .IgnoreQueryFilters()
                .AnyAsync(tenant =>
                    tenant.Id == tenantId &&
                    tenant.IsActive &&
                    tenant.Status == TenantStatus.Active &&
                    tenant.DeletedAtUtc == null) &&
            !await _context.UserGovernanceProfiles
                .AsNoTracking()
                .AnyAsync(governance =>
                    governance.SubjectId == tenantId &&
                    governance.Role == UserGovernanceRole.Vendor &&
                    (governance.Status == UserGovernanceStatus.Suspended ||
                     governance.Status == UserGovernanceStatus.Banned));

        if (!hasCurrentProviderVisibility)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "MARKETPLACE_PROVIDER_NOT_VISIBLE" });
        }

        // Analytics cards are derivative only; bind them to current SQL tenant/profile authority.
        var servicesCount = await _context.Set<Service>()
            .Where(s =>
                s.TenantId == tenantId &&
                s.ProfileId == profile.Id &&
                !s.IsDeleted)
            .CountAsync();

        var completedCount = await _context.Set<ServiceRequest>()
            .IgnoreQueryFilters() // Cross-tenant marketplace
            .Where(r =>
                r.TenantId == tenantId &&
                r.ProviderUserId == userId &&
                r.ProviderProfileId == profile.Id &&
                r.Status == Core.Enums.ServiceRequestStatus.CompletedByBuyer &&
                !r.IsDeleted)
            .CountAsync();

        var trust = await _trustService.GetTrustAggregateAsync(profile.Id);

        return Ok(new
        {
            tenantId,
            providerUserId = userId,
            providerProfileId = profile.Id,
            servicesOffered = servicesCount,
            completedRequests = completedCount,
            averageRating = trust?.AverageRating ?? 0m,
            totalReviews = trust?.TotalReviews ?? 0,
            trustBadge = trust?.ComputeBadge().ToString() ?? TrustBadge.New.ToString()
        });
    }

    /// <summary>
    /// Get reviews for a specific service.
    /// </summary>
    [HttpGet("services/{serviceId}/reviews")]
    [AllowAnonymous]
    public async Task<IActionResult> GetServiceReviews(Guid serviceId)
    {
        var service = await _catalogService.GetServiceByIdAsync(serviceId);
        if (service == null)
            return NotFound();

        var reviews = (await _reviewService.GetReviewsByServiceIdAsync(serviceId)).ToList();
        var providerUserId = service.Profile!.UserId;
        if (reviews.Any(review =>
                review.ServiceId != service.Id ||
                review.ProviderUserId != providerUserId))
        {
            _logger.LogError(
                "Service review history lineage mismatch | ServiceId: {ServiceId} | ProviderUserId: {ProviderUserId}",
                service.Id,
                providerUserId);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Service review history is temporarily unavailable." });
        }

        // Get buyer user IDs
        var buyerUserIds = reviews.Select(r => r.ReviewerUserId).Distinct().ToList();
        
        // Fetch buyer users
        var buyers = await _context.Set<User>()
            .IgnoreQueryFilters() // Cross-tenant marketplace
            .Where(u => buyerUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        if (buyerUserIds.Any(buyerUserId =>
                !buyers.TryGetValue(buyerUserId, out var buyerName) ||
                string.IsNullOrWhiteSpace(buyerName)))
        {
            _logger.LogWarning(
                "Service review history attribution unavailable | ServiceId: {ServiceId}",
                service.Id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Service review history is temporarily unavailable." });
        }

        var items = reviews.Select(r => new
        {
            reviewId = r.Id,
            serviceRequestId = r.ServiceRequestId,
            serviceId = r.ServiceId,
            reviewerUserId = r.ReviewerUserId,
            providerUserId = r.ProviderUserId,
            rating = r.Rating,
            comment = r.Comment,
            createdAt = r.CreatedAt,
            updatedAt = r.UpdatedAt ?? r.CreatedAt,
            buyerName = buyers[r.ReviewerUserId]!,
            // Phase MR-1: Marketing evidence
            marketingEvidence = r.MarketingEvidence != null ? new
            {
                campaignName = r.MarketingEvidence.CampaignName,
                platform = r.MarketingEvidence.Platform.ToString(),
                startDate = r.MarketingEvidence.StartDate,
                endDate = r.MarketingEvidence.EndDate,
                evidenceType = r.MarketingEvidence.EvidenceType.ToString(),
                ordersCount = r.MarketingEvidence.OrdersCount,
                revenueAmount = r.MarketingEvidence.RevenueAmount
            } : null
        }).ToList();

        return Ok(new
        {
            serviceId = service.Id,
            providerProfileId = service.ProfileId,
            providerUserId,
            totalReviews = items.Count,
            items
        });
    }

    /// <summary>
    /// Search marketplace services.
    /// Elasticsearch may return candidates; returned listing fields are recomposed from SQL authority.
    /// Phase DS-1: Input validation + sort whitelist.
    /// </summary>
    [HttpPost("search")]
    public async Task<IActionResult> SearchServices([FromBody] MarketplaceSearchRequest request)
    {
        var cancellationToken = HttpContext.RequestAborted;
        cancellationToken.ThrowIfCancellationRequested();
        // DS-1: Input validation & sanitization
        var allowedSorts = new[] { "relevance", "rating", "newest", "price_low", "price_high" };
        if (!allowedSorts.Contains(request.Sort))
            request = request with { Sort = "relevance" };

        if (request.Page <= 0)
            request = request with { Page = 1 };

        if (request.PageSize is < 1 or > 100)
            request = request with { PageSize = 24 };

        if (request.Query?.Length > 200)
            return BadRequest(new { error = "Query too long (max 200 characters)" });

        // Trim whitespace-only category
        if (!string.IsNullOrWhiteSpace(request.Category))
            request = request with { Category = request.Category.Trim() };
        else if (!string.IsNullOrWhiteSpace(request.CategoryId))
            request = request with { CategoryId = request.CategoryId.Trim() };

        try
        {
            var result = await _searchService.SearchServicesAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Ok(result);
        }
        catch (Exception ex)
        {
            // A request abort is not an availability failure or permission to
            // return successful/partial results. SDKs may wrap cancellation;
            // the request's actual token, not exception wording, is authority.
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogError(ex, "[MARKETPLACE-SEARCH] Search failed | Query: {Query} | Category: {Category}", 
                request.Query, request.EffectiveCategory);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Marketplace search is temporarily unavailable." });
        }
    }

    /// <summary>
    /// Get service details by ID.
    /// </summary>
    [HttpGet("services/{serviceId}")]
    public async Task<IActionResult> GetService(Guid serviceId)
    {
        var service = await _catalogService.GetServiceByIdAsync(serviceId);
        if (service == null)
            return NotFound();

        var trust = await _trustService.GetTrustAggregateAsync(service.ProfileId);
        var profile = service.Profile!;
        var requestEntry = await _requestService.GetRequestEntryAuthorityAsync(
            serviceId,
            GetUserId(),
            HttpContext.RequestAborted);
        var ratingDistribution = trust?.GetRatingDistribution();
        var ratingPercentages = ratingDistribution == null
            ? null
            : BuildRatingPercentages(ratingDistribution, trust!.TotalReviews);

        return Ok(new
        {
            serviceId = service.Id,
            title = service.Title,
            description = service.Description,
            category = service.Category,
            providerProfileId = service.ProfileId,
            providerDisplayName = profile.DisplayName,
            isActive = service.IsActive,
            createdAt = service.CreatedAt,
            startingPrice = service.StartingPrice,
            pricingType = service.PricingType?.ToString(),
            currency = service.Currency,
            serviceImages = service.ServiceImages,
            packages = service.Packages,
            requestEntry = new
            {
                canRequest = requestEntry.CanRequest,
                reason = requestEntry.Reason,
                activeRequestId = requestEntry.ActiveRequestId
            },
            // Phase FE-3: Provider profile details
            providerProfile = new
            {
                bio = profile.Bio,
                profileImageUrl = profile.ProfileImageUrl,
                coverImageUrl = profile.CoverImageUrl,
                location = profile.Location,
                skills = profile.Skills,
                portfolioLinks = profile.PortfolioLinks
            },
            trust = trust != null ? new
            {
                averageRating = trust.AverageRating,
                totalReviews = trust.TotalReviews,
                trustBadge = trust.ComputeBadge().ToString(),
                ratingDistribution,
                ratingPercentages
            } : null
        });
    }

    /// <summary>
    /// Request a service.
    /// Backend determines buyer/provider, UI never passes status.
    /// </summary>
    [HttpPost("requests")]
    public async Task<IActionResult> RequestService([FromBody] RequestServiceDto dto)
    {
        try
        {
            var userId = GetUserId();
            var request = await _requestService.RequestServiceAsync(userId, dto);

            return Ok(new
            {
                requestId = request.Id,
                serviceId = request.ServiceId,
                status = request.Status.ToString(),
                createdAt = request.CreatedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Marketplace request validation failed");
            return BadRequest(new { error = "The request could not be processed" });
        }
        catch (MarketplaceDomainException ex)
        {
            _logger.LogWarning(ex, "Marketplace domain validation failed");
            return ex.ErrorCode switch
            {
                "ACTIVE_REQUEST_EXISTS" => Conflict(new { error = ex.ErrorCode }),
                "SELF_REQUEST_FORBIDDEN" => StatusCode(
                    StatusCodes.Status403Forbidden,
                    new { error = ex.ErrorCode }),
                _ => BadRequest(new { error = ex.ErrorCode })
            };
        }
    }

    /// <summary>
    /// Get my service requests (as buyer).
    /// </summary>
    [HttpGet("my-requests")]
    public async Task<IActionResult> GetMyRequests()
    {
        var userId = GetUserId();
        var requests = await _requestService.GetRequestsByBuyerAsync(userId);

        // Get all request IDs to check for existing reviews
        var requestIds = requests.Select(r => r.Id).ToList();
        var existingReviews = await _context.Set<ServiceReview>()
            .IgnoreQueryFilters()
            .Where(r => requestIds.Contains(r.ServiceRequestId))
            .Select(r => new { r.ServiceRequestId, r.Id })
            .ToDictionaryAsync(r => r.ServiceRequestId, r => r.Id);

        return Ok(requests.Select(r => new
        {
                requestId = r.Id,
                serviceId = r.ServiceId,
                serviceTitle = r.ServiceTitleSnapshot,
                serviceCategory = r.ServiceCategorySnapshot,
                startingPrice = r.ServiceStartingPriceSnapshot,
                pricingType = r.ServicePricingTypeSnapshot?.ToString(),
                currency = r.ServiceCurrencySnapshot,
                providerDisplayName = r.Service?.Profile?.DisplayName,
                buyerUserId = r.BuyerUserId,
                providerUserId = r.ProviderUserId,
                status = r.Status.ToString(),
                requestMessage = r.RequestMessage,
            completionEvidence = BuildCompletionEvidence(r),
            canChat = r.AllowsInteraction(),
            canViewChatHistory = r.CanViewChatHistory(userId),
            canSendChat = r.AllowsInteraction(),
            canConfirmCompletion = r.Status == Core.Enums.ServiceRequestStatus.CompletedByProvider,
            canCancel = r.Status == Core.Enums.ServiceRequestStatus.Pending || r.Status == Core.Enums.ServiceRequestStatus.Accepted,
            canReview = r.CanBeReviewed() && !existingReviews.ContainsKey(r.Id),
            existingReviewId = existingReviews.GetValueOrDefault(r.Id),
            createdAt = r.CreatedAt
        }).ToList());
    }

    /// <summary>
    /// Get incoming service requests (as provider).
    /// </summary>
    [HttpGet("incoming-requests")]
    public async Task<IActionResult> GetIncomingRequests()
    {
        var userId = GetUserId();
        var requests = await _requestService.GetRequestsByProviderAsync(userId);

        return Ok(requests.Select(r => new
        {
                requestId = r.Id,
                serviceId = r.ServiceId,
                serviceTitle = r.ServiceTitleSnapshot,
                serviceCategory = r.ServiceCategorySnapshot,
                startingPrice = r.ServiceStartingPriceSnapshot,
                pricingType = r.ServicePricingTypeSnapshot?.ToString(),
                currency = r.ServiceCurrencySnapshot,
                buyerUserId = r.BuyerUserId,
                providerUserId = r.ProviderUserId,
                status = r.Status.ToString(),
                requestMessage = r.RequestMessage,
                completionEvidence = BuildCompletionEvidence(r),
                canAccept = r.Status == Core.Enums.ServiceRequestStatus.Pending,
                canReject = r.Status == Core.Enums.ServiceRequestStatus.Pending,
            canMarkCompleted = r.Status == Core.Enums.ServiceRequestStatus.Accepted,
            canChat = r.AllowsInteraction(),
            canViewChatHistory = r.CanViewChatHistory(userId),
            canSendChat = r.AllowsInteraction(),
            createdAt = r.CreatedAt
        }).ToList());
    }

    /// <summary>
    /// Get service request details with capabilities.
    /// </summary>
    [HttpGet("requests/{requestId}")]
    public async Task<IActionResult> GetRequest(Guid requestId)
    {
        var userId = GetUserId();
        var request = await _requestService.GetRequestByIdAsync(requestId);

        if (request == null)
            return NotFound();

        var isBuyer = request.BuyerUserId == userId;
        var isProvider = request.ProviderUserId == userId;

        if (!isBuyer && !isProvider)
            return Forbid();

        var existingReviewId = isBuyer
            ? await _context.Set<ServiceReview>()
                .IgnoreQueryFilters()
                .Where(review => review.ServiceRequestId == request.Id)
                .Select(review => (Guid?)review.Id)
                .FirstOrDefaultAsync()
            : null;

        return Ok(new
        {
            requestId = request.Id,
            serviceId = request.ServiceId,
            serviceTitle = request.ServiceTitleSnapshot,
            serviceCategory = request.ServiceCategorySnapshot,
            startingPrice = request.ServiceStartingPriceSnapshot,
            pricingType = request.ServicePricingTypeSnapshot?.ToString(),
            currency = request.ServiceCurrencySnapshot,
            packages = request.ServicePackagesSnapshot,
            buyerUserId = request.BuyerUserId,
            providerUserId = request.ProviderUserId,
            providerProfileId = request.ProviderProfileId,
            providerDisplayName = request.Service?.Profile?.DisplayName,
            status = request.Status.ToString(),
            requestMessage = request.RequestMessage,
            createdAt = request.CreatedAt,
            auditTimeline = BuildRequestAuditTimeline(request),
            completionEvidence = BuildCompletionEvidence(request),
            capabilities = new
            {
                canAccept = isProvider && request.Status == Core.Enums.ServiceRequestStatus.Pending,
                canReject = isProvider && request.Status == Core.Enums.ServiceRequestStatus.Pending,
                canMarkCompleted = isProvider && request.Status == Core.Enums.ServiceRequestStatus.Accepted,
                canConfirmCompletion = isBuyer && request.Status == Core.Enums.ServiceRequestStatus.CompletedByProvider,
                canCancel = (isBuyer || isProvider) && (request.Status == Core.Enums.ServiceRequestStatus.Pending || request.Status == Core.Enums.ServiceRequestStatus.Accepted),
                canChat = request.AllowsInteraction(),
                canViewChatHistory = request.CanViewChatHistory(userId),
                canSendChat = request.AllowsInteraction(),
                canReview = isBuyer && request.CanBeReviewed() && !existingReviewId.HasValue,
                existingReviewId
            }
        });
    }

    private static object BuildCompletionEvidence(ServiceRequest request)
    {
        return new
        {
            requestId = request.Id,
            status = request.Status.ToString(),
            providerCompletion = BuildCompletionEvidencePhase(
                request,
                "ProviderCompletion",
                request.CompletedByProviderAt,
                "Provider",
                request.ProviderUserId),
            buyerConfirmation = BuildCompletionEvidencePhase(
                request,
                "BuyerConfirmation",
                request.CompletedByBuyerAt,
                "Buyer",
                request.BuyerUserId),
            notes = Array.Empty<object>(),
            attachments = Array.Empty<object>(),
            deliverables = Array.Empty<object>()
        };
    }

    private static Dictionary<int, decimal> BuildRatingPercentages(
        IReadOnlyDictionary<int, int> ratingDistribution,
        int totalReviews)
    {
        return ratingDistribution.ToDictionary(
            entry => entry.Key,
            entry => totalReviews == 0
                ? 0m
                : Math.Round(
                    entry.Value * 100m / totalReviews,
                    2,
                    MidpointRounding.AwayFromZero));
    }

    private static object? BuildCompletionEvidencePhase(
        ServiceRequest request,
        string evidenceType,
        DateTime? occurredAt,
        string actorRole,
        Guid actorUserId)
    {
        if (!occurredAt.HasValue)
        {
            return null;
        }

        var occurredAtUtc = DateTime.SpecifyKind(occurredAt.Value, DateTimeKind.Utc);
        return new
        {
            evidenceId = $"{request.Id:N}:{evidenceType}:{occurredAtUtc.Ticks}",
            requestId = request.Id,
            evidenceType,
            occurredAt = occurredAtUtc,
            actorRole,
            actorUserId
        };
    }

    private static IReadOnlyList<object> BuildRequestAuditTimeline(ServiceRequest request)
    {
        var events = new List<RequestAuditTimelineEvent>();

        AddEvent(events, request, "Requested", request.CreatedAt, "Buyer", request.BuyerUserId, Core.Enums.ServiceRequestStatus.Pending);
        AddEvent(events, request, "Accepted", request.AcceptedAt, "Provider", request.ProviderUserId, Core.Enums.ServiceRequestStatus.Accepted);
        AddEvent(events, request, "Rejected", request.RejectedAt, "Provider", request.ProviderUserId, Core.Enums.ServiceRequestStatus.Rejected);
        AddEvent(events, request, "CompletedByProvider", request.CompletedByProviderAt, "Provider", request.ProviderUserId, Core.Enums.ServiceRequestStatus.CompletedByProvider);
        AddEvent(events, request, "CompletedByBuyer", request.CompletedByBuyerAt, "Buyer", request.BuyerUserId, Core.Enums.ServiceRequestStatus.CompletedByBuyer);
        AddEvent(events, request, "Cancelled", request.CancelledAt, null, null, Core.Enums.ServiceRequestStatus.Cancelled);

        return events
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Sequence)
            .Select(e => new
            {
                eventId = $"{e.RequestId:N}:{e.EventType}:{e.OccurredAt.Ticks}",
                requestId = e.RequestId,
                eventType = e.EventType,
                status = e.Status.ToString(),
                occurredAt = e.OccurredAt,
                actorRole = e.ActorRole,
                actorUserId = e.ActorUserId
            })
            .Cast<object>()
            .ToList();
    }

    private static void AddEvent(
        ICollection<RequestAuditTimelineEvent> events,
        ServiceRequest request,
        string eventType,
        DateTime? occurredAt,
        string? actorRole,
        Guid? actorUserId,
        Core.Enums.ServiceRequestStatus status)
    {
        if (!occurredAt.HasValue)
        {
            return;
        }

        events.Add(new RequestAuditTimelineEvent(
            request.Id,
            eventType,
            status,
            DateTime.SpecifyKind(occurredAt.Value, DateTimeKind.Utc),
            actorRole,
            actorUserId,
            events.Count));
    }

    private sealed record RequestAuditTimelineEvent(
        Guid RequestId,
        string EventType,
        Core.Enums.ServiceRequestStatus Status,
        DateTime OccurredAt,
        string? ActorRole,
        Guid? ActorUserId,
        int Sequence);

    /// <summary>
    /// Accept a service request (provider only).
    /// </summary>
    [HttpPost("requests/{requestId}/accept")]
    public async Task<IActionResult> AcceptRequest(Guid requestId)
    {
        var userId = GetUserId();
        var request = await _requestService.AcceptRequestAsync(requestId, userId);

        return Ok(new { status = request.Status.ToString() });
    }

    /// <summary>
    /// Reject a service request (provider only).
    /// </summary>
    [HttpPost("requests/{requestId}/reject")]
    public async Task<IActionResult> RejectRequest(Guid requestId)
    {
        var userId = GetUserId();
        var request = await _requestService.RejectRequestAsync(requestId, userId);

        return Ok(new { status = request.Status.ToString() });
    }

    /// <summary>
    /// Mark request as completed by provider.
    /// </summary>
    [HttpPost("requests/{requestId}/complete")]
    public async Task<IActionResult> MarkCompleted(Guid requestId)
    {
        var userId = GetUserId();
        var request = await _requestService.MarkCompletedByProviderAsync(requestId, userId);

        return Ok(new { status = request.Status.ToString() });
    }

    /// <summary>
    /// Confirm completion as buyer.
    /// </summary>
    [HttpPost("requests/{requestId}/confirm")]
    public async Task<IActionResult> ConfirmCompletion(Guid requestId)
    {
        var userId = GetUserId();
        var request = await _requestService.ConfirmCompletedByBuyerAsync(requestId, userId);

        return Ok(new { status = request.Status.ToString() });
    }

    /// <summary>
    /// Cancel a service request.
    /// </summary>
    [HttpPost("requests/{requestId}/cancel")]
    public async Task<IActionResult> CancelRequest(Guid requestId, [FromBody] CancelRequestDto dto)
    {
        var userId = GetUserId();
        var request = await _requestService.CancelRequestAsync(requestId, userId, dto.Reason);

        return Ok(new { status = request.Status.ToString() });
    }

    /// <summary>
    /// Get chat messages for a request.
    /// PHASE CR-1: Server-authoritative tenant validation.
    /// </summary>
    [HttpGet("requests/{requestId}/chat/messages")]
    public async Task<IActionResult> GetChatMessages(
        Guid requestId,
        [FromQuery] Guid? beforeMessageId = null)
    {
        const int historyPageSize = 50;
        var userId = GetUserId();
        var tenantId = GetTenantId();
        
        var canView = await _chatService.CanUserViewConversationAsync(requestId, userId, tenantId);

        if (!canView)
            return Forbid();

        var historyWindow = (await _chatService.GetMessagesAsync(
            requestId,
            userId,
            tenantId,
            historyPageSize + 1,
            beforeMessageId)).ToList();
        var hasMore = historyWindow.Count > historyPageSize;
        var messages = hasMore
            ? historyWindow.Skip(historyWindow.Count - historyPageSize).ToList()
            : historyWindow;

        var items = messages.Select(m => new
        {
            id = m.Id,
            serviceRequestId = m.ServiceRequestId,
            senderUserId = m.SenderUserId,
            senderRole = m.SenderRole.ToString(),
            messageType = m.MessageType.ToString(),
            body = m.Body,
            createdAt = m.CreatedAt,
            attachmentId = ResolveAttachmentId(m),
            attachmentFileName = ResolveAttachmentFileName(m)
        }).ToList();

        return Ok(new
        {
            requestId,
            beforeMessageId,
            pageSize = historyPageSize,
            hasMore,
            nextBeforeMessageId = hasMore ? messages[0].Id : (Guid?)null,
            items
        });
    }

    [HttpPost("service-reviews")]
    public async Task<IActionResult> CreateServiceReview(
        [FromBody] CreateReviewDto reviewRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            var review = await _reviewService.CreateReviewAsync(
                GetUserId(),
                reviewRequest,
                cancellationToken);

            return StatusCode(StatusCodes.Status201Created, new
            {
                reviewId = review.Id,
                serviceRequestId = review.ServiceRequestId,
                serviceId = review.ServiceId,
                reviewerUserId = review.ReviewerUserId,
                providerUserId = review.ProviderUserId,
                rating = review.Rating,
                createdAt = review.CreatedAt
            });
        }
        catch (MarketplaceDomainException ex)
        {
            _logger.LogWarning(ex, "Service marketplace review creation rejected");
            return ex.ErrorCode switch
            {
                "REQUEST_NOT_FOUND" => NotFound(new { error = ex.ErrorCode }),
                "UNAUTHORIZED" => StatusCode(
                    StatusCodes.Status403Forbidden,
                    new { error = ex.ErrorCode }),
                "REVIEW_EXISTS" => Conflict(new { error = ex.ErrorCode }),
                "REVIEW_NOT_ALLOWED" => Conflict(new { error = ex.ErrorCode }),
                _ => BadRequest(new { error = ex.ErrorCode })
            };
        }
    }


    /// <summary>
    /// Get my services (provider only).
    /// </summary>
    [HttpGet("my-services")]
    public async Task<IActionResult> GetMyServices()
    {
        var userId = GetUserId();
        var profile = await _profileService.GetProfileByUserIdAsync(userId);

        if (profile == null)
            return Ok(new List<object>());

        var services = await _catalogService.GetServicesByProfileIdAsync(profile.Id);

        return Ok(services.Select(s => new
        {
            serviceId = s.Id,
            title = s.Title,
            description = s.Description,
            category = s.Category,
            isActive = s.IsActive,
            createdAt = s.CreatedAt,
            startingPrice = s.StartingPrice,
            pricingType = s.PricingType?.ToString(),
            currency = s.Currency,
            serviceImages = s.ServiceImages
        }).ToList());
    }

    /// <summary>
    /// Create a new service (provider only).
    /// </summary>
    [HttpPost("my-services")]
    public async Task<IActionResult> CreateService([FromBody] CreateServiceDto dto)
    {
        var userId = GetUserId();
        var profile = await _profileService.GetProfileByUserIdAsync(userId);

        if (profile == null)
            return BadRequest(new { error = "You must create a marketplace profile first" });

        var service = await _catalogService.CreateServiceAsync(profile.Id, userId, dto);

        return Ok(new
        {
            serviceId = service.Id,
            title = service.Title,
            description = service.Description,
            category = service.Category,
            isActive = service.IsActive
        });
    }

    /// <summary>
    /// Update a service.
    /// </summary>
    [HttpPut("my-services/{serviceId}")]
    public async Task<IActionResult> UpdateService(Guid serviceId, [FromBody] UpdateServiceDto dto)
    {
        var userId = GetUserId();
        var service = await _catalogService.UpdateServiceAsync(serviceId, userId, dto);

        return Ok(new
        {
            serviceId = service.Id,
            title = service.Title,
            description = service.Description,
            category = service.Category
        });
    }

    /// <summary>
    /// Activate a service.
    /// </summary>
    [HttpPost("my-services/{serviceId}/activate")]
    public async Task<IActionResult> ActivateService(Guid serviceId)
    {
        var userId = GetUserId();
        await _catalogService.ActivateServiceAsync(serviceId, userId);

        return Ok(new { isActive = true });
    }

    /// <summary>
    /// Deactivate a service.
    /// </summary>
    [HttpPost("my-services/{serviceId}/deactivate")]
    public async Task<IActionResult> DeactivateService(Guid serviceId)
    {
        var userId = GetUserId();
        await _catalogService.DeactivateServiceAsync(serviceId, userId);

        return Ok(new { isActive = false });
    }

    /// <summary>
    /// Create marketplace profile.
    /// </summary>
    [HttpPost("profile")]
    public async Task<IActionResult> CreateProfile([FromBody] CreateMarketplaceProfileDto dto)
    {
        var userId = GetUserId();
        var profile = await _profileService.CreateProfileAsync(userId, dto);

        return Ok(new
        {
            profileId = profile.Id,
            displayName = profile.DisplayName,
            bio = profile.Bio
        });
    }

    /// <summary>
    /// Update marketplace profile.
    /// </summary>
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateMarketplaceProfileDto dto)
    {
        var userId = GetUserId();
        
        // Get user's profile
        var profile = await _profileService.GetProfileByUserIdAsync(userId);
        if (profile == null)
            return NotFound(new { error = "Profile not found" });

        var updatedProfile = await _profileService.UpdateProfileAsync(profile.Id, userId, dto);

        return Ok(new
        {
            profileId = updatedProfile.Id,
            displayName = updatedProfile.DisplayName,
            bio = updatedProfile.Bio,
            profileImageUrl = updatedProfile.ProfileImageUrl,
            coverImageUrl = updatedProfile.CoverImageUrl,
            location = updatedProfile.Location,
            skills = updatedProfile.Skills,
            portfolioLinks = updatedProfile.PortfolioLinks
        });
    }

    /// <summary>
    /// Reindex marketplace services to Elasticsearch.
    /// Tenant admins repair only their tenant's documents. Global nondestructive repair is
    /// exposed only by the RequireSuperAdmin route below; neither route recreates an index.
    /// </summary>
    [HttpPost("admin/reindex")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ReindexServices(CancellationToken cancellationToken)
    {
        return await ReindexTenantServicesAsync(cancellationToken);
    }

    /// <summary>
    /// Repair marketplace service search documents from current SQL authority.
    /// Missing/incompatible indices are never deleted or recreated in place.
    /// Requires current SQL super-admin platform authority.
    /// </summary>
    [HttpPost("admin/reindex/global")]
    [Authorize(Policy = SuperAdminSecurityConstants.PolicyName)]
    public async Task<IActionResult> ReindexServicesGlobally(CancellationToken cancellationToken)
    {
        if (!SuperAdminTokenClaimsValidator.HasRequiredClaims(User) ||
            !int.TryParse(User.FindFirst(SuperAdminSecurityConstants.TokenVersionClaim)?.Value, out var version))
            return Forbid();
        return await RepairServicesAsync(new(GetUserId(), null, version), cancellationToken);
    }

    private async Task<IActionResult> ReindexTenantServicesAsync(CancellationToken cancellationToken)
    {
        return await RepairServicesAsync(new(GetUserId(), GetTenantId(), null), cancellationToken);
    }

    private async Task<IActionResult> RepairServicesAsync(
        MarketplaceSearchRepairActor actor, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _repairAuthority.RepairAsync(actor, cancellationToken);
            if (result.Code == "REPAIR_AUTHORITY_REJECTED") return Forbid();
            var body = new { success = result.Success, code = result.Code, tenantId = actor.TenantId,
                indexedServices = result.IndexedServices, deletedServices = result.DeletedServices,
                errors = result.Errors, scope = actor.TenantId.HasValue ? "tenant" : "global",
                message = result.Success
                    ? actor.TenantId.HasValue
                        ? "Tenant marketplace services reindexed successfully"
                        : "All active marketplace services reindexed successfully"
                    : $"Marketplace service repair incomplete with {result.Errors} errors" };
            return result.Success ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Marketplace search repair failed | Actor: {ActorId} | Tenant: {TenantId}", actor.UserId, actor.TenantId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, code = "REPAIR_UNAVAILABLE",
                message = "Marketplace search repair is unavailable" });
        }
    }

    /// <summary>
    /// Marketplace diagnostics endpoint - detects Elasticsearch drift.
    /// PHASE: Marketplace Root Cause Investigation
    /// Compares PostgreSQL vs Elasticsearch counts to identify missing services.
    /// </summary>
    [HttpGet("admin/diagnostics")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetMarketplaceDiagnostics(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();

        _logger.LogWarning(
            "Admin requested marketplace diagnostics | TenantId: {TenantId} | User: {User}",
            tenantId, User.Identity?.Name ?? "Unknown");

        try
        {
            // SECURITY: Tenant-admin diagnostics are tenant-scoped. Global
            // marketplace diagnostics require a dedicated super-admin route.
            await using var generationRead = await _generationReads.BeginAsync(tenantId,
                PointOfSale.Infrastructure.Marketplace.Generation.MarketplaceGenerationReadScope.QueryHash(
                    new MarketplaceSearchRequest { Query = "admin-diagnostics" }), null, false, cancellationToken);
            cancellationToken = generationRead.CancellationToken;
            var activeServicesQuery = _context.Set<Service>()
                .IgnoreQueryFilters()
                .Where(s =>
                    s.TenantId == tenantId &&
                    !s.IsDeleted &&
                    s.IsActive &&
                    s.Profile != null &&
                    !s.Profile.IsDeleted &&
                    s.Profile.IsActive &&
                    s.Profile.TenantId == s.TenantId &&
                    _context.Tenants.IgnoreQueryFilters().Any(tenant =>
                        tenant.Id == s.TenantId &&
                        tenant.Status == TenantStatus.Active &&
                        tenant.DeletedAtUtc == null) &&
                    !_context.UserGovernanceProfiles.AsNoTracking().Any(governance =>
                        governance.SubjectId == s.TenantId &&
                        governance.Role == UserGovernanceRole.Vendor &&
                        (governance.Status == UserGovernanceStatus.Suspended ||
                         governance.Status == UserGovernanceStatus.Banned)));

            var pgCount = await activeServicesQuery.CountAsync(cancellationToken);

            // 2. Count documents in Elasticsearch
            var esCountResponse = await _elasticClient.CountAsync<MarketplaceServiceDocument>(c => c
                .Index(generationRead.PhysicalName)
                .Query(q => q.Bool(b => b
                    .Filter(
                        f => f.Term(t => t.Field(d => d.TenantId).Value(tenantId)),
                        f => f.Term(t => t.Field(d => d.IsActive).Value(true))))),
                ct: cancellationToken);

            var esCount = esCountResponse.IsValid ? (int)esCountResponse.Count : -1;
            var drift = pgCount - esCount;

            // 3. Get recent services from PostgreSQL
            var recentServices = await activeServicesQuery
                .OrderByDescending(s => s.CreatedAt)
                .Take(10)
                .Select(s => new
                {
                    serviceId = s.Id,
                    tenantId = s.TenantId,
                    title = s.Title,
                    category = s.Category,
                    isActive = s.IsActive,
                    createdAt = s.CreatedAt
                })
                .ToListAsync(cancellationToken);

            // 4. Search for these services in Elasticsearch
            var recentServiceIds = recentServices.Select(s => s.serviceId.ToString()).ToList();
            var esDocsResponse = await _elasticClient.SearchAsync<MarketplaceServiceDocument>(s => s
                .Index(generationRead.PhysicalName)
                .Size(100)
                .Query(q => q.Terms(t => t.Field(f => f.ServiceId).Terms(recentServiceIds)))
            , ct: cancellationToken);

            var indexedServiceIds = esDocsResponse.IsValid 
                ? esDocsResponse.Documents.Select(d => d.ServiceId).ToList()
                : new List<Guid>();

            // 5. Identify missing services
            var missingServices = recentServices
                .Where(s => !indexedServiceIds.Contains(s.serviceId))
                .ToList();

            // 6. Check index health
            var indexExistsResponse = await _elasticClient.Indices.ExistsAsync(generationRead.PhysicalName, ct: cancellationToken);
            var indexExists = indexExistsResponse.Exists;

            cancellationToken.ThrowIfCancellationRequested();
            return Ok(new
            {
                timestamp = DateTime.UtcNow,
                status = drift == 0 ? "✅ Healthy" : drift < 5 ? "⚠️ Minor Drift" : "🚨 Critical Drift",
                postgresCount = pgCount,
                elasticsearchCount = esCount,
                drift = drift,
                driftPercentage = pgCount > 0 ? Math.Round((double)drift / pgCount * 100, 2) : 0,
                indexExists = indexExists,
                indexHealth = esCountResponse.IsValid ? "✅ Responding" : "🚨 Unhealthy",
                recentServices = recentServices,
                missingFromElasticsearch = missingServices,
                recommendations = drift > 0 
                    ? new[] { 
                        "Run /api/marketplace/admin/reindex to sync missing services",
                        "Check MassTransit consumer logs for ServiceDiscoveryProjectionConsumer",
                        "Verify Elasticsearch is running and accessible"
                      }
                    : new[] { "Marketplace index is healthy" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Marketplace diagnostics failed");
            return StatusCode(500, new
            {
                status = "🚨 Error",
                message = "An unexpected error occurred"
            });
        }
    }

    /// <summary>
    /// Upload a private attachment for a chat request.
    /// Returns only a server-issued object key; clients never receive a public file URL.
    /// </summary>
    [HttpPost("requests/{requestId:guid}/attachments")]
    [RequestSizeLimit(MaxChatAttachmentBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxChatAttachmentBytes)]
    public async Task<IActionResult> UploadAttachment(
        Guid requestId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var tenantId = GetTenantId();

        try
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { error = "No file provided" });

            if (file.Length > MaxChatAttachmentBytes)
                return BadRequest(new { error = "File size must be less than 10MB" });

            var canParticipate = await _chatService.CanUserParticipateAsync(
                requestId,
                userId,
                tenantId,
                cancellationToken);

            if (!canParticipate)
            {
                return Forbid();
            }

            FileValidationResult validationResult;
            await using (var validationStream = file.OpenReadStream())
            {
                validationResult = await _fileValidationService.ValidateFileAsync(
                    validationStream,
                    file.FileName,
                    file.ContentType,
                    FileValidationProfiles.ChatAttachments,
                    cancellationToken);
            }

            if (!validationResult.IsValid ||
                string.IsNullOrWhiteSpace(validationResult.ValidatedExtension) ||
                string.IsNullOrWhiteSpace(validationResult.CanonicalContentType))
            {
                return BadRequest(new { error = "Unsupported or invalid attachment type" });
            }

            await using var uploadStream = file.OpenReadStream();
            var uploadResult = await _storageService.UploadFileAsync(
                uploadStream,
                validationResult.CanonicalContentType,
                MarketplaceChatAttachmentKey.BuildStoragePath(tenantId, requestId, userId),
                cancellationToken);

            if (!MarketplaceChatAttachmentKey.TryNormalizeTenantScopedObjectKey(
                uploadResult.ObjectKey,
                tenantId,
                requestId,
                userId,
                out var objectKey))
            {
                throw new InvalidOperationException("Chat attachment storage returned an invalid object key.");
            }

            _logger.LogInformation(
                "Marketplace attachment uploaded | TenantId: {TenantId} | RequestId: {RequestId} | UserId: {UserId} | ObjectKey: {ObjectKey} | Size: {Size}",
                tenantId,
                requestId,
                userId,
                objectKey,
                file.Length
            );

            return Ok(new
            {
                objectKey,
                fileName = MarketplaceChatAttachmentKey.GetDisplayFileName(objectKey)
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(
                ex,
                "Marketplace attachment upload unavailable | TenantId: {TenantId} | RequestId: {RequestId} | UserId: {UserId}",
                tenantId,
                requestId,
                userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Attachment storage is unavailable" });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Marketplace attachment upload failed | TenantId: {TenantId} | RequestId: {RequestId} | UserId: {UserId}",
                tenantId,
                requestId,
                userId);
            return StatusCode(500, new { error = "File upload failed" });
        }
    }

    /// <summary>
    /// Resolve a private marketplace attachment after validating tenant ownership and chat participation.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("attachments/{id:guid}")]
    public async Task<IActionResult> GetAttachment(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserContext(out var userId, out var tenantId))
        {
            _telemetry.RecordAttachmentUnauthorizedAccess();
            return Unauthorized(new { error = "Authentication required" });
        }

        try
        {
            var message = await _context.Set<ServiceRequestMessage>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted, cancellationToken);

            if (message == null || string.IsNullOrWhiteSpace(message.AttachmentUrl))
            {
                return NotFound();
            }

            if (message.TenantId != tenantId)
            {
                _telemetry.RecordAttachmentAccessDenied(tenantId, "cross_tenant");
                return Forbid();
            }

            var canView = await _chatService.CanUserViewConversationAsync(
                message.ServiceRequestId,
                userId,
                tenantId,
                cancellationToken);

            if (!canView)
            {
                _telemetry.RecordAttachmentAccessDenied(tenantId, "not_participant");
                return Forbid();
            }

            var objectKey = await ResolveAttachmentObjectKeyAsync(message, cancellationToken);
            if (string.IsNullOrWhiteSpace(objectKey))
            {
                _telemetry.RecordAttachmentAccessDenied(tenantId, "invalid_reference");
                return NotFound();
            }

            var downloadUrl = await _storageService.CreateDownloadUrlAsync(
                objectKey,
                TimeSpan.FromMinutes(5),
                cancellationToken);

            Response.Headers.CacheControl = "no-store, private, max-age=0";
            Response.Headers.Pragma = "no-cache";
            Response.Headers.Expires = "0";
            _telemetry.RecordAttachmentServed(tenantId, "signed_url");
            return Redirect(downloadUrl);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(
                ex,
                "Marketplace attachment access unavailable | TenantId: {TenantId} | AttachmentMessageId: {AttachmentMessageId} | UserId: {UserId}",
                tenantId,
                id,
                userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Attachment storage is unavailable" });
        }
    }

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.Parse(userIdClaim ?? throw new UnauthorizedAccessException());
    }
    
    private Guid GetTenantId()
    {
        var tenantIdClaim = User.FindFirst("tenant_id")?.Value;
        return Guid.Parse(tenantIdClaim ?? throw new UnauthorizedAccessException("Missing tenant claim"));
    }

    private bool TryGetTenantId(out Guid tenantId)
    {
        var tenantIdClaim = User.FindFirst("tenant_id")?.Value;
        return Guid.TryParse(tenantIdClaim, out tenantId) && tenantId != Guid.Empty;
    }

    private bool TryGetAuthenticatedUserContext(out Guid userId, out Guid tenantId)
    {
        userId = Guid.Empty;
        tenantId = Guid.Empty;

        if (User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var tenantIdClaim = User.FindFirst("tenant_id")?.Value;

        return Guid.TryParse(userIdClaim, out userId) &&
               userId != Guid.Empty &&
               Guid.TryParse(tenantIdClaim, out tenantId) &&
               tenantId != Guid.Empty;
    }

    private async Task<string?> ResolveAttachmentObjectKeyAsync(
        ServiceRequestMessage message,
        CancellationToken cancellationToken)
    {
        if (MarketplaceChatAttachmentKey.TryNormalizeTenantScopedObjectKey(
            message.AttachmentUrl,
            message.TenantId,
            message.ServiceRequestId,
            message.SenderUserId,
            out var objectKey))
        {
            return objectKey;
        }

        if (MarketplaceChatAttachmentKey.TryExtractLegacyLocalFileName(message.AttachmentUrl, out _))
        {
            _logger.LogWarning(
                "Rejected legacy local marketplace attachment reference because chat attachments must live in shared object storage | TenantId: {TenantId} | MessageId: {MessageId}",
                message.TenantId,
                message.Id);
        }

        return null;
    }

    private static Guid? ResolveAttachmentId(ServiceRequestMessage message)
    {
        return MarketplaceChatAttachmentKey.TryNormalizeTenantScopedObjectKey(
            message.AttachmentUrl,
            message.TenantId,
            message.ServiceRequestId,
            message.SenderUserId,
            out _)
            ? message.Id
            : null;
    }

    private static string? ResolveAttachmentFileName(ServiceRequestMessage message)
    {
        return ResolveAttachmentId(message).HasValue
            ? MarketplaceChatAttachmentKey.GetDisplayFileName(message.AttachmentUrl)
            : null;
    }

}

public record CancelRequestDto(string? Reason);
