using Cicclo.Api.Laundry;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Laundry;

/// <summary>Escolha FIFO, máquinas elegíveis, prazo e posição derivada (QUEUE-04..06), sem I/O.</summary>
[Trait("Increment", "E03")]
[Trait("Task", "T2")]
public sealed class OfferPolicyTests
{
    private static readonly Guid M1 = Guid.Parse("00000000-0000-4000-8000-000000000101");
    private static readonly Guid M2 = Guid.Parse("00000000-0000-4000-8000-000000000103");
    private static readonly Guid M3 = Guid.Parse("00000000-0000-4000-8000-000000000105");
    private static readonly Guid A = Guid.Parse("00000000-0000-4000-8000-00000000a003");
    private static readonly Guid B = Guid.Parse("00000000-0000-4000-8000-00000000a002");
    private static readonly Guid C = Guid.Parse("00000000-0000-4000-8000-00000000a001");

    [Fact]
    public void U01_TheLowestTicketIsServedFirstRegardlessOfIdAndInputOrder()
    {
        var pairs = OfferPolicy.Pair([Free(M1)], [new(C, 30), new(A, 10), new(B, 20)]);

        Assert.Equal([new OfferPairing(A, M1)], pairs);
    }

    [Fact]
    public void U02_SeveralFreeMachinesServeTheQueueInOrderWithoutSkippingAnyone()
    {
        var pairs = OfferPolicy.Pair([Free(M3), Free(M1), Free(M2)], [new(C, 30), new(B, 20), new(A, 10)]);

        Assert.Equal([new OfferPairing(A, M1), new OfferPairing(B, M2), new OfferPairing(C, M3)], pairs);
    }

    [Fact]
    public void U03_MoreMachinesThanQueueLeavesTheExtraMachinesIdleAndMoreQueueThanMachinesWaits()
    {
        Assert.Equal([new OfferPairing(B, M1)], OfferPolicy.Pair([Free(M2), Free(M1)], [new(B, 7)]));
        Assert.Equal([new OfferPairing(A, M1), new OfferPairing(B, M2)], OfferPolicy.Pair([Free(M1), Free(M2)], [new(C, 3), new(B, 2), new(A, 1)]));
        Assert.Empty(OfferPolicy.Pair([Free(M1)], []));
        Assert.Empty(OfferPolicy.Pair([], [new(A, 1)]));
    }

    [Theory]
    [InlineData(MachineState.Reserved, false)]
    [InlineData(MachineState.Running, false)]
    [InlineData(MachineState.OutOfService, false)]
    [InlineData(MachineState.Available, true)]
    public void U04_OnlyAnAvailableMachineWithoutActiveItemReceivesAnOffer(MachineState state, bool withItem)
    {
        var slot = new MachineSlot(M1, state, withItem ? C : null);

        Assert.Empty(OfferPolicy.Pair([slot], [new(A, 1)]));
        Assert.Equal([new OfferPairing(A, M2)], OfferPolicy.Pair([slot, Free(M2)], [new(A, 1)]));
    }

    [Fact]
    public void U05_EqualTicketsAreOrderedByIdAsInPostgres()
    {
        var low = Guid.Parse("0000000a-0000-4000-8000-000000000000");
        var high = Guid.Parse("000000a0-0000-4000-8000-000000000000");

        Assert.Equal([new OfferPairing(low, M1)], OfferPolicy.Pair([Free(M1)], [new(high, 5), new(low, 5)]));
        Assert.Equal(2, OfferPolicy.Position(high, [new(high, 5), new(low, 5)]));
    }

    [Fact]
    public void U06_PositionIsDerivedFromTicketsAndIsNullOutsideTheQueue()
    {
        IReadOnlyList<QueuedItem> queue = [new(C, 30), new(A, 10), new(B, 20)];

        Assert.Equal((1, 2, 3), (OfferPolicy.Position(A, queue), OfferPolicy.Position(B, queue), OfferPolicy.Position(C, queue)));
        Assert.Null(OfferPolicy.Position(M1, queue));
        Assert.Null(OfferPolicy.Position(A, []));
    }

    [Fact]
    public void U07_DeadlineIsTheOfferInstantPlusTheConfiguredTimeoutExactly()
    {
        var offeredAt = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);

        Assert.Equal(offeredAt.AddTicks(600_000_000), OfferPolicy.ExpiresAt(offeredAt, TimeSpan.FromSeconds(60)));
        Assert.Equal(offeredAt.AddSeconds(90), OfferPolicy.ExpiresAt(offeredAt, TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void U08_DecisionInstantIsUtcTruncatedToTheMicrosecond()
    {
        var instant = OfferPolicy.DecisionInstant(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(-3)).AddTicks(1_234_567));

        Assert.Equal(new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560), instant);
        Assert.Equal(DateTimeKind.Utc, instant.Kind);
        Assert.Equal(instant, OfferPolicy.DecisionInstant(new DateTimeOffset(instant)));
    }

    private static MachineSlot Free(Guid id) => new(id, MachineState.Available, null);
}
