using Cicclo.Api.Bookings;
using Cicclo.Api.Wallet;

namespace Cicclo.Api.Laundry.ConfirmMachineFailure;

public enum ConfirmMachineFailureOutcome
{
    /// <summary>A execução (real ou sintética) falhou: máquina out_of_service e, com item real, item failed com refund único.</summary>
    Failed,

    NotFound,

    /// <summary>Máquina fora de running (available, reserved, out_of_service, inclusive falha repetida): nada mudou.</summary>
    InvalidState,
}

/// <summary>
/// Desfecho da falha; BookingId/ItemId identificam o item real que falhou e Refund o crédito lançado para ele (todos
/// null na ocupação sintética).
/// </summary>
public sealed record ConfirmMachineFailureResult(ConfirmMachineFailureOutcome Outcome, Guid? BookingId = null, Guid? ItemId = null, RefundPlan? Refund = null);

/// <summary>
/// Caso de uso único de falha confirmada de máquina (breakdown da simulação; a confirmação de incidente reutiliza
/// <see cref="FailAsync"/>). Numa transação, na ordem comum de locks wallet → máquinas do tipo → itens: só running
/// falha. Execução real → item failed, máquina out_of_service e refund do preço histórico com crédito na mesma
/// transação; ocupação sintética → out_of_service sem item, refund ou lançamento. A fila do tipo é reconciliada antes do
/// commit e as métricas só contam depois dele. Complete e falha disputam os mesmos locks: só um desfecho por execução.
/// </summary>
public sealed partial class ConfirmMachineFailure(
    ILaundrySessions sessions, QueueCoordinator coordinator, LaundryMetrics laundryMetrics, RefundMetrics refundMetrics, TimeProvider time, ILogger<ConfirmMachineFailure> logger)
{
    public async Task<ConfirmMachineFailureResult> ExecuteAsync(Guid machineId, CancellationToken cancellationToken)
    {
        ConfirmMachineFailureResult result;
        IReadOnlyList<QueueChanges> changes;
        await using (var session = await sessions.OpenAsync(cancellationToken))
        {
            await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            if (await session.LocateMachineAsync(machineId, cancellationToken) is not { } type)
            {
                return new(ConfirmMachineFailureOutcome.NotFound);
            }

            var machine = (await session.LockMachinesAsync(type, cancellationToken)).Single(slot => slot.MachineId == machineId);
            var items = await session.LockItemsAsync(type, machine.ActiveItemId, cancellationToken);
            var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
            if (machine.State != MachineState.Running)
            {
                LogRefused(machineId, machine.State);
                return new(ConfirmMachineFailureOutcome.InvalidState);
            }

            result = await FailAsync(session, machine, items, now, cancellationToken);
            changes = await coordinator.ReconcileAsync(session, [type], cancellationToken);
            await session.CommitAsync();
        }

        laundryMetrics.RecordCommitted(changes);
        if (result.Refund is { } refund)
        {
            refundMetrics.RecordCommitted(refund.Reason);
        }

        LogFailed(machineId, result.ItemId, result.Refund?.Amount);
        return result;
    }

    /// <summary>
    /// Transição de falha de uma máquina running já bloqueada (com seu item, se houver), sem commit: a mesma para o
    /// breakdown e para a confirmação de incidente. O refund usa o preço histórico do item e o crédito entra junto.
    /// </summary>
    public static async Task<ConfirmMachineFailureResult> FailAsync(
        ILaundrySession session, MachineSlot machine, IReadOnlyList<LaundryItem> items, DateTime now, CancellationToken cancellationToken)
    {
        if (machine.State != MachineState.Running)
        {
            throw new InvalidOperationException($"Máquina {machine.MachineId} em {machine.State} não tem execução para falhar.");
        }

        if (machine.ActiveItemId is not { } running)
        {
            await session.FailRunAsync(machine.MachineId, null, now, cancellationToken);
            return new(ConfirmMachineFailureOutcome.Failed);
        }

        var item = items.Single(candidate => candidate.ItemId == running);
        if (item.Status != BookingItemStatus.InProgress || item.MachineId != machine.MachineId)
        {
            throw new InvalidOperationException($"Máquina {machine.MachineId} em execução aponta o item {running} fora de in_progress nela.");
        }

        var bookingId = (await session.LocateItemAsync(running, cancellationToken))!.BookingId;
        var refundable = await session.ReadRefundableAsync(running, cancellationToken);
        var plan = RefundPolicy.PlanMachineFailure(item.Status, refundable.UnitPrice);
        await session.FailRunAsync(machine.MachineId, running, now, cancellationToken);
        await session.CreditRefundAsync(DemoWallet.Id, bookingId, running, plan.Amount, now, cancellationToken);
        return new(ConfirmMachineFailureOutcome.Failed, bookingId, running, plan);
    }

    [LoggerMessage(EventId = 630, Level = LogLevel.Information, Message = "Falha da máquina {MachineId} confirmada (item {ItemId}, refund {Amount}).")]
    private partial void LogFailed(Guid machineId, Guid? itemId, decimal? amount);

    [LoggerMessage(EventId = 631, Level = LogLevel.Information, Message = "Falha da máquina {MachineId} recusada no estado {State}.")]
    private partial void LogRefused(Guid machineId, MachineState state);
}
