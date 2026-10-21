using Cicclo.Api.Bookings;
using Cicclo.Api.Wallet;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Wallet;

/// <summary>Refund de falha confirmada (SIM-04, REFUND-06): só a execução em andamento, pelo preço histórico, sem I/O.</summary>
[Trait("Increment", "E06")]
[Trait("Task", "T5")]
public sealed class MachineFailureRefundPolicyTests
{
    [Fact]
    public void F01_TheRunningItemIsRefundedWithItsHistoricalUnitPriceForMachineFailure() =>
        Assert.Equal(new RefundPlan(RefundReason.MachineFailure, 18.90m), RefundPolicy.PlanMachineFailure(BookingItemStatus.InProgress, 18.90m));

    [Theory]
    [InlineData(BookingItemStatus.PendingActivation)]
    [InlineData(BookingItemStatus.Queued)]
    [InlineData(BookingItemStatus.ToBeConfirmed)]
    [InlineData(BookingItemStatus.Cancelled)]
    [InlineData(BookingItemStatus.Completed)]
    [InlineData(BookingItemStatus.Failed)]
    public void F02_ItemsThatAreNotRunningHaveNoFailureRefund(BookingItemStatus status) =>
        Assert.Throws<InvalidOperationException>(() => RefundPolicy.PlanMachineFailure(status, 18.90m));

    [Fact]
    public void F03_ANonPositiveHistoricalPriceIsNeverRefunded() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RefundPolicy.PlanMachineFailure(BookingItemStatus.InProgress, 0m));
}
