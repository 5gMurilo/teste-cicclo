using Cicclo.Api.Bookings;
using Cicclo.Api.Wallet;

namespace Cicclo.Api.Laundry.Simulation;

public enum OccupyMachineOutcome
{
    /// <summary>A máquina livre, sem ninguém na fila do tipo, passou a running sintético.</summary>
    Occupied,

    NotFound,

    /// <summary>Máquina não livre (reserved, running, out_of_service) ou com fila anterior no tipo: nada mudou.</summary>
    InvalidState,
}

/// <summary>
/// Ocupação sintética da simulação numa transação, na ordem comum de locks: wallet (mutex das escritas, a mesma
/// serialização da ativação) → máquinas do tipo → itens do tipo. Sob os locks, só uma máquina available sem item e sem
/// queued no tipo vira running com is_demo_occupied; nenhuma compra, débito ou wallet é criada.
/// </summary>
public sealed partial class OccupyMachine(ILaundrySessions sessions, TimeProvider time, ILogger<OccupyMachine> logger)
{
    public async Task<OccupyMachineOutcome> ExecuteAsync(Guid machineId, CancellationToken cancellationToken)
    {
        await using var session = await sessions.OpenAsync(cancellationToken);
        await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
        if (await session.LocateMachineAsync(machineId, cancellationToken) is not { } type)
        {
            return OccupyMachineOutcome.NotFound;
        }

        var machine = (await session.LockMachinesAsync(type, cancellationToken)).Single(slot => slot.MachineId == machineId);
        var items = await session.LockItemsAsync(type, null, cancellationToken);
        var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
        var queued = items.Count(item => item.Status == BookingItemStatus.Queued);
        if (machine.State != MachineState.Available || machine.ActiveItemId is not null || queued > 0)
        {
            LogRefused(machineId, machine.State, queued);
            return OccupyMachineOutcome.InvalidState;
        }

        await session.OccupyAsync(machineId, now, cancellationToken);
        await session.CommitAsync();
        LogOccupied(machineId);
        return OccupyMachineOutcome.Occupied;
    }

    [LoggerMessage(EventId = 600, Level = LogLevel.Information, Message = "Máquina {MachineId} ocupada pela simulação, sem item nem cobrança.")]
    private partial void LogOccupied(Guid machineId);

    [LoggerMessage(EventId = 601, Level = LogLevel.Information, Message = "Ocupação da máquina {MachineId} recusada no estado {State} com {Queued} itens na fila do tipo.")]
    private partial void LogRefused(Guid machineId, MachineState state, int queued);
}
