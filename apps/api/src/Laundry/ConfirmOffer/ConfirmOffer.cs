using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry.Notifications;
using Cicclo.Api.Wallet;

namespace Cicclo.Api.Laundry.ConfirmOffer;

public enum ConfirmOfferOutcome
{
    /// <summary>Oferta vigente confirmada: item in_progress e máquina running, sem lançamento financeiro.</summary>
    Started,

    /// <summary>O item já estava em execução: nada mudou (repetição).</summary>
    AlreadyStarted,

    /// <summary>now &gt;= expiresAt no instante da decisão: a oferta venceu e a reconciliação (reentrada no fim da fila) foi confirmada.</summary>
    Expired,

    /// <summary>Estado ou vínculo item↔máquina fora da regra: nada mudou.</summary>
    InvalidState,

    /// <summary>Item inexistente ou de outro booking.</summary>
    NotFound,
}

public sealed record ConfirmOfferResult(ConfirmOfferOutcome Outcome, ItemPlacement? Item = null);

/// <summary>
/// Confirma a oferta corrente de um item: wallet → máquinas do tipo → itens sob lock, instante capturado depois dos
/// locks e prazo estrito now &lt; expiresAt. A confirmação válida grava início do item e máquina running no mesmo commit.
/// Oferta vencida não é exceção: a mesma transação reconcilia o tipo (reentrada no fim da fila e próxima oferta) e
/// confirma, e só então devolve Expired. As demais recusas não mudam nada. Avisos realtime (opcionais) só depois do commit,
/// que já liberou os locks.
/// </summary>
public sealed partial class ConfirmOffer(
    ILaundrySessions sessions,
    QueueCoordinator coordinator,
    LaundryMetrics metrics,
    TimeProvider time,
    ILogger<ConfirmOffer> logger,
    LaundryChangeNotifier? notifier = null)
{
    public async Task<ConfirmOfferResult> ExecuteAsync(Guid bookingId, Guid itemId, CancellationToken cancellationToken)
    {
        await using var session = await sessions.OpenAsync(cancellationToken);
        await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
        if (await session.LocateItemAsync(itemId, cancellationToken) is not { } location || location.BookingId != bookingId)
        {
            return new(ConfirmOfferOutcome.NotFound);
        }

        var machines = await session.LockMachinesAsync(location.MachineType, cancellationToken);
        var items = await session.LockItemsAsync(location.MachineType, itemId, cancellationToken);
        var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
        var item = items.Single(candidate => candidate.ItemId == itemId);
        var machine = machines.SingleOrDefault(candidate => candidate.MachineId == item.MachineId);
        var check = OfferPolicy.CheckConfirmation(item, machine, now);
        switch (check)
        {
            case OfferCheck.AlreadyStarted:
                return new(ConfirmOfferOutcome.AlreadyStarted, Placement(item));
            case OfferCheck.Invalid:
                LogRefused(itemId, item.Status);
                return new(ConfirmOfferOutcome.InvalidState, Placement(item));
            case OfferCheck.Expired:
                var changes = await coordinator.ReconcileAsync(session, [location.MachineType], cancellationToken);
                var current = await session.LockItemsAsync(location.MachineType, itemId, cancellationToken);
                await session.CommitAsync();
                metrics.RecordCommitted(changes);
                await NotifyAsync(new() { Bookings = session.CommittedRevisions, Queues = changes });
                LogExpired(itemId);
                return new(ConfirmOfferOutcome.Expired, QueueCoordinator.Placement(itemId, current));
        }

        await session.StartAsync(itemId, item.MachineId!.Value, now, cancellationToken);
        await session.CommitAsync();
        await NotifyAsync(new() { Bookings = session.CommittedRevisions });
        LogStarted(itemId, item.MachineId.Value);
        return new(ConfirmOfferOutcome.Started, new ItemPlacement(itemId, BookingItemStatus.InProgress, item.MachineId, item.ExpiresAt, null));
    }

    private Task NotifyAsync(CommittedLaundryChanges changes) => notifier?.NotifyAsync(changes) ?? Task.CompletedTask;

    private static ItemPlacement Placement(LaundryItem item) => new(item.ItemId, item.Status, item.MachineId, item.ExpiresAt, null);

    [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Oferta do item {ItemId} confirmada na máquina {MachineId}.")]
    private partial void LogStarted(Guid itemId, Guid machineId);

    [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "Confirmação do item {ItemId} recusada: oferta vencida, devolvida ao fim da fila.")]
    private partial void LogExpired(Guid itemId);

    [LoggerMessage(EventId = 302, Level = LogLevel.Information, Message = "Confirmação do item {ItemId} recusada no estado {Status}.")]
    private partial void LogRefused(Guid itemId, BookingItemStatus status);
}
