using Cicclo.Api.Bookings;
using Cicclo.Api.Wallet;

namespace Cicclo.Api.Laundry.Simulation;

public enum CompleteMachineOutcome
{
    /// <summary>A execução (real ou sintética) terminou, a máquina foi liberada e a fila do tipo promovida no mesmo commit.</summary>
    Completed,

    NotFound,

    /// <summary>Máquina fora de running (available, reserved, out_of_service, inclusive conclusão repetida): nada mudou.</summary>
    InvalidState,
}

/// <summary>Desfecho da conclusão; BookingId/ItemId identificam o item real concluído (null na ocupação sintética).</summary>
public sealed record CompleteMachineResult(CompleteMachineOutcome Outcome, Guid? BookingId = null, Guid? ItemId = null);

/// <summary>
/// Conclui a execução de uma máquina numa transação, na ordem comum de locks: wallet (mutex das escritas) → máquinas do
/// tipo → itens do tipo e o item em execução. Só running conclui: execução real → item completed e máquina available;
/// ocupação sintética → máquina available. Em seguida a fila do tipo é reconciliada (próximo por ticket recebe a oferta)
/// antes do commit. Nenhum débito, crédito ou lançamento. Métricas só depois do commit.
/// </summary>
public sealed partial class CompleteMachine(
    ILaundrySessions sessions, QueueCoordinator coordinator, LaundryMetrics metrics, TimeProvider time, ILogger<CompleteMachine> logger)
{
    public async Task<CompleteMachineResult> ExecuteAsync(Guid machineId, CancellationToken cancellationToken)
    {
        Guid? bookingId = null;
        Guid? itemId;
        IReadOnlyList<QueueChanges> changes;
        await using (var session = await sessions.OpenAsync(cancellationToken))
        {
            await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            if (await session.LocateMachineAsync(machineId, cancellationToken) is not { } type)
            {
                return new(CompleteMachineOutcome.NotFound);
            }

            var machine = (await session.LockMachinesAsync(type, cancellationToken)).Single(slot => slot.MachineId == machineId);
            itemId = machine.ActiveItemId;
            var items = await session.LockItemsAsync(type, itemId, cancellationToken);
            var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
            if (machine.State != MachineState.Running)
            {
                LogRefused(machineId, machine.State);
                return new(CompleteMachineOutcome.InvalidState);
            }

            if (itemId is { } running)
            {
                var item = items.Single(candidate => candidate.ItemId == running);
                if (item.Status != BookingItemStatus.InProgress || item.MachineId != machineId)
                {
                    throw new InvalidOperationException($"Máquina {machineId} em execução aponta o item {running} fora de in_progress nela.");
                }

                bookingId = (await session.LocateItemAsync(running, cancellationToken))!.BookingId;
            }

            await session.CompleteRunAsync(machineId, itemId, now, cancellationToken);
            changes = await coordinator.ReconcileAsync(session, [type], cancellationToken);
            await session.CommitAsync();
        }

        metrics.RecordCommitted(changes);
        LogCompleted(machineId, itemId);
        return new(CompleteMachineOutcome.Completed, bookingId, itemId);
    }

    [LoggerMessage(EventId = 610, Level = LogLevel.Information, Message = "Execução da máquina {MachineId} concluída (item {ItemId}), sem efeito financeiro.")]
    private partial void LogCompleted(Guid machineId, Guid? itemId);

    [LoggerMessage(EventId = 611, Level = LogLevel.Information, Message = "Conclusão da máquina {MachineId} recusada no estado {State}.")]
    private partial void LogRefused(Guid machineId, MachineState state);
}
