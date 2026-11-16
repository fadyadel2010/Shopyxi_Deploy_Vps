using System.ComponentModel.DataAnnotations;

namespace PointOfSale.API.DTOs.CommerceHub;

public sealed class CommerceHubSellerApplicationSubmitRequest
{
    [Required, StringLength(160, MinimumLength = 2)]
    public string StoreDisplayName { get; init; } = string.Empty;

    [Required, StringLength(120, MinimumLength = 2)]
    public string BusinessCategory { get; init; } = string.Empty;

    [Required, StringLength(1000, MinimumLength = 20)]
    public string BusinessDescription { get; init; } = string.Empty;

    [Required, StringLength(160, MinimumLength = 2)]
    public string ContactName { get; init; } = string.Empty;

    [Required, StringLength(40, MinimumLength = 5)]
    public string ContactPhone { get; init; } = string.Empty;

    [Required, EmailAddress, StringLength(320)]
    public string ContactEmail { get; init; } = string.Empty;

    [Required, MinLength(1), MaxLength(10)]
    public IReadOnlyList<string> ProductCategories { get; init; } = Array.Empty<string>();

    [StringLength(500)]
    public string? StorefrontUrl { get; init; }
}

public sealed class CommerceHubSellerApplicationDto
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string ReferenceNumber { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string Status { get; init; } = string.Empty;
    public string StoreDisplayName { get; init; } = string.Empty;
    public string BusinessCategory { get; init; } = string.Empty;
    public string BusinessDescription { get; init; } = string.Empty;
    public string ContactName { get; init; } = string.Empty;
    public string ContactPhone { get; init; } = string.Empty;
    public string ContactEmail { get; init; } = string.Empty;
    public IReadOnlyList<string> ProductCategories { get; init; } = Array.Empty<string>();
    public string? StorefrontUrl { get; init; }
    public DateTime SubmittedAtUtc { get; init; }
    public Guid? ReviewedBySuperAdminId { get; init; }
    public string? ReviewerIdentity { get; init; }
    public DateTime? ReviewedAtUtc { get; init; }
    public string? RejectionReason { get; init; }
}

public sealed class CommerceHubSellerApplicationStatusDto
{
    public Guid TenantId { get; init; }
    public string Status { get; init; } = "NotApplied";
    public bool PublishingEnabled { get; init; }
    public bool CanSubmit { get; init; }
    public bool CanResubmit { get; init; }
    public bool ExistingVerificationRequired { get; init; }
    public CommerceHubSellerApplicationPrefillDto Prefill { get; init; } = new();
    public CommerceHubSellerApplicationDto? CurrentApplication { get; init; }
    public IReadOnlyList<CommerceHubSellerApplicationDto> History { get; init; } = Array.Empty<CommerceHubSellerApplicationDto>();
}

public sealed class CommerceHubSellerApplicationPrefillDto
{
    public string StoreDisplayName { get; init; } = string.Empty;
    public string ContactName { get; init; } = string.Empty;
    public string ContactPhone { get; init; } = string.Empty;
    public string ContactEmail { get; init; } = string.Empty;
    public string? StorefrontUrl { get; init; }
}

public sealed class CommerceHubSellerApplicationListQuery
{
    public string Status { get; init; } = "Pending";
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed class CommerceHubSellerApplicationListItemDto
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string TenantReference { get; init; } = string.Empty;
    public string MerchantName { get; init; } = string.Empty;
    public string BusinessCategory { get; init; } = string.Empty;
    public string ReferenceNumber { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime SubmittedAtUtc { get; init; }
    public DateTime? ReviewedAtUtc { get; init; }
}

public sealed class CommerceHubSellerApplicationSearchResultDto
{
    public IReadOnlyList<CommerceHubSellerApplicationListItemDto> Applications { get; init; } = Array.Empty<CommerceHubSellerApplicationListItemDto>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
}

public sealed class CommerceHubSellerApplicationDetailsDto
{
    public CommerceHubSellerApplicationDto Application { get; init; } = new();
    public string TenantName { get; init; } = string.Empty;
    public string? TenantSlug { get; init; }
    public string TenantStatus { get; init; } = string.Empty;
    public bool IsKycVerified { get; init; }
    public bool IsMarketplaceApproved { get; init; }
    public IReadOnlyList<CommerceHubSellerApplicationDto> History { get; init; } = Array.Empty<CommerceHubSellerApplicationDto>();
}

public sealed class CommerceHubSellerApplicationRejectRequest
{
    [Required, StringLength(1000, MinimumLength = 10)]
    public string Reason { get; init; } = string.Empty;
}
