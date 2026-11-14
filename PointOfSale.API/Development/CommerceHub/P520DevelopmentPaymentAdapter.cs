using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.API.Services.Payments.ProviderContracts;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Development.CommerceHub;

/// <summary>Local session handoff only. Capture still requires the authorized Development success command.</summary>
public sealed class P520DevelopmentPaymentAdapter(
    PaymobAdapter fallback,
    ApplicationDbContext db,
    IWebHostEnvironment environment,
    IConfiguration configuration) : IPaymentProviderAdapter
{
    public ProviderCode ProviderCode => fallback.ProviderCode;
    private bool Enabled => environment.IsDevelopment() &&
        configuration.GetValue<bool>("CommerceHub:DevelopmentAcceptance:PaymentSessions");

    public PaymentProviderCapabilities Capabilities => Enabled
        ? new(true, true, true, true, false, true, true, false,
            new Dictionary<string, string> { ["environment"] = "Development", ["provenance"] = "SyntheticNonDeliveringSession" })
        : fallback.Capabilities;

    public async Task<PaymentProviderSessionResult> CreateSessionAsync(
        PaymentProviderSessionRequest request, CancellationToken cancellationToken = default)
    {
        var order = await db.CommerceHubMarketplaceOrders.IgnoreQueryFilters().AsNoTracking()
            .Include(current => current.SubOrders)
            .SingleOrDefaultAsync(current => current.Id == request.MarketplaceOrderId && !current.IsDeleted, cancellationToken);
        var email = order == null ? null : await db.CommerceHubCustomers.IgnoreQueryFilters().AsNoTracking()
            .Where(customer => customer.Id == order.CustomerId && !customer.IsDeleted)
            .Select(customer => customer.Email).SingleOrDefaultAsync(cancellationToken);
        var ownedRecipient = email != null && P520DevelopmentMailbox.CanCapture(
            environment.EnvironmentName, Enabled, email);
        if (!ownedRecipient)
        {
            if ((email ?? request.Email)?.StartsWith("p520d-", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException("Development payment sessions are disabled for this test identity.");
            return await fallback.CreateSessionAsync(request, cancellationToken);
        }

        if (order == null) throw new InvalidOperationException("Development order is missing.");
        var customerOwned = await db.CommerceHubCustomers.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(customer => customer.Id == order.CustomerId && customer.Email == email && !customer.IsDeleted, cancellationToken);
        var vendorIds = order.SubOrders.Select(current => current.StoreId).Distinct().ToArray();
        var vendorCount = await db.Tenants.IgnoreQueryFilters().CountAsync(tenant => vendorIds.Contains(tenant.Id) &&
            tenant.Name.StartsWith("P520D-20261028-E2E01-") && tenant.IsActive, cancellationToken);
        if (!customerOwned || vendorCount != vendorIds.Length || vendorIds.Length == 0 ||
            order.TotalAmount != request.Amount || order.Currency != request.Currency ||
            request.Currency != "EGP" || request.Amount <= 0 || request.PaymentId == Guid.Empty)
            throw new InvalidOperationException("Development session ownership, amount or currency does not match canonical order authority.");

        return new PaymentProviderSessionResult(
            $"http://localhost:5175/orders/{order.Id:D}/recover",
            $"DEV-SIM:SESSION-TOKEN:{request.PaymentId:N}",
            $"DEV-SIM:SESSION:{request.PaymentId:N}",
            JsonSerializer.Serialize(new { EvidenceType = "DevelopmentNonDeliveringPaymentSession", ExternalMoneyMovementConfirmed = false, request.PaymentId, request.MarketplaceOrderId, request.Amount, request.Currency }));
    }

    // No synthetic callback is ever relabeled as authenticated Paymob evidence.
    public Task<PaymentProviderWebhookResult> ParseWebhookAsync(PaymentProviderWebhookRequest request, CancellationToken cancellationToken = default) => fallback.ParseWebhookAsync(request, cancellationToken);
    public Task<PaymentProviderCallbackResult> ParseCallbackAsync(PaymentProviderCallbackRequest request, CancellationToken cancellationToken = default) => fallback.ParseCallbackAsync(request, cancellationToken);
    public Task<PaymentProviderStatusResult> GetStatusAsync(PaymentProviderStatusRequest request, CancellationToken cancellationToken = default) => fallback.GetStatusAsync(request, cancellationToken);
}
