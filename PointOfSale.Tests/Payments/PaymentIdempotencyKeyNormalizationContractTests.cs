using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.API.Services.Payments.Stripe;

namespace PointOfSale.Tests.Payments;

// Pure key formatting only. Never construct a gateway service or send a payment.
public sealed class PaymentIdempotencyKeyNormalizationContractTests
{
    [Theory]
    [InlineData("paymob", "null")]
    [InlineData("paymob", "blank")]
    [InlineData("paymob", "trim")]
    [InlineData("paymob", "255")]
    [InlineData("paymob", "256")]
    [InlineData("paymob", "long")]
    [InlineData("paymob", "unicode")]
    [InlineData("paymob", "fallback-long")]
    [InlineData("stripe", "null")]
    [InlineData("stripe", "blank")]
    [InlineData("stripe", "trim")]
    [InlineData("stripe", "255")]
    [InlineData("stripe", "256")]
    [InlineData("stripe", "long")]
    [InlineData("stripe", "unicode")]
    [InlineData("stripe", "fallback-long")]
    public void ExactKeyFormattingPreservesFallbackTrimmingLengthAndDigest(string provider, string state)
    {
        var type = provider == "paymob" ? typeof(PaymobPaymentService) : typeof(StripePaymentService);
        var method = type.GetMethod("NormalizeCheckoutPaymentIdempotencyKey", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var fallback = state == "fallback-long" ? new string('f', 300) : "fictional-fallback";
        string? requested = state switch
        {
            "null" or "fallback-long" => null, "blank" => " \t ", "trim" => "  fictional-key  ",
            "255" => new string('a', 255), "256" => new string('b', 256),
            "unicode" => new string('\u0628', 300), _ => new string('k', 1024)
        };
        var key = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        var expected = key.Length <= 255 ? key : key[..187] + "..." +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var actual = Assert.IsType<string>(method.Invoke(null, [requested, fallback]));
        Assert.Equal(expected, actual);
        Assert.True(actual.Length <= 255);
        Assert.Equal(actual, Assert.IsType<string>(method.Invoke(null, [requested, fallback])));
        if (key.Length > 255)
        {
            Assert.Equal(254, actual.Length);
            Assert.NotEqual(actual, Assert.IsType<string>(method.Invoke(null, [key + "different", fallback])));
        }
    }
}
