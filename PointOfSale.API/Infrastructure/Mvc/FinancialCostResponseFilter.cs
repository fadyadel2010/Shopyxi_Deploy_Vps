using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using PointOfSale.API.Security;
using PointOfSale.Core.Security;

namespace PointOfSale.API.Infrastructure.Mvc;

/// <summary>
/// Server-side cost contract for POS operational reads. Kept at the serialized
/// DTO boundary so nested product units/variants and anonymous projections obey
/// the same rule. Does not replace endpoint permission or entity-exposure guards.
/// Selling prices and stock quantities are intentionally retained.
/// </summary>
public sealed class FinancialCostResponseFilter(IOptions<JsonOptions> options) : IAsyncResultFilter, IOrderedFilter
{
    // Runs after the existing direct-entity guard has inspected the actual DTO.
    public int Order => 1000;

    private static readonly HashSet<string> CostFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "costPrice", "purchasePrice", "unitCost", "totalCost", "costAmount",
        "dispatchedUnitCost", "averageCost", "weightedAverageCost",
        "costPriceSnapshot", "costAmountSnapshot", "totalCostOfGoodsSold",
        "totalCostReversed", "costReversed", "costOfReturnedItems", "avgCostPerUnit",
        "averageCostPerUnit", "lastPurchaseCost", "minPurchaseCost", "maxPurchaseCost", "costPerBaseUnit",
        "inTransitValue", "postedValue"
    };

    private static readonly HashSet<string> PurchaseMoneyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "unitPrice", "subtotal", "totalAmount", "paidAmount", "remainingAmount",
        "taxAmount", "discountAmount", "unitCost", "amount", "balance",
        "cashRefundAmount", "cardRefundAmount", "customerCreditAmount"
    };

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        // Applies only to the revalidated POS principal, not unrelated public/customer actors.
        var user = context.HttpContext.User;
        if (PosPermissionAuthorizationHandler.HasPermission(user, PosPermissions.DashboardView) &&
            !PosPermissionAuthorizationHandler.HasPermission(user, PosPermissions.FinanceView))
        {
            context.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
            var purchase = controller is "Purchases" or "PurchaseReturns";
            if (context.Result is ObjectResult { Value: not null } result && (result.StatusCode ?? 200) < 400)
            {
                var node = JsonSerializer.SerializeToNode(result.Value, result.Value.GetType(), options.Value.JsonSerializerOptions);
                Redact(node, purchase);
                result.Value = node;
                result.DeclaredType = null;
            }
            else if (context.Result is JsonResult { Value: not null } json && (json.StatusCode ?? 200) < 400)
            {
                var node = JsonSerializer.SerializeToNode(json.Value, json.Value.GetType(), options.Value.JsonSerializerOptions);
                Redact(node, purchase);
                json.Value = node;
            }
        }
        await next();
    }

    internal static void Redact(JsonNode? node, bool purchase = false)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (CostFields.Contains(key) || purchase && PurchaseMoneyFields.Contains(key)) obj.Remove(key);
                else Redact(obj[key], purchase);
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array) Redact(item, purchase);
    }
}
