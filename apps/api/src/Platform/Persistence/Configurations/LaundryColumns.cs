using Cicclo.Api.Laundry;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cicclo.Api.Platform.Persistence.Configurations;

/// <summary>Valores textuais persistidos para os enums de máquina; as constraints SQL usam os mesmos literais.</summary>
internal static class LaundryColumns
{
    public static readonly ValueConverter<MachineType, string> MachineTypeConverter =
        new(value => ToText(value), value => ParseMachineType(value));

    public static readonly ValueConverter<MachineState, string> MachineStateConverter =
        new(value => ToText(value), value => ParseMachineState(value));

    public static string ToText(MachineType value) => value switch
    {
        MachineType.Washer => "washer",
        MachineType.Dryer => "dryer",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Tipo de máquina sem representação persistida."),
    };

    public static string ToText(MachineState value) => value switch
    {
        MachineState.Available => "available",
        MachineState.Reserved => "reserved",
        MachineState.Running => "running",
        MachineState.OutOfService => "out_of_service",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Estado de máquina sem representação persistida."),
    };

    private static MachineType ParseMachineType(string value) => value switch
    {
        "washer" => MachineType.Washer,
        "dryer" => MachineType.Dryer,
        _ => throw new InvalidOperationException($"Tipo de máquina persistido desconhecido: '{value}'."),
    };

    private static MachineState ParseMachineState(string value) => value switch
    {
        "available" => MachineState.Available,
        "reserved" => MachineState.Reserved,
        "running" => MachineState.Running,
        "out_of_service" => MachineState.OutOfService,
        _ => throw new InvalidOperationException($"Estado de máquina persistido desconhecido: '{value}'."),
    };
}
