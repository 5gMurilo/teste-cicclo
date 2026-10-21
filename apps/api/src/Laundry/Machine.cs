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

/// <summary>
/// Capacidade física; reserved e running com item real apontam o item ativo. Running sem item é ocupação sintética da
/// simulação (IsDemoOccupied), sem booking nem wallet.
/// </summary>
public sealed class Machine(Guid id, string name, MachineType machineType, MachineState state, DateTime updatedAt)
{
    public Guid Id { get; private set; } = id;

    public string Name { get; private set; } = name;

    public MachineType MachineType { get; private set; } = machineType;

    public MachineState State { get; private set; } = state;

    public DateTime UpdatedAt { get; private set; } = updatedAt;

    /// <summary>Item com oferta corrente (reserved) ou em execução (running); null quando livre ou fora de serviço.</summary>
    public Guid? ActiveItemId { get; private set; }

    /// <summary>Running sem item ativo, criado pela simulação; false em qualquer outro estado.</summary>
    public bool IsDemoOccupied { get; private set; }
}
