using Cicclo.Api.Wallet;

namespace Cicclo.Api.Laundry.RestoreMachine;

public enum RestoreMachineOutcome
{
    /// <summary>A máquina voltou a available e a fila do tipo foi promovida no mesmo commit.</summary>
    Restored,

    NotFound,

    /// <summary>Máquina fora de out_of_service (available, reserved, running, inclusive restauração repetida): nada mudou.</summary>
    InvalidState,
}

/// <summary>Desfecho da restauração; BookingId/ItemId identificam o item que recebeu a oferta desta máquina (null sem fila).</summary>
public sealed record RestoreMachineResult(RestoreMachineOutcome Outcome, Guid? BookingId = null, Guid? ItemId = null);

/// <summary>
/// Restaura uma máquina fora de serviço numa transação, na ordem comum de locks: wallet (mutex das escritas) → máquinas do
/// tipo → itens do tipo. Só out_of_service restaura: a máquina volta a available e a fila do tipo é reconciliada antes do
/// commit (o próximo por ticket recebe a oferta). Só itens queued/to_be_confirmed participam da fila: failed/cancelled não
/// são reativados. Nenhum débito, crédito ou lançamento. Métricas só depois do commit.
/// </summary>
public sealed partial class RestoreMachine(
    ILaundrySessions sessions, QueueCoordinator coordinator, LaundryMetrics metrics, TimeProvider time, ILogger<RestoreMachine> logger)
{
    public async Task<RestoreMachineResult> ExecuteAsync(Guid machineId, CancellationToken cancellationToken)
    {
        Guid? bookingId = null;
        Guid? itemId;
        IReadOnlyList<QueueChanges> changes;
        await using (var session = await sessions.OpenAsync(cancellationToken))
        {
            await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            if (await session.LocateMachineAsync(machineId, cancellationToken) is not { } type)
            {
                return new(RestoreMachineOutcome.NotFound);
            }

            var machine = (await session.LockMachinesAsync(type, cancellationToken)).Single(slot => slot.MachineId == machineId);
            await session.LockItemsAsync(type, null, cancellationToken);
            var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
            if (machine.State != MachineState.OutOfService)
            {
                LogRefused(machineId, machine.State);
                return new(RestoreMachineOutcome.InvalidState);
            }

            await session.RestoreAsync(machineId, now, cancellationToken);
            changes = await coordinator.ReconcileAsync(session, [type], cancellationToken);
            itemId = changes.SelectMany(change => change.Offers).SingleOrDefault(offer => offer.MachineId == machineId)?.ItemId;
            if (itemId is { } offered)
            {
                bookingId = (await session.LocateItemAsync(offered, cancellationToken))!.BookingId;
            }

            await session.CommitAsync();
        }

        metrics.RecordCommitted(changes);
        LogRestored(machineId, itemId);
        return new(RestoreMachineOutcome.Restored, bookingId, itemId);
    }

    [LoggerMessage(EventId = 650, Level = LogLevel.Information, Message = "Máquina {MachineId} restaurada (oferta ao item {ItemId}), sem efeito financeiro.")]
    private partial void LogRestored(Guid machineId, Guid? itemId);

    [LoggerMessage(EventId = 651, Level = LogLevel.Information, Message = "Restauração da máquina {MachineId} recusada no estado {State}.")]
    private partial void LogRefused(Guid machineId, MachineState state);
}
