namespace Cicclo.Api.Bookings;

/// <summary>Estados de item existentes nesta etapa; ativação, fila e desfechos chegam com seus casos de uso.</summary>
public enum BookingItemStatus
{
    PendingActivation,
}

/// <summary>Uma execução contratada, com o preço histórico cobrado; duplicatas do mesmo serviço são itens distintos.</summary>
public sealed class BookingItem(Guid id, Guid bookingId, string serviceCode, decimal unitPrice, BookingItemStatus status)
{
    public Guid Id { get; private set; } = id;

    public Guid BookingId { get; private set; } = bookingId;

    public string ServiceCode { get; private set; } = serviceCode;

    public decimal UnitPrice { get; private set; } = unitPrice;

    public BookingItemStatus Status { get; private set; } = status;
}
