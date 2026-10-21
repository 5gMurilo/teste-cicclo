using Cicclo.Api.Laundry;

namespace Cicclo.Api.Services.GetCatalog;

/// <summary>Projeção interna de um serviço do catálogo, com preço em decimal e contratabilidade já avaliada.</summary>
public sealed record ServiceCatalogEntry(
    string Code,
    string Name,
    MachineType MachineType,
    decimal CurrentPrice,
    bool IsActive,
    Availability Availability);
