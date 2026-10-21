using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry.GetMachines;

namespace Cicclo.Api.Laundry.ConfirmIncident;

/// <summary>Incidente confirmado com a máquina atual e o item real da execução que falhou.</summary>
public sealed record ConfirmIncidentResponse(
    [property: Required, Description("Identificador (UUID) do incidente confirmado.")] Guid IncidentId,
    [property: Required, Description("Estado do incidente: confirmed (defeito confirmado pela simulação).")] ConfirmedIncidentStatusContract Status,
    [property: Required, Description("Máquina do incidente no estado atual, lida depois do comando.")] MachineResponse Machine,
    [property: Required, Description("Item da execução relatada, na forma dos itens do booking (failed).")] BookingItemResponse Item);

[JsonConverter(typeof(JsonStringEnumConverter<ConfirmedIncidentStatusContract>))]
public enum ConfirmedIncidentStatusContract
{
    [JsonStringEnumMemberName("confirmed")]
    Confirmed,
}
