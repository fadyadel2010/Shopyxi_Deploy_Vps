using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.DTOs.Checkout;
using PointOfSale.API.Services.Checkout;
using PointOfSale.API.Services.Pricing;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateCouponCommissionTests
{
    [Fact]
    public void ActualCheckoutSnapshot_DiscountTaxAndRounding_ReconcileWithoutShippingOrFee()
    {
        var type = typeof(CheckoutService);
        var admissionType = type.GetNestedType("AffiliateOrderLineAdmissionSnapshot", BindingFlags.NonPublic)!;
        var admissions = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid), admissionType))!;
        var order = new Order { ShippingCost = 40, PaymentFee = 5 };
        var cart = new List<CartItemSnapshot>();
        for (var i = 0; i < 3; i++)
        {
            var product = Guid.NewGuid(); cart.Add(new() { ProductId = product, CurrentPrice = 1, Quantity = 1 });
            order.Items.Add(new OrderItem { Id = Guid.NewGuid(), ProductId = product, Quantity = 1 });
            admissions.Add(product, Activator.CreateInstance(admissionType, product, Guid.NewGuid(), .1m, null)!);
        }
        var taxes = cart.Select((item, index) => new ProductTaxSnapshot(item.ProductId, item.VariantId,
            1m, index == 2 ? 6m : 7m, false, index == 2 ? .06m : .07m)).ToArray();
        type.GetMethod("RefreshOrderItemAuthoritySnapshots", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(RuntimeHelpers.GetUninitializedObject(type), [order, cart, 1m, taxes, admissions, true]);
        Assert.Equal(new[] { .34m, .33m, .33m }, order.Items.Select(i => i.DiscountAmount));
        Assert.Equal(.2m, order.Items.Sum(i => i.TaxAmount)); Assert.Equal(2.2m, order.Items.Sum(i => i.Total));
        Assert.Equal(.22m, order.Items.Sum(i => i.AffiliateCommissionAmountSnapshot));
        Assert.All(order.Items, i => Assert.Equal(i.Total, i.AffiliateCommissionBaseAmountSnapshot));
        Assert.Equal(47.2m, order.Items.Sum(i => i.Total) + order.ShippingCost + order.PaymentFee);
    }

    [Theory]
    [InlineData(null, 9)]
    [InlineData("4", 4)]
    public async Task DiscountedLine_CanonicalEarnAndPartialFullRefund_AreBoundedAndIdempotent(string? capValue, decimal commission)
    {
        decimal? cap = capValue == null ? null : decimal.Parse(capValue, System.Globalization.CultureInfo.InvariantCulture);
        // Controlled financial-state fixture, not a collection or real payout.
        await using var f = new AffiliatePhase4CommissionLifecycleTests.Fixture(price: 100, discount: 20, tax: 10, cap: cap);
        await f.Service.RecordCommissionForOrderAsync(f.Order.Id);
        await f.Service.RecordCommissionForOrderAsync(f.Order.Id);
        var earned = Assert.Single(await f.Ledger()); Assert.Equal(90, earned.BaseAmount); Assert.Equal(commission, earned.CommissionAmount);
        f.Item.RefundedAmount = 45; await f.Db.SaveChangesAsync();
        await f.Service.RecordCommissionForOrderAsync(f.Order.Id); await f.Service.RecordCommissionForOrderAsync(f.Order.Id);
        var half = Assert.Single((await f.Ledger()).Where(e => e.EntryType == CommissionEntryType.Reversed));
        Assert.Equal(earned.Id, half.OriginalEntryId); Assert.Equal(commission / 2, half.CommissionAmount);
        f.Item.RefundedAmount = 90; await f.Db.SaveChangesAsync();
        await f.Service.RecordCommissionForOrderAsync(f.Order.Id); await f.Service.RecordCommissionForOrderAsync(f.Order.Id);
        var reversed = (await f.Ledger()).Where(e => e.EntryType == CommissionEntryType.Reversed).ToList();
        Assert.Equal(commission, reversed.Sum(e => e.CommissionAmount)); Assert.Equal(90, reversed.Sum(e => e.BaseAmount));
        Assert.All(reversed, r => { Assert.Equal(earned.Id, r.OriginalEntryId); Assert.Equal(f.Order.Id, r.OrderId); Assert.Equal(f.Item.Id, r.OrderItemId); });
    }
}
