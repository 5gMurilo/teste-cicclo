using Cicclo.Api.Bookings;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Bookings;

[Trait("Increment", "E02")]
[Trait("Task", "T2")]
public sealed class BookingIntentTests
{
    private const int Max = 10;

    [Fact]
    public void I01_EquivalentOrderProducesTheSameCanonicalIntent()
    {
        var first = Valid(["wash", "dry", "wash"], "dry", 5870);
        var second = Valid(["dry", "wash", "wash"], "dry", 5870);

        Assert.Equal(["dry", "wash", "wash"], first.ServiceCodes);
        Assert.Equal(first.ServiceCodes, second.ServiceCodes);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        // Representação canônica estável entre versões: SHA-256 de
        // "cicclo.booking-intent.v1\ncodes:3\n3:dry\n4:wash\n4:wash\npreferred:3:dry\nexpected:5870", calculado fora do código.
        Assert.Equal("b615d8aa62dc3cd9f7a5845036fbcd020efe9f31d205d02f59c439d5f892c136", first.Fingerprint);
    }

    [Fact]
    public void I02_OmittedAndNullPreferenceAreTheSameAbsentPreference()
    {
        var omitted = BookingIntent.Create(["wash"], preferredFirstServiceCode: default, 1890, Max).Intent!;
        var explicitNull = Valid(["wash"], null, 1890);

        Assert.Null(omitted.PreferredFirstServiceCode);
        Assert.Null(explicitNull.PreferredFirstServiceCode);
        Assert.Equal(omitted.Fingerprint, explicitNull.Fingerprint);
        Assert.Equal("26fd40fb133e7869152da3af9ab3202cecde3294ea4e14cc88ad1bb768e93d44", explicitNull.Fingerprint);
        Assert.NotEqual(explicitNull.Fingerprint, Valid(["wash"], "wash", 1890).Fingerprint);
    }

    [Fact]
    public void I03_DistinctMultiplicityIsADistinctIntent()
    {
        var single = Valid(["wash"], null, 3780);
        var twice = Valid(["wash", "wash"], null, 3780);
        var mixed = Valid(["dry", "wash"], null, 3980);
        var mixedWithExtraDry = Valid(["dry", "wash", "dry"], null, 3980);

        Assert.Equal(["wash", "wash"], twice.ServiceCodes);
        Assert.Equal(["dry", "dry", "wash"], mixedWithExtraDry.ServiceCodes);
        Assert.NotEqual(single.Fingerprint, twice.Fingerprint);
        Assert.NotEqual(mixed.Fingerprint, mixedWithExtraDry.Fingerprint);
    }

    [Fact]
    public void I04_DistinctPreferenceOrExpectedTotalIsADistinctIntent()
    {
        string[] codes = ["dry", "wash"];
        var fingerprints = new[]
        {
            Valid(codes, null, 3980).Fingerprint,
            Valid(codes, "wash", 3980).Fingerprint,
            Valid(codes, "dry", 3980).Fingerprint,
            Valid(codes, null, 3981).Fingerprint,
            Valid(codes, "wash", 3981).Fingerprint,
        };

        Assert.Equal(fingerprints.Length, fingerprints.Distinct(StringComparer.Ordinal).Count());
        Assert.All(fingerprints, fingerprint => Assert.Matches("^[0-9a-f]{64}$", fingerprint));
    }

    [Theory]
    [InlineData(1, 10, true)]
    [InlineData(10, 10, true)]
    [InlineData(11, 10, false)]
    [InlineData(0, 10, false)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, false)]
    public void I05_BatchSizeMustBeBetweenOneAndTheConfiguredMaximum(int count, int max, bool accepted)
    {
        var result = BookingIntent.Create(Enumerable.Repeat<string?>("wash", count).ToList(), null, 1890L * Math.Max(count, 1), max);

        Assert.Equal(accepted ? null : BookingRejection.InvalidServiceSelection, result.Rejection);
        Assert.Equal(accepted ? count : null, result.Intent?.ServiceCodes.Count);
    }

    [Fact]
    public void I05_MissingServiceCodesIsAnInvalidSelection()
    {
        var result = BookingIntent.Create(null, null, 1890, Max);

        Assert.Null(result.Intent);
        Assert.Equal(BookingRejection.InvalidServiceSelection, result.Rejection);
    }

    [Theory]
    [InlineData("dry")]
    [InlineData("WASH")]
    [InlineData("")]
    [InlineData("wash ")]
    public void I06_PreferenceOutsideTheBatchIsAnInvalidSelection(string preferred)
    {
        var result = BookingIntent.Create(["wash", "wash"], preferred, 3780, Max);

        Assert.Null(result.Intent);
        Assert.Equal(BookingRejection.InvalidServiceSelection, result.Rejection);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void I08_NonPositiveExpectedTotalIsAValidationError(long expected)
    {
        var result = BookingIntent.Create(["wash"], null, expected, Max);

        Assert.Null(result.Intent);
        Assert.Equal(BookingRejection.ValidationError, result.Rejection);
    }

    [Fact]
    public void I08_NullServiceCodeIsAValidationErrorBeforeSelectionRules()
    {
        var result = BookingIntent.Create(Enumerable.Repeat<string?>(null, 11).ToList(), "dry", 1890, Max);

        Assert.Null(result.Intent);
        Assert.Equal(BookingRejection.ValidationError, result.Rejection);
    }

    private static BookingIntent Valid(IReadOnlyList<string?> codes, string? preferred, long expected)
    {
        var result = BookingIntent.Create(codes, preferred, expected, Max);
        Assert.Null(result.Rejection);
        return result.Intent!;
    }
}
