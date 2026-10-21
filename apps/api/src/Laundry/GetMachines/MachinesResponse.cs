using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Cicclo.Api.Services.GetCatalog;

namespace Cicclo.Api.Laundry.GetMachines;

/// <summary>Máquinas da demonstração em ordem de nome, com o estado persistido no instante da leitura.</summary>
public sealed record MachinesResponse(
    [property: Required, Description("Máquinas ordenadas por name.")] IReadOnlyList<MachineResponse> Machines);

/// <summary>Máquina e seu vínculo atual: item real (oferta ou execução) ou ocupação sintética da simulação.</summary>
public sealed record MachineResponse(
    [property: Required, Description("Identificador (UUID) estável da máquina.")] Guid MachineId,
    [property: Required, Description("Nome de exibição, único.")] string Name,
    [property: Required, Description("Tipo de equipamento.")] MachineTypeContract MachineType,
    [property: Required, Description("Estado persistido: available (livre), reserved (oferta a um item), running (em execução) ou out_of_service.")] MachineStateContract State,
    [property: Required, Description("Item com a oferta corrente (reserved) ou em execução real (running); null quando livre, fora de serviço ou em ocupação sintética.")] Guid? ActiveItemId,
    [property: Required, Description("true somente em running sem item: ocupação sintética da simulação, sem booking, débito ou wallet.")] bool IsDemoOccupied)
{
    public static MachineResponse From(Machine machine) =>
        new(
            machine.Id,
            machine.Name,
            machine.MachineType switch
            {
                Laundry.MachineType.Washer => MachineTypeContract.Washer,
                Laundry.MachineType.Dryer => MachineTypeContract.Dryer,
                _ => throw new ArgumentOutOfRangeException(nameof(machine), machine.MachineType, "Tipo de máquina sem contrato HTTP."),
            },
            machine.State switch
            {
                MachineState.Available => MachineStateContract.Available,
                MachineState.Reserved => MachineStateContract.Reserved,
                MachineState.Running => MachineStateContract.Running,
                MachineState.OutOfService => MachineStateContract.OutOfService,
                _ => throw new ArgumentOutOfRangeException(nameof(machine), machine.State, "Estado de máquina sem contrato HTTP."),
            },
            machine.ActiveItemId,
            machine.IsDemoOccupied);
}

[JsonConverter(typeof(JsonStringEnumConverter<MachineStateContract>))]
public enum MachineStateContract
{
    [JsonStringEnumMemberName("available")]
    Available,

    [JsonStringEnumMemberName("reserved")]
    Reserved,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("out_of_service")]
    OutOfService,
}
