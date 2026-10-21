using Cicclo.Api.Bookings;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Bookings;

[Trait("Increment", "E02")]
[Trait("Task", "T2")]
public sealed class BookingPricingTests
{
    // Catálogo do seed público (spec: wash 1890, dry 2090), escrito aqui de forma independente.
    private static readonly CatalogEntry Wash = new("wash", 18.90m, true, true);
    private static readonly CatalogEntry Dry = new("dry", 20.90m, true, true);

    [Theory]
    [InlineData(new[] { "wash" }, 1890L, "18.90")]
    [InlineData(new[] { "dry" }, 2090L, "20.90")]
    [InlineData(new[] { "wash", "dry" }, 3980L, "39.80")]
    [InlineData(new[] { "wash", "wash" }, 3780L, "37.80")]
    public void I07_TotalsAreCalculatedFromTheCatalog(string[] codes, long expected, string total)
    {
        var result = BookingPricing.Price(Intent(codes, expected), [Wash, Dry]);

        Assert.Null(result.Rejection);
        Assert.Null(result.CalculatedTotalCents);
        Assert.Equal(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture), result.Price!.Total);
        Assert.Equal(
            codes.Order(StringComparer.Ordinal).Select(code => $"{code}|{(code == "wash" ? 18.90m : 20.90m)}"),
            result.Price.Items.Select(item => $"{item.ServiceCode}|{item.UnitPrice}"));
    }

    [Theory]
    [InlineData(1889L)]
    [InlineData(1891L)]
    [InlineData(long.MaxValue)]
    public void I08_ExpectedTotalDifferentFromTheServerTotalIsPriceChanged(long expected)
    {
        var result = BookingPricing.Price(Intent(["wash"], expected), [Wash, Dry]);

        Assert.Null(result.Price);
        Assert.Equal(BookingRejection.PriceChanged, result.Rejection);
        Assert.Equal(1890L, result.CalculatedTotalCents);
    }

    [Fact]
    public void I07_CurrentCatalogPriceWinsOverAnOutdatedClientTotal()
    {
        var changed = Wash with { CurrentPrice = 25.00m };

        var stale = BookingPricing.Price(Intent(["wash"], 1890), [changed, Dry]);
        var confirmed = BookingPricing.Price(Intent(["wash"], 2500), [changed, Dry]);

        Assert.Equal(BookingRejection.PriceChanged, stale.Rejection);
        Assert.Equal(2500L, stale.CalculatedTotalCents);
        Assert.Equal(25.00m, confirmed.Price!.Total);
    }

    [Theory]
    [InlineData("iron", true, true, BookingRejection.ServiceNotFound)]
    [InlineData("WASH", true, true, BookingRejection.ServiceNotFound)]
    [InlineData("dry", false, true, BookingRejection.ServiceDisabled)]
    [InlineData("dry", false, false, BookingRejection.ServiceDisabled)]
    [InlineData("dry", true, false, BookingRejection.NoOperationalMachine)]
    public void I08_AnyInvalidItemRejectsTheWholeBatch(string code, bool dryActive, bool dryOperational, BookingRejection expected)
    {
        var dry = Dry with { IsActive = dryActive, HasOperationalMachine = dryOperational };

        var result = BookingPricing.Price(Intent(["wash", code], code == "dry" ? 3980 : 3780), [Wash, dry]);

        Assert.Null(result.Price);
        Assert.Equal(expected, result.Rejection);
        Assert.Null(result.CalculatedTotalCents);
    }

    [Fact]
    public void I08_UnknownServicePrevailsOverDisabledAndDisabledOverMissingCapacity()
    {
        var disabledWash = Wash with { IsActive = false };
        var noCapacityDry = Dry with { HasOperationalMachine = false };

        Assert.Equal(BookingRejection.ServiceNotFound, BookingPricing.Price(Intent(["dry", "iron", "wash"], 1), [disabledWash, noCapacityDry]).Rejection);
        Assert.Equal(BookingRejection.ServiceDisabled, BookingPricing.Price(Intent(["dry", "wash"], 1), [disabledWash, noCapacityDry]).Rejection);
    }

    [Fact]
    public void I08_PriceWithMoreThanTwoDecimalsIsRefusedWithoutRounding()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            BookingPricing.Price(Intent(["wash"], 1891), [Wash with { CurrentPrice = 18.905m }, Dry]));

        Assert.Contains("duas casas", error.Message);
    }

    [Fact]
    public void I08_TotalBeyondNumeric12Comma2IsRefused()
    {
        var huge = Wash with { CurrentPrice = 9_999_999_999.99m };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BookingPricing.Price(Intent(["wash", "wash"], 1_999_999_999_998L), [huge, Dry]));
    }

    private static BookingIntent Intent(IReadOnlyList<string?> codes, long expected) =>
        BookingIntent.Create(codes, null, expected, 10).Intent!;
}
