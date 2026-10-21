using Cicclo.Api.Laundry;
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
}

/// <summary>
/// Ativa um item pago e pendente numa transação de lavanderia: wallet (mutex das escritas) → item do booking → coordenador
/// de fila (máquinas do tipo, itens, instante depois dos locks) → commit. Não debita nada: a cobrança aconteceu na compra.
/// Métricas de fila só depois do commit.
/// </summary>
public sealed partial class ActivateItem(ILaundrySessions sessions, QueueCoordinator coordinator, LaundryMetrics metrics, ILogger<ActivateItem> logger)
{
    public async Task<ActivateItemOutcome> ExecuteAsync(Guid bookingId, Guid itemId, CancellationToken cancellationToken)
    {
        ActivationResult result;
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

            await session.CommitAsync();
        }

        if (result.Changes is { } changes)
        {
            metrics.RecordCommitted([changes]);
        }

        LogActivated(itemId, result.Outcome, result.Item!.Status);
        return result.Outcome == ActivationOutcome.Activated ? ActivateItemOutcome.Activated : ActivateItemOutcome.AlreadyActive;
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Information, Message = "Ativação do item {ItemId}: {Outcome}, estado {Status}.")]
    private partial void LogActivated(Guid itemId, ActivationOutcome outcome, BookingItemStatus status);
}
