namespace PointOfSale.Core.Events.Affiliate;

/// <summary>Committed cash-return fact. Amounts are reloaded from SQL, never trusted from an event.</summary>
public sealed class AffiliateManualPayoutReturnedEvent : IntegrationEvent, IAggregateScopedIntegrationEvent, IAffectedTenantScopedIntegrationEvent
{
    public Guid StoreTenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid AffiliateAccountId { get; set; }
    public Guid ReturnId { get; set; }
    public Guid? AffiliateAuthorityTenantId { get; set; }
    public Guid GetAuthoritativeAggregateId() => OrderId;
    public IReadOnlyCollection<Guid> GetAffectedTenantIds() =>
        new[] { StoreTenantId, AffiliateAuthorityTenantId ?? Guid.Empty }.Where(id => id != Guid.Empty).Distinct().ToArray();
}
