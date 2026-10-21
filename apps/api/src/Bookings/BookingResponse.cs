using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Cicclo.Api.Platform.Http;

namespace Cicclo.Api.Bookings;

/// <summary>Contratação da wallet demo: cobrança histórica, saldo atual e estado atual de cada execução.</summary>
public sealed record BookingResponse(
    [property: Required, Description("Identificador do booking.")] Guid BookingId,
    [property: Required, Description("Moeda ISO 4217; sempre BRL.")] string Currency,
    [property: Required, Range(1, 999_999_999_999), Description("Valor cobrado na compra, em centavos inteiros (int64); histórico, não muda com o catálogo.")] long ChargedAmountCents,
    [property: Required, Range(0, 999_999_999_999), Description("Saldo atual da wallet demo em centavos inteiros (int64), lido no mesmo snapshot dos itens; pode refletir operações posteriores à compra.")] long RemainingBalanceCents,
    [property: Required, Description("Instante UTC da compra.")] DateTime CreatedAt,
    [property: Required, Description("Execuções contratadas, ordenadas por serviceCode e itemId; duplicatas são itens distintos.")] IReadOnlyList<BookingItemResponse> Items)
{
    public static BookingResponse From(BookingView view) =>
        new(
            view.BookingId,
            MoneyCents.Currency,
            MoneyCents.FromDecimal(view.ChargedAmount),
            MoneyCents.FromDecimal(view.RemainingBalance),
            view.CreatedAt,
            [.. view.Items.Select(BookingItemResponse.From)]);
}

/// <summary>Uma execução contratada no estado atual; campos fora do estado aplicável são null, nunca omitidos.</summary>
public sealed record BookingItemResponse(
    [property: Required, Description("Identificador da execução.")] Guid ItemId,
    [property: Required, Description("Código do serviço contratado.")] string ServiceCode,
    [property: Required, Range(1, 999_999_999_999), Description("Preço histórico cobrado por esta execução, em centavos inteiros (int64).")] long UnitPriceCents,
    [property: Required, Description("Estado atual: pending_activation (paga, aguardando ativação), queued (na fila), to_be_confirmed (máquina ofertada aguardando confirmação) ou in_progress (em execução).")] BookingItemStatusContract Status,
    [property: Required, Description("Máquina da oferta corrente (to_be_confirmed) ou da execução (in_progress); null nos demais estados.")] Guid? MachineId,
    [property: Required, Range(1, int.MaxValue), Description("Posição 1-based derivada da fila do tipo de máquina (ticket, depois id); null fora de queued. Não é promessa de posição futura.")] int? QueuePosition,
    [property: Required, Description("Prazo UTC para confirmar a oferta; a confirmação só vale antes dele. Null fora de to_be_confirmed. Pode estar no passado até a reconciliação.")] DateTime? ExpiresAt)
{
    public static BookingItemResponse From(BookingItemView item) =>
        new(
            item.ItemId,
            item.ServiceCode,
            MoneyCents.FromDecimal(item.UnitPrice),
            item.Status switch
            {
                BookingItemStatus.PendingActivation => BookingItemStatusContract.PendingActivation,
                BookingItemStatus.Queued => BookingItemStatusContract.Queued,
                BookingItemStatus.ToBeConfirmed => BookingItemStatusContract.ToBeConfirmed,
                BookingItemStatus.InProgress => BookingItemStatusContract.InProgress,
                _ => throw new ArgumentOutOfRangeException(nameof(item), item.Status, "Estado de item sem contrato HTTP."),
            },
            item.MachineId,
            item.QueuePosition,
            item.ExpiresAt);
}

[JsonConverter(typeof(JsonStringEnumConverter<BookingItemStatusContract>))]
public enum BookingItemStatusContract
{
    [JsonStringEnumMemberName("pending_activation")]
    PendingActivation,

    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("to_be_confirmed")]
    ToBeConfirmed,

    [JsonStringEnumMemberName("in_progress")]
    InProgress,
}
