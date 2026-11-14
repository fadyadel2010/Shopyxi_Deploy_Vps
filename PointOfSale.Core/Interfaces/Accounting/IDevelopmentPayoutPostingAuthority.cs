namespace PointOfSale.Core.Interfaces.Accounting;

/// <summary>Optional read-only authority for explicitly synthetic internal payout cash accounting.
/// Absence or rejection grants no additional treasury outflow permission.</summary>
public interface IDevelopmentPayoutPostingAuthority
{
    Task<bool> IsAuthorizedAsync(Guid transactionId, Guid tenantId, string currency, decimal amount, CancellationToken ct);
}
