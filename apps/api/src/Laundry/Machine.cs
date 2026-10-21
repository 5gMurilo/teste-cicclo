namespace Cicclo.Api.Laundry;

public enum MachineType
{
    Washer,
    Dryer,
}

public enum MachineState
{
    Available,
    Reserved,
    Running,
    OutOfService,
}

/// <summary>Capacidade física mínima consultada pelo catálogo; transições chegam com suas próprias etapas.</summary>
public sealed class Machine(Guid id, string name, MachineType machineType, MachineState state, DateTime updatedAt)
{
    public Guid Id { get; private set; } = id;

    public string Name { get; private set; } = name;

    public MachineType MachineType { get; private set; } = machineType;

    public MachineState State { get; private set; } = state;

    public DateTime UpdatedAt { get; private set; } = updatedAt;
}
