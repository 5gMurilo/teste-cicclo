using Cicclo.Api.Laundry;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Laundry;

/// <summary>Regra única de vencimento usada pela reconciliação e pela confirmação (QUEUE-09/10): now &gt;= expiresAt.</summary>
[Trait("Increment", "E03")]
[Trait("Task", "T4")]
public sealed class OfferExpiryTests
{
    private static readonly DateTime Expires = new(2026, 10, 21, 12, 1, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-1_000_000L, false)]
    [InlineData(-10L, false)]
    [InlineData(0L, true)]
    [InlineData(10L, true)]
    [InlineData(1_000_000_000L, true)]
    public void U01_AnOfferIsDueExactlyAtItsDeadlineAndAfter(long ticksFromDeadline, bool expired) =>
        Assert.Equal(expired, OfferPolicy.IsExpired(Expires, Expires.AddTicks(ticksFromDeadline)));
}
