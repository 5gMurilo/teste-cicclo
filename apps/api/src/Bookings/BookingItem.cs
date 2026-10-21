namespace Cicclo.Api.Bookings;

/// <summary>
/// Estados de item. Cancelled é o término voluntário antes de iniciar (com refund único); completed e failed são os
/// desfechos de execução, persistíveis pelo schema e publicados pelos comandos de simulação quando entregues.
/// </summary>
public enum BookingItemStatus
{
    PendingActivation,
    Queued,
    ToBeConfirmed,
    InProgress,
    Cancelled,
    Completed,
    Failed,
}

/// <summary>
/// Uma execução contratada, com o preço histórico cobrado; duplicatas do mesmo serviço são itens distintos.
/// Ao entrar na fila recebe ticket e queued_at; a oferta vincula a máquina com prazo; a confirmação registra o início.
/// </summary>
public sealed class BookingItem(Guid id, Guid bookingId, string serviceCode, decimal unitPrice, BookingItemStatus status)
{
    public Guid Id { get; private set; } = id;

    public Guid BookingId { get; private set; } = bookingId;

    public string ServiceCode { get; private set; } = serviceCode;

    public decimal UnitPrice { get; private set; } = unitPrice;

    public BookingItemStatus Status { get; private set; } = status;

    /// <summary>Ticket da última entrada na fila (queue_ticket_seq); ordena o FIFO com o id como desempate.</summary>
    public long? QueueTicket { get; private set; }

    /// <summary>Instante UTC da última entrada na fila; mede a espera, que created_at não mede.</summary>
    public DateTime? QueuedAt { get; private set; }

    /// <summary>Máquina da oferta corrente ou da execução.</summary>
    public Guid? MachineId { get; private set; }

    public DateTime? OfferedAt { get; private set; }

    /// <summary>Prazo da oferta: confirmar só vale com now &lt; ExpiresAt.</summary>
    public DateTime? ExpiresAt { get; private set; }

    public DateTime? StartedAt { get; private set; }

    /// <summary>Instante UTC do cancelamento; existe só em cancelled.</summary>
    public DateTime? CancelledAt { get; private set; }
}
