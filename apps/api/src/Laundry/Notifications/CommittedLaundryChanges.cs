using Cicclo.Api.Bookings;

namespace Cicclo.Api.Laundry.Notifications;

/// <summary>
/// Mudanças que um caso de uso real acabou de confirmar no PostgreSQL, montadas somente depois de o commit retornar com
/// sucesso: bookings revisados com a revisão atribuída na transação, mudanças de fila do coordenador, tipos cuja fila
/// ganhou ou perdeu um item fora delas, débito/refund e falha de máquina. Não é event store, barramento nem agregado
/// persistido; existe só para virar avisos (tabela de eventos E09).
/// </summary>
public sealed record CommittedLaundryChanges
{
    public IReadOnlyList<RevisedBooking> Bookings { get; init; } = [];

    public IReadOnlyList<QueueChanges> Queues { get; init; } = [];

    /// <summary>Tipos cuja fila ganhou ou perdeu um item queued por ativação ou cancelamento, além dos de <see cref="Queues"/>.</summary>
    public IReadOnlyList<MachineType> QueueTypes { get; init; } = [];

    /// <summary>Débito ou refund confirmado nesta transação.</summary>
    public bool WalletChanged { get; init; }

    /// <summary>Máquina cuja falha (real ou sintética) foi confirmada nesta transação.</summary>
    public Guid? FailedMachineId { get; init; }

    /// <summary>
    /// Avisos na ordem de envio: falha, ofertas vencidas e novas (instantes persistidos da oferta), filas por tipo,
    /// bookings com a versão confirmada e wallet. Sem mudança, lista vazia: leitura e replay puro não reemitem nada.
    /// </summary>
    public IReadOnlyList<LaundryNotification> ToNotifications(DateTime occurredAt)
    {
        var notifications = new List<LaundryNotification>();
        if (FailedMachineId is { } machineId)
        {
            notifications.Add(LaundryNotification.MachineFailed(machineId, occurredAt));
        }

        foreach (var queue in Queues)
        {
            notifications.AddRange(queue.Expired.Select(expired => LaundryNotification.MachineOfferExpired(expired.ItemId, expired.RequeuedAt)));
            notifications.AddRange(queue.Offers.Select(offer => LaundryNotification.MachineOffered(offer.ItemId, offer.MachineId, offer.ExpiresAt, offer.OfferedAt)));
        }

        var queueTypes = Queues
            .Where(queue => queue.Expired.Count > 0 || queue.Offers.Count > 0)
            .Select(queue => queue.Type)
            .Concat(QueueTypes)
            .Distinct()
            .Order();
        notifications.AddRange(queueTypes.Select(type => LaundryNotification.QueueUpdated(type, occurredAt)));
        notifications.AddRange(Bookings.Select(booking => LaundryNotification.BookingUpdated(booking.BookingId, booking.Revision, occurredAt)));
        if (WalletChanged)
        {
            notifications.Add(LaundryNotification.WalletUpdated(occurredAt));
        }

        return notifications;
    }
}

/// <summary>
/// Efeito pós-commit dos avisos realtime (D12): chamado pelos casos de uso só depois do commit confirmado, fora da
/// transação e dos locks, com orçamento próprio finito (não o token da requisição, que pode já estar cancelado). Qualquer
/// falha ou estouro do orçamento é registrada e engolida: o resultado REST/financeiro já confirmado nunca muda por causa
/// do transporte, não há retry nem rollback. Sem entrega garantida: o REST continua a fonte de recuperação.
/// </summary>
public sealed partial class LaundryChangeNotifier(ILaundryNotifications notifications, TimeProvider time, ILogger<LaundryChangeNotifier> logger)
{
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);

    public async Task NotifyAsync(CommittedLaundryChanges changes)
    {
        try
        {
            var batch = changes.ToNotifications(OfferPolicy.DecisionInstant(time.GetUtcNow()));
            if (batch.Count == 0)
            {
                return;
            }

            // Relógio real: o orçamento limita a espera do chamador mesmo se o transporte ignorar o cancelamento.
            using var budget = new CancellationTokenSource(Budget);
            await notifications.PublishAsync(batch, budget.Token).WaitAsync(budget.Token);
        }
        catch (Exception exception)
        {
            LogNotSent(exception);
        }
    }

    [LoggerMessage(EventId = 900, Level = LogLevel.Warning, Message = "Avisos realtime de uma mudança já confirmada não foram enviados; o estado segue recuperável por REST.")]
    private partial void LogNotSent(Exception exception);
}
