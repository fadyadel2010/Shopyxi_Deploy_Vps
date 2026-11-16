using PointOfSale.Core.Enums;

namespace PointOfSale.Core.Entities;

public sealed class CommerceHubSellerApplication
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string ReferenceNumber { get; private set; } = string.Empty;
    public int Revision { get; private set; }
    public CommerceHubSellerApplicationStatus Status { get; private set; }
    public string StoreDisplayName { get; private set; } = string.Empty;
    public string BusinessCategory { get; private set; } = string.Empty;
    public string BusinessDescription { get; private set; } = string.Empty;
    public string ContactName { get; private set; } = string.Empty;
    public string ContactPhone { get; private set; } = string.Empty;
    public string ContactEmail { get; private set; } = string.Empty;
    public string ProductCategoriesJson { get; private set; } = "[]";
    public string? StorefrontUrl { get; private set; }
    public Guid SubmittedByUserId { get; private set; }
    public DateTime SubmittedAtUtc { get; private set; }
    public Guid? ReviewedBySuperAdminId { get; private set; }
    public string? ReviewerIdentity { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }
    public string? RejectionReason { get; private set; }
    public Guid ConcurrencyToken { get; private set; }

    private CommerceHubSellerApplication()
    {
    }

    public static CommerceHubSellerApplication Submit(
        Guid tenantId,
        string referenceNumber,
        int revision,
        string storeDisplayName,
        string businessCategory,
        string businessDescription,
        string contactName,
        string contactPhone,
        string contactEmail,
        string productCategoriesJson,
        string? storefrontUrl,
        Guid submittedByUserId,
        DateTime submittedAtUtc)
    {
        return new CommerceHubSellerApplication
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ReferenceNumber = referenceNumber,
            Revision = revision,
            Status = CommerceHubSellerApplicationStatus.Submitted,
            StoreDisplayName = storeDisplayName,
            BusinessCategory = businessCategory,
            BusinessDescription = businessDescription,
            ContactName = contactName,
            ContactPhone = contactPhone,
            ContactEmail = contactEmail,
            ProductCategoriesJson = productCategoriesJson,
            StorefrontUrl = storefrontUrl,
            SubmittedByUserId = submittedByUserId,
            SubmittedAtUtc = submittedAtUtc,
            ConcurrencyToken = Guid.NewGuid()
        };
    }

    public void Approve(Guid reviewerId, string reviewerIdentity, DateTime reviewedAtUtc)
    {
        EnsurePending();
        Status = CommerceHubSellerApplicationStatus.Approved;
        ReviewedBySuperAdminId = reviewerId;
        ReviewerIdentity = reviewerIdentity;
        ReviewedAtUtc = reviewedAtUtc;
        RejectionReason = null;
        ConcurrencyToken = Guid.NewGuid();
    }

    public void Reject(Guid reviewerId, string reviewerIdentity, string reason, DateTime reviewedAtUtc)
    {
        EnsurePending();
        Status = CommerceHubSellerApplicationStatus.Rejected;
        ReviewedBySuperAdminId = reviewerId;
        ReviewerIdentity = reviewerIdentity;
        ReviewedAtUtc = reviewedAtUtc;
        RejectionReason = reason;
        ConcurrencyToken = Guid.NewGuid();
    }

    private void EnsurePending()
    {
        if (Status != CommerceHubSellerApplicationStatus.Submitted)
        {
            throw new InvalidOperationException("Only a pending seller application can be reviewed.");
        }
    }
}
