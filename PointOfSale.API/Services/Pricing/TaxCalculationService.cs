using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Helpers;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Tenancy;

namespace PointOfSale.API.Services.Pricing;

public interface ITaxCalculationService
{
    Task<TaxComputationResult> CalculateLinesAsync(
        Guid tenantId, IReadOnlyList<ProductTaxLine> lines, string? countryCode,
        CancellationToken cancellationToken = default);

    Task<TaxComputationResult> CalculateAsync(
        Guid tenantId,
        decimal subtotal,
        string? countryCode,
        CancellationToken cancellationToken = default);
}

public sealed record TaxComputationResult(
    decimal TaxRate,
    decimal TaxAmount,
    string Source,
    string? CountryCode)
{
    public IReadOnlyList<ProductTaxSnapshot> Lines { get; init; } = [];
}

public sealed record ProductTaxLine(Guid ProductId, Guid? VariantId, decimal Subtotal);
public sealed record ProductTaxSnapshot(Guid ProductId, Guid? VariantId, decimal TaxableBase,
    decimal TaxRate, bool PriceIncludesTax, decimal TaxAmount);

public sealed class TaxCalculationService : ITaxCalculationService
{
    private const int CurrencyScale = 2;
    private readonly ApplicationDbContext _dbContext;
    private readonly StorefrontPricingOptions _options;
    private readonly ILogger<TaxCalculationService> _logger;

    public TaxCalculationService(
        ApplicationDbContext dbContext,
        IOptions<StorefrontPricingOptions> options,
        ILogger<TaxCalculationService> logger)
    {
        _dbContext = dbContext;
        _options = options.Value;
        _logger = logger;
    }

    // Legacy non-catalog subtotal API only. Product carts/checkouts must use
    // CalculateLinesAsync: a required Product.TaxRate (even zero) is explicit.
    public async Task<TaxComputationResult> CalculateAsync(
        Guid tenantId,
        decimal subtotal,
        string? countryCode,
        CancellationToken cancellationToken = default)
    {
        if (subtotal <= 0m)
        {
            return new TaxComputationResult(0m, 0m, "empty-subtotal", NormalizeCountryCode(countryCode));
        }

        var normalizedCountry = NormalizeCountryCode(countryCode);
        var settings = await _dbContext.SystemSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(setting =>
                setting.TenantId == tenantId &&
                (setting.Key == "Tax.Enable" || setting.Key == "Tax.Rate"))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        if (settings.TryGetValue("Tax.Enable", out var enableValue) &&
            bool.TryParse(enableValue, out var enableTax) &&
            !enableTax)
        {
            return CreateResult(0m, "tenant-disabled", normalizedCountry, subtotal);
        }

        if (settings.TryGetValue("Tax.Rate", out var configuredRate))
        {
            if (decimal.TryParse(configuredRate, NumberStyles.Number, CultureInfo.InvariantCulture, out var tenantRate))
            {
                return CreateResult(Math.Max(0m, tenantRate), "tenant-setting", normalizedCountry, subtotal);
            }

            _logger.LogWarning(
                "Ignoring invalid tenant tax rate setting | TenantId: {TenantId} | Value: {Value}",
                tenantId,
                configuredRate);
        }

        var effectiveCountry = normalizedCountry ?? await ResolveTenantCountryAsync(tenantId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(effectiveCountry) &&
            _options.CountryTaxRates.TryGetValue(effectiveCountry, out var countryRate))
        {
            return CreateResult(Math.Max(0m, countryRate), "country", effectiveCountry, subtotal);
        }

        return CreateResult(Math.Max(0m, _options.DefaultTaxRate), "default", effectiveCountry, subtotal);
    }

    public static async Task<bool> IsTaxEnabledAsync(ApplicationDbContext context, Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var value = await context.SystemSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Key == "Tax.Enable")
            .Select(s => s.Value).SingleOrDefaultAsync(cancellationToken);
        return !bool.TryParse(value, out var enabled) || enabled;
    }

    public async Task<TaxComputationResult> CalculateLinesAsync(Guid tenantId,
        IReadOnlyList<ProductTaxLine> lines, string? countryCode,
        CancellationToken cancellationToken = default)
    {
        var ids = lines.Select(l => l.ProductId).Distinct().ToArray();
        // Tax metadata is SQL catalog authority, never Redis/Mongo/client input.
        var products = await _dbContext.Products.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && ids.Contains(p.Id) && !p.IsDeleted)
            .Select(p => new { p.Id, p.TaxRate, p.PriceIncludesTax })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var enabled = await IsTaxEnabledAsync(_dbContext, tenantId, cancellationToken);
        var snapshots = lines.Select(line =>
        {
            if (!products.TryGetValue(line.ProductId, out var product))
                throw new InvalidOperationException("Product tax authority is unavailable.");
            return new ProductTaxSnapshot(line.ProductId, line.VariantId,
                ProductTaxPolicy.Round(line.Subtotal),
                ProductTaxPolicy.EffectiveRate(product.TaxRate, enabled), product.PriceIncludesTax,
                ProductTaxPolicy.CalculateAddedTax(line.Subtotal, 0m, product.TaxRate, product.PriceIncludesTax, enabled));
        }).ToArray();
        var rates = snapshots.Select(l => l.TaxRate).Distinct().ToArray();
        // Compatibility summary is fractional; mixed rates are represented by Lines.
        return new TaxComputationResult(rates.Length == 1 ? rates[0] / 100m : 0m,
            snapshots.Sum(l => l.TaxAmount), enabled ? "product" : "tenant-disabled", NormalizeCountryCode(countryCode))
        { Lines = snapshots };
    }

    private async Task<string?> ResolveTenantCountryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await TenantAuthorityMemo.GetTenantByIdAsync(
            _dbContext,
            tenantId,
            cancellationToken);

        return NormalizeCountryCode(tenant?.Country);
    }

    private static TaxComputationResult CreateResult(decimal rate, string source, string? countryCode, decimal subtotal)
    {
        // Normalize percentage-style rates (e.g., 15) to fractional rates (e.g., 0.15)
        // This ensures consistency between config-backed and DB-backed tax rates
        var normalizedRate = rate >= 1m ? rate / 100m : rate;

        var taxAmount = normalizedRate <= 0m
            ? 0m
            : RoundCurrency(subtotal * normalizedRate);

        return new TaxComputationResult(normalizedRate, taxAmount, source, countryCode);
    }

    private static string? NormalizeCountryCode(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        return CountryCodeHelper.TryNormalizeCountryCode(countryCode, out var normalizedCountry)
            ? normalizedCountry
            : countryCode.Trim().ToUpperInvariant();
    }

    private static decimal RoundCurrency(decimal value)
    {
        return Math.Round(value, CurrencyScale, MidpointRounding.AwayFromZero);
    }
}
