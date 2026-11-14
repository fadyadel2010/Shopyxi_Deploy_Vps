using PointOfSale.Core.Constants;

namespace PointOfSale.Core.Entities.Affiliate;

/// <summary>Presentation preference only. Never changes product policy or financial eligibility.</summary>
public sealed class AffiliateLandingPageDesign
{
    public Guid AffiliateAccountId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public string TemplateType { get; private set; } = LandingTemplates.ClassicV1;
    public DateTime UpdatedAtUtc { get; private set; }

    public void SelectTemplate(string templateType)
    {
        if (!LandingTemplates.Allowed.Contains(templateType))
            throw new ArgumentException("Unsupported landing page design.", nameof(templateType));
        TemplateType = templateType;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
