using Cicclo.Api.Laundry;

namespace Cicclo.Api.Services;

/// <summary>Serviço do catálogo, identificado pelo código estável e atendido por um tipo de máquina.</summary>
public sealed class LaundryService(string code, string name, MachineType machineType, decimal currentPrice, bool isActive, DateTime createdAt)
{
    public string Code { get; private set; } = code;

    public string Name { get; private set; } = name;

    public MachineType MachineType { get; private set; } = machineType;

    public decimal CurrentPrice { get; private set; } = currentPrice;

    public bool IsActive { get; private set; } = isActive;

    public DateTime CreatedAt { get; private set; } = createdAt;
}
