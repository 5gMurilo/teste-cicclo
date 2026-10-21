using Cicclo.Api.Platform.Http;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Platform;

[Trait("Increment", "E01")]
[Trait("Task", "T6")]
public sealed class MoneyCentsTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("0.00", 0L)]
    [InlineData("18.90", 1890L)]
    [InlineData("20.9", 2090L)]
    [InlineData("50.00", 5000L)]
    [InlineData("31.10", 3110L)]
    [InlineData("9999999999.99", 999_999_999_999L)]
    public void W09_ValidAmountsConvertToExactCents(string amount, long expected) =>
        Assert.Equal(expected, MoneyCents.FromDecimal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0.001")]
    [InlineData("18.905")]
    [InlineData("18.999")]
    [InlineData("-0.01")]
    [InlineData("10000000000.00")]
    public void W10_InvalidPrecisionOrRangeIsRejectedWithoutRounding(string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        Assert.ThrowsAny<ArgumentException>(() => MoneyCents.FromDecimal(value));
    }
}
