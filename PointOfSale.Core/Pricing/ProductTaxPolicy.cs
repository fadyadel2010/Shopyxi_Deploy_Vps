namespace PointOfSale.Core.Pricing;

/// <summary>
/// Catalog rates are percentages, including an explicit zero. Tax.Enable is the
/// tenant kill switch; a tenant/default rate never overrides a catalog rate.
/// TaxAmount means tax ADDED to the price: inclusive prices are not taxed twice.
/// Line discounts reduce the base; order-level discounts are applied afterwards.
/// </summary>
public static class ProductTaxPolicy
{
    public static decimal EffectiveRate(decimal productRate, bool enabled) =>
        enabled ? productRate : 0m;

    public static decimal CalculateAddedTax(decimal subtotal, decimal lineDiscount,
        decimal productRate, bool priceIncludesTax, bool enabled = true)
    {
        if (productRate is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(productRate));
        var taxableBase = Round(Math.Max(0m, Round(subtotal) - Round(lineDiscount)));
        return priceIncludesTax ? 0m : Round(taxableBase * EffectiveRate(productRate, enabled) / 100m);
    }

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
