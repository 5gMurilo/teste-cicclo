using Cicclo.Api.Bookings;
using Cicclo.Api.Wallet;
using Cicclo.Api.Wallet.ReadCaching;

namespace Cicclo.Api.Laundry.ConfirmIncident;

public enum ConfirmIncidentOutcome
{
    /// <summary>O relato foi confirmado agora: a execução falhou pelo caso de falha compartilhado, com refund único.</summary>
    Confirmed,

    /// <summary>O relato já estava confirmado: devolve o estado atual, sem nova falha nem refund.</summary>
    AlreadyConfirmed,

    NotFound,

    /// <summary>
    /// Relato aberto sem execução em andamento coerente com ele (item concluído/falho, máquina fora de running ou vínculo
    /// máquina/item divergente): nada mudou e o relato continua reported, como histórico.
    /// </summary>
    InvalidState,
}

/// <summary>Desfecho da confirmação; MachineId/BookingId/ItemId identificam a máquina e o item do incidente.</summary>
public sealed record ConfirmIncidentResult(ConfirmIncidentOutcome Outcome, Guid? MachineId = null, Guid? BookingId = null, Guid? ItemId = null);

/// <summary>
/// Segundo adaptador do caso de falha confirmada: confirma um relato reported da execução corrente usando
/// <see cref="ConfirmMachineFailure.ConfirmMachineFailure.FailAsync"/>, sem outro cálculo de estorno. Numa transação, na
/// ordem comum de locks wallet → máquinas do tipo → itens → incidente: só um relato aberto cuja máquina está running
/// com o item in_progress dele falha a execução e passa a confirmed; relato já confirmado devolve o estado atual sem
/// novo refund; relato de execução já concluída ou falha recusa sem reabrir o item. Com refund, o cache de leitura da
/// wallet é invalidado depois do commit.
/// </summary>
public sealed partial class ConfirmIncident(
    ILaundrySessions sessions,
    QueueCoordinator coordinator,
    LaundryMetrics laundryMetrics,
    RefundMetrics refundMetrics,
    TimeProvider time,
    WalletCacheInvalidation walletCache,
    ILogger<ConfirmIncident> logger)
{
    public async Task<ConfirmIncidentResult> ExecuteAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        ConfirmMachineFailure.ConfirmMachineFailureResult failure;
        IReadOnlyList<QueueChanges> changes;
        IncidentLocation incident;
        await using (var session = await sessions.OpenAsync(cancellationToken))
        {
            await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            if (await session.LocateIncidentAsync(incidentId, cancellationToken) is not { } located)
            {
                return new(ConfirmIncidentOutcome.NotFound);
            }

            incident = located;
            var location = await session.LocateItemAsync(incident.ItemId, cancellationToken)
                ?? throw new InvalidOperationException($"Incidente {incidentId} aponta o item inexistente {incident.ItemId}.");
            var machines = await session.LockMachinesAsync(location.MachineType, cancellationToken);
            var items = await session.LockItemsAsync(location.MachineType, incident.ItemId, cancellationToken);
            var now = OfferPolicy.DecisionInstant(time.GetUtcNow());

            // O status só vai de reported para confirmed (check do banco): fora do relato aberto do item, já foi confirmado.
            if (await session.LockOpenIncidentAsync(incident.ItemId, cancellationToken) != incidentId)
            {
                return new(ConfirmIncidentOutcome.AlreadyConfirmed, incident.MachineId, location.BookingId, incident.ItemId);
            }

            var item = items.Single(candidate => candidate.ItemId == incident.ItemId);
            var machine = machines.SingleOrDefault(slot => slot.MachineId == incident.MachineId);
            if (item.Status != BookingItemStatus.InProgress || item.MachineId != incident.MachineId
                || machine is not { State: MachineState.Running } || machine.ActiveItemId != incident.ItemId)
            {
                LogRefused(incidentId, item.Status);
                return new(ConfirmIncidentOutcome.InvalidState);
            }

            failure = await ConfirmMachineFailure.ConfirmMachineFailure.FailAsync(session, machine, items, now, cancellationToken);
            await session.ConfirmIncidentAsync(incidentId, cancellationToken);
            changes = await coordinator.ReconcileAsync(session, [location.MachineType], cancellationToken);
            await session.CommitAsync();
        }

        laundryMetrics.RecordCommitted(changes);
        if (failure.Refund is { } refund)
        {
            await walletCache.AfterCommitAsync();
            refundMetrics.RecordCommitted(refund.Reason);
        }

        LogConfirmed(incidentId, incident.ItemId, failure.Refund?.Amount);
        return new(ConfirmIncidentOutcome.Confirmed, incident.MachineId, failure.BookingId, failure.ItemId);
    }

    [LoggerMessage(EventId = 640, Level = LogLevel.Information, Message = "Incidente {IncidentId} confirmado: item {ItemId} falhou (refund {Amount}).")]
    private partial void LogConfirmed(Guid incidentId, Guid itemId, decimal? amount);

    [LoggerMessage(EventId = 641, Level = LogLevel.Information, Message = "Confirmação do incidente {IncidentId} recusada com o item em {Status}.")]
    private partial void LogRefused(Guid incidentId, BookingItemStatus status);
}
