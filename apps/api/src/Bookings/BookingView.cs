namespace Cicclo.Api.Bookings;

/// <summary>
/// Projeção autoritativa de um booking da wallet demo: cobrança e preços históricos, saldo atual da wallet e estado
/// atual de cada item, lidos num único snapshot. Serve criação, replay e consultas.
/// </summary>
public sealed record BookingView(
    Guid BookingId,
    decimal ChargedAmount,
    decimal RemainingBalance,
    DateTime CreatedAt,
    IReadOnlyList<BookingItemView> Items);

/// <summary>
/// Item no estado atual: máquina na oferta corrente ou na execução, posição derivada só na fila e prazo só na oferta
/// pendente de confirmação. Campos fora do estado aplicável ficam null.
/// </summary>
public sealed record BookingItemView(
    Guid ItemId,
    string ServiceCode,
    decimal UnitPrice,
    BookingItemStatus Status,
    Guid? MachineId,
    int? QueuePosition,
    DateTime? ExpiresAt)
{
    /// <summary>
    /// Aplica a regra de nulabilidade do contrato sobre os valores persistidos: expires_at continua gravado depois da
    /// confirmação, mas só é prazo enquanto a oferta aguarda confirmação.
    /// </summary>
    public static BookingItemView FromPersisted(
        Guid itemId, string serviceCode, decimal unitPrice, BookingItemStatus status, Guid? machineId, int? queuePosition, DateTime? expiresAt) =>
        new(
            itemId,
            serviceCode,
            unitPrice,
            status,
            status is BookingItemStatus.ToBeConfirmed or BookingItemStatus.InProgress ? machineId : null,
            status == BookingItemStatus.Queued ? queuePosition : null,
            status == BookingItemStatus.ToBeConfirmed ? expiresAt : null);
}
