using Cicclo.Api.Laundry;
using Cicclo.Api.Services;
using Xunit;

namespace Cicclo.Api.Tests.Unit.Services;

[Trait("Increment", "E01")]
[Trait("Task", "T7")]
public sealed class ServiceAvailabilityTests
{
    [Theory]
    [InlineData(true, true, true, null)]
    [InlineData(true, false, false, UnavailabilityReason.NoOperationalMachine)]
    [InlineData(false, true, false, UnavailabilityReason.ServiceDisabled)]
    [InlineData(false, false, false, UnavailabilityReason.ServiceDisabled)]
    public void C07_TruthTableGivesDisabledPriorityOverCapacity(
        bool isActive, bool hasOperationalMachine, bool canBeBooked, UnavailabilityReason? reason) =>
        Assert.Equal(new Availability(canBeBooked, reason), ServiceAvailability.Evaluate(isActive, hasOperationalMachine));

    [Fact]
    public void C07_OnlyOutOfServiceIsNotOperational() =>
        Assert.Equal(
            [MachineState.Available, MachineState.Reserved, MachineState.Running],
            Enum.GetValues<MachineState>().Where(ServiceAvailability.OperationalStates.Contains));
}
