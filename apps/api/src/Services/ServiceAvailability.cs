using Cicclo.Api.Laundry;

namespace Cicclo.Api.Services;

public enum UnavailabilityReason
{
    ServiceDisabled,
    NoOperationalMachine,
}

public readonly record struct Availability(bool CanBeBooked, UnavailabilityReason? Reason);

/// <summary>
/// Regra pura de contratabilidade: serviço inativo prevalece; ativo exige ao menos uma máquina operacional do tipo.
/// available, reserved e running são operacionais; somente out_of_service não é.
/// </summary>
public static class ServiceAvailability
{
    public static readonly IReadOnlyList<MachineState> OperationalStates = [MachineState.Available, MachineState.Reserved, MachineState.Running];

    public static Availability Evaluate(bool isActive, bool hasOperationalMachine) =>
        !isActive ? new(false, UnavailabilityReason.ServiceDisabled)
        : !hasOperationalMachine ? new(false, UnavailabilityReason.NoOperationalMachine)
        : new(true, null);
}
