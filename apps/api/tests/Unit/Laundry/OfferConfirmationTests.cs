using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Laundry;

/// <summary>Decisão pura de confirmação (QUEUE-08/09/13/14): prazo estrito now &lt; expiresAt e vínculo item↔máquina.</summary>
[Trait("Increment", "E03")]
[Trait("Task", "T3")]
public sealed class OfferConfirmationTests
{
    private static readonly Guid Item = Guid.Parse("00000000-0000-4000-8000-00000000c001");
    private static readonly Guid Other = Guid.Parse("00000000-0000-4000-8000-00000000c002");
    private static readonly Guid Machine = Guid.Parse("00000000-0000-4000-8000-000000000101");
    private static readonly Guid Machine2 = Guid.Parse("00000000-0000-4000-8000-000000000103");
    private static readonly DateTime Expires = new(2026, 10, 21, 12, 1, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-60_000_000L, OfferCheck.Confirmable)]
    [InlineData(-1_000_000L, OfferCheck.Confirmable)]
    [InlineData(-1L, OfferCheck.Confirmable)]
    [InlineData(0L, OfferCheck.Expired)]
    [InlineData(1L, OfferCheck.Expired)]
    [InlineData(1_000_000L, OfferCheck.Expired)]
    public void U01_OfferIsValidOnlyStrictlyBeforeItsDeadline(long microsecondsFromDeadline, OfferCheck expected)
    {
        var now = Expires.AddTicks(microsecondsFromDeadline * 10);

        Assert.Equal(expected, OfferPolicy.CheckConfirmation(Offer(), new MachineSlot(Machine, MachineState.Reserved, Item), now));
    }

    [Fact]
    public void U02_AnItemAlreadyInProgressIsARepeatEvenAfterTheDeadline()
    {
        var item = Offer() with { Status = BookingItemStatus.InProgress };

        Assert.Equal(OfferCheck.AlreadyStarted, OfferPolicy.CheckConfirmation(item, new MachineSlot(Machine, MachineState.Running, Item), Expires.AddHours(1)));
        Assert.Equal(OfferCheck.AlreadyStarted, OfferPolicy.CheckConfirmation(item, null, Expires.AddHours(1)));
    }

    [Theory]
    [InlineData(BookingItemStatus.PendingActivation)]
    [InlineData(BookingItemStatus.Queued)]
    public void U03_ItemsWithoutACurrentOfferCannotBeConfirmed(BookingItemStatus status)
    {
        var item = new LaundryItem(Item, status, status == BookingItemStatus.Queued ? 4 : null, null, null, null);

        Assert.Equal(OfferCheck.Invalid, OfferPolicy.CheckConfirmation(item, new MachineSlot(Machine, MachineState.Reserved, Item), Expires.AddSeconds(-30)));
        Assert.Equal(OfferCheck.Invalid, OfferPolicy.CheckConfirmation(item, null, Expires.AddSeconds(-30)));
    }

    [Theory]
    [InlineData("no machine")]
    [InlineData("other machine")]
    [InlineData("machine running")]
    [InlineData("machine available")]
    [InlineData("machine reserved for another item")]
    [InlineData("machine reserved without item")]
    public void U04_ADivergentItemMachineLinkIsInvalidEvenBeforeTheDeadline(string scenario)
    {
        MachineSlot? machine = scenario switch
        {
            "no machine" => null,
            "other machine" => new(Machine2, MachineState.Reserved, Item),
            "machine running" => new(Machine, MachineState.Running, Item),
            "machine available" => new(Machine, MachineState.Available, null),
            "machine reserved for another item" => new(Machine, MachineState.Reserved, Other),
            _ => new(Machine, MachineState.Reserved, null),
        };

        Assert.Equal(OfferCheck.Invalid, OfferPolicy.CheckConfirmation(Offer(), machine, Expires.AddSeconds(-30)));
        Assert.Equal(OfferCheck.Invalid, OfferPolicy.CheckConfirmation(Offer(), machine, Expires.AddSeconds(30)));
    }

    private static LaundryItem Offer() => new(Item, BookingItemStatus.ToBeConfirmed, 1, Expires.AddSeconds(-60), Machine, Expires);
}
