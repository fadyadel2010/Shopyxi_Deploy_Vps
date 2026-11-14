using System.Globalization;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace PointOfSale.Tests.Compatibility;

public sealed class Dotnet10RedisConversionContractTests
{
    [Fact]
    public void GuardedTextConversion_RetainsStringOperatorForAllStorageRepresentations()
    {
        RedisValue[] values =
        [
            RedisValue.Null, RedisValue.EmptyString, "", Array.Empty<byte>(),
            "héllo 世界 😀", Encoding.UTF8.GetBytes("héllo 世界 😀"),
            new byte[] { 0, 0xff, 0xc3, 0x28, 0x80 },
            new ReadOnlyMemory<byte>(new byte[] { 0, 0xc3, 0xa9, 0xff }, 1, 2),
            0L, -42L, long.MinValue, long.MaxValue, ulong.MaxValue,
            1.25, double.NaN, double.PositiveInfinity, true
        ];

        foreach (var value in values)
        {
            Assert.Equal(!value.IsNullOrEmpty, value.HasValue);
            if (!value.HasValue)
            {
                Assert.Equal(string.Empty, value.ToString());
                continue;
            }
            Assert.Equal((string?)value, value.ToString());
        }
        // ToString coalesces null to empty; every modified application site guards first.
        Assert.Null((string?)RedisValue.Null);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("ar-EG")]
    [InlineData("tr-TR")]
    public void NumericParsing_RetainsDefaultAndInvariantCultureSemantics(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            RedisValue[] values =
            [
                RedisValue.Null, "", " 42 ", "+42", "-42", "1,234", "1.234",
                "2147483648", "9223372036854775808", "invalid", 0L, -42L, long.MaxValue, ulong.MaxValue
            ];
            foreach (var value in values)
            {
                if (!value.HasValue) continue;
                string? original = value;
                var explicitText = value.ToString();
                Assert.Equal(int.TryParse(original, out var expectedInt), int.TryParse(explicitText, out var actualInt));
                Assert.Equal(expectedInt, actualInt);
                Assert.Equal(long.TryParse(original, out var expectedLong), long.TryParse(explicitText, out var actualLong));
                Assert.Equal(expectedLong, actualLong);
                Assert.Equal(
                    long.TryParse(original, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedTicks),
                    long.TryParse(explicitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualTicks));
                Assert.Equal(expectedTicks, actualTicks);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void JsonDeserialization_RetainsOptionsUnicodeAndCorruptPayloadBehavior()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        RedisValue[] valid =
        [
            "{\"count\":7,\"name\":\"héllo 世界\"}",
            Encoding.UTF8.GetBytes("{\"count\":7,\"name\":\"héllo 世界\"}"),
            "null"
        ];
        foreach (var value in valid)
        {
            Assert.Equal(
                JsonSerializer.Deserialize<Payload>((string)value!, options),
                JsonSerializer.Deserialize<Payload>(value.ToString(), options));
        }
        RedisValue[] corrupt = ["{", "not-json", new byte[] { 0xff, 0xc3, 0x28 }];
        foreach (var value in corrupt)
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Payload>((string)value!, options));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Payload>(value.ToString(), options));
        }
    }

    private sealed record Payload(int Count, string Name);
}
