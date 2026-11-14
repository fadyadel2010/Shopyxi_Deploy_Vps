namespace PointOfSale.Core.Financial;

/// <summary>Changing a display setting must never relabel retained accounting amounts.</summary>
public sealed class AccountingCurrencyChangeException : InvalidOperationException
{
    public const string ErrorCode = "ACCOUNTING_CURRENCY_IMMUTABLE";

    public AccountingCurrencyChangeException()
        : base("The accounting currency of an existing store cannot be changed by ordinary settings or record updates. Retained payments and journals must not be relabeled; a separately reviewed accounting conversion workflow is required.")
    {
    }
}
