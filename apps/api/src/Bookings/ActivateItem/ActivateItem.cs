using Cicclo.Api.Laundry;
using Cicclo.Api.Laundry.Notifications;
using Cicclo.Api.Wallet;

namespace Cicclo.Api.Bookings.ActivateItem;

public enum ActivateItemOutcome
{
    /// <summary>O item pendente entrou na fila e, havendo máquina livre sem ninguém à frente, recebeu a oferta.</summary>
    Activated,

    /// <summary>O item já estava em queued, to_be_confirmed ou in_progress: sem ticket novo nem cobrança.</summary>
    AlreadyActive,

    /// <summary>Item inexistente ou de outro booking.</summary>
    NotFound,

    /// <summary>Item cancelado ou com desfecho de execução: nada mudou.</summary>
    InvalidState,
}

/// <summary>
/// Ativa um item pago e pendente numa transação de lavanderia: wallet (mutex das escritas) → item do booking → coordenador
/// de fila (máquinas do tipo, itens, instante depois dos locks) → commit. Não debita nada: a cobrança aconteceu na compra.
/// Métricas e avisos realtime (opcionais) só depois do commit.
/// </summary>
public sealed partial class ActivateItem(
    ILaundrySessions sessions, QueueCoordinator coordinator, LaundryMetrics metrics, ILogger<ActivateItem> logger, LaundryChangeNotifier? notifier = null)
{
    public async Task<ActivateItemOutcome> ExecuteAsync(Guid bookingId, Guid itemId, CancellationToken cancellationToken)
    {
        ActivationResult result;
        IReadOnlyList<RevisedBooking> revised;
        await using (var session = await sessions.OpenAsync(cancellationToken))
        {
            await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            if (await session.LocateItemAsync(itemId, cancellationToken) is not { } location || location.BookingId != bookingId)
            {
                return ActivateItemOutcome.NotFound;
            }

            result = await coordinator.ActivateAsync(session, itemId, cancellationToken);
            if (result.Outcome == ActivationOutcome.NotFound)
            {
                return ActivateItemOutcome.NotFound;
            }

            if (result.Outcome == ActivationOutcome.InvalidState)
            {
                return ActivateItemOutcome.InvalidState;
            }

            await session.CommitAsync();
            revised = session.CommittedRevisions;
        }

        if (result.Changes is { } changes)
        {
            metrics.RecordCommitted([changes]);
        }

        if (notifier is not null)
        {
            await notifier.NotifyAsync(new CommittedLaundryChanges
            {
                Bookings = revised,
                Queues = result.Changes is { } queue ? [queue] : [],
                QueueTypes = result.Outcome == ActivationOutcome.Activated && result.Type is { } type ? [type] : [],
            });
        }

        LogActivated(itemId, result.Outcome, result.Item!.Status);
        return result.Outcome == ActivationOutcome.Activated ? ActivateItemOutcome.Activated : ActivateItemOutcome.AlreadyActive;
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Information, Message = "Ativação do item {ItemId}: {Outcome}, estado {Status}.")]
    private partial void LogActivated(Guid itemId, ActivationOutcome outcome, BookingItemStatus status);
}
