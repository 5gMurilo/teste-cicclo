using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry.GetMachines;

namespace Cicclo.Api.Laundry.Simulation;

/// <summary>Resultado de um comando de simulação: a máquina atual e o item afetado, quando existe.</summary>
public sealed record SimulationResponse(
    [property: Required, Description("Máquina no estado atual, lida depois do comando.")] MachineResponse Machine,
    [property: Required, Description("Item real afetado pelo comando, na forma dos itens do booking; null quando a máquina não tinha item (ocupação sintética).")] BookingItemResponse? Item);
