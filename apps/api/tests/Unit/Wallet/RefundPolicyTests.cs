using Cicclo.Api.Bookings;
using Cicclo.Api.Wallet;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Wallet;

/// <summary>Elegibilidade do cancelamento e valor do refund pelo preço histórico (REFUND-01/04/06), sem I/O.</summary>
[Trait("Increment", "E05")]
[Trait("Task", "T1")]
public sealed class RefundPolicyTests
{
    [Theory]
    [InlineData(BookingItemStatus.PendingActivation)]
    [InlineData(BookingItemStatus.Queued)]
    [InlineData(BookingItemStatus.ToBeConfirmed)]
    public void R01_ItemsNotYetStartedAreRefundedWithTheirHistoricalUnitPrice(BookingItemStatus status)
    {
        Assert.True(RefundPolicy.IsCancellable(status));
        Assert.Equal(new RefundPlan(RefundReason.Cancelled, 18.90m), RefundPolicy.PlanCancellation(status, 18.90m));
    }

    [Theory]
    [InlineData(BookingItemStatus.InProgress)]
    [InlineData(BookingItemStatus.Completed)]
    [InlineData(BookingItemStatus.Failed)]
    [InlineData(BookingItemStatus.Cancelled)]
    public void R02_StartedFinishedOrAlreadyCancelledItemsHaveNoCancellationRefund(BookingItemStatus status)
    {
        Assert.False(RefundPolicy.IsCancellable(status));
        Assert.Throws<InvalidOperationException>(() => RefundPolicy.PlanCancellation(status, 18.90m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-18.90)]
    public void R03_ANonPositiveHistoricalPriceIsNeverRefunded(double price)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RefundPolicy.PlanCancellation(BookingItemStatus.Queued, (decimal)price));
    }
}
