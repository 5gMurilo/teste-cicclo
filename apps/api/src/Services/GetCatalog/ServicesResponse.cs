using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Cicclo.Api.Services.GetCatalog;

/// <summary>Catálogo de serviços ordenado por code; a ordem não indica prioridade de execução.</summary>
public sealed record ServicesResponse(
    [property: Required, Description("Serviços ordenados por code.")] IReadOnlyList<ServiceItemResponse> Services);

/// <summary>Serviço do catálogo com preço atual e contratabilidade informativa.</summary>
public sealed record ServiceItemResponse(
    [property: Required, Description("Código estável do serviço.")] string Code,
    [property: Required, Description("Nome de exibição.")] string Name,
    [property: Required, Description("Tipo de equipamento que executa o serviço.")] MachineTypeContract MachineType,
    [property: Required, Range(1, 999_999_999_999), Description("Preço atual em centavos inteiros (int64).")] long UnitPriceCents,
    [property: Required, Description("Moeda ISO 4217; sempre BRL.")] string Currency,
    [property: Required, Description("Serviço habilitado no catálogo.")] bool IsActive,
    [property: Required, Description("Informativo: indica se uma compra seria aceita agora. A compra revalida no servidor.")] bool CanBeBooked,
    [property: Required, Description("Motivo quando canBeBooked é false; null quando contratável.")] UnavailabilityReasonContract? UnavailabilityReason);

[JsonConverter(typeof(JsonStringEnumConverter<MachineTypeContract>))]
public enum MachineTypeContract
{
    [JsonStringEnumMemberName("washer")]
    Washer,

    [JsonStringEnumMemberName("dryer")]
    Dryer,
}

[JsonConverter(typeof(JsonStringEnumConverter<UnavailabilityReasonContract>))]
public enum UnavailabilityReasonContract
{
    [JsonStringEnumMemberName("SERVICE_DISABLED")]
    ServiceDisabled,

    [JsonStringEnumMemberName("NO_OPERATIONAL_MACHINE")]
    NoOperationalMachine,
}
