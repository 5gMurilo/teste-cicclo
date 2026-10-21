using Cicclo.Api.Wallet.GetBalance;
using Cicclo.Api.Wallet.ReadCaching;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Wallet;

/// <summary>Forma e intervalo da projeção guardada no cache (CACHE-02/05), sem I/O: o que não é saldo válido vira miss.</summary>
[Trait("Increment", "E08")]
[Trait("Task", "T1")]
public sealed class WalletCacheEntryTests
{
    private static readonly DateTime UpdatedAt = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("50.00", 5_000)]
    [InlineData("9999999999.99", 999_999_999_999)]
    public void U01_ValidProjectionsRoundtripThroughIntegerCents(string balance, long cents)
    {
        var projection = new WalletBalance(decimal.Parse(balance, System.Globalization.CultureInfo.InvariantCulture), "BRL", UpdatedAt);

        var json = WalletCacheEntry.Serialize(projection);

        Assert.Equal($"{{\"balanceCents\":{cents},\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}}", json);
        Assert.True(WalletCacheEntry.TryParse(json, out var parsed));
        Assert.Equal(projection, parsed);
    }

    [Theory]
    [InlineData("-0.01", "BRL", DateTimeKind.Utc)]
    [InlineData("10000000000.00", "BRL", DateTimeKind.Utc)]
    [InlineData("0.001", "BRL", DateTimeKind.Utc)]
    [InlineData("50.00", "USD", DateTimeKind.Utc)]
    [InlineData("50.00", "BRL", DateTimeKind.Unspecified)]
    [InlineData("50.00", "BRL", DateTimeKind.Local)]
    public void U02_ProjectionsOutsideTheContractAreNeverSerialized(string balance, string currency, DateTimeKind kind)
    {
        var projection = new WalletBalance(decimal.Parse(balance, System.Globalization.CultureInfo.InvariantCulture), currency, DateTime.SpecifyKind(UpdatedAt, kind));

        Assert.Null(WalletCacheEntry.Serialize(projection));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"balanceCents\":-1,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}")]
    [InlineData("{\"balanceCents\":1000000000000,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}")]
    [InlineData("{\"balanceCents\":50.5,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}")]
    [InlineData("{\"balanceCents\":\"5000\",\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}")]
    [InlineData("{\"balanceCents\":5000,\"currency\":\"USD\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}")]
    [InlineData("{\"balanceCents\":5000,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00\"}")]
    [InlineData("{\"balanceCents\":5000,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000+03:00\"}")]
    [InlineData("{\"balanceCents\":5000,\"currency\":\"BRL\"}")]
    [InlineData("{\"balanceCents\":5000,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\",\"idempotencyKey\":\"k\"}")]
    [InlineData("{\"balanceCents\":5000,\"balanceCents\":5000,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}")]
    [InlineData("{\"BalanceCents\":5000,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}")]
    public void U03_CorruptNegativeOrMisshapedEntriesAreRejected(string? json)
    {
        Assert.False(WalletCacheEntry.TryParse(json, out var parsed));
        Assert.Null(parsed);
    }
}
