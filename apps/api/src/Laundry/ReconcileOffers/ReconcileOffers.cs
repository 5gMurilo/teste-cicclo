using Cicclo.Api.Wallet;

namespace Cicclo.Api.Laundry.ReconcileOffers;

/// <summary>
/// Reconciliação persistida das ofertas de todos os tipos numa transação: wallet → máquinas (tipo, id) → itens,
/// ofertas vencidas devolvidas ao fim da fila e máquinas livres ofertadas aos próximos. Métricas só depois do commit.
/// O mesmo algoritmo do coordenador serve a ativação e a confirmação vencida; o worker apenas chama este caso de uso.
/// </summary>
public sealed partial class ReconcileOffers(ILaundrySessions sessions, QueueCoordinator coordinator, LaundryMetrics metrics, ILogger<ReconcileOffers> logger)
{
    private static readonly MachineType[] AllTypes = Enum.GetValues<MachineType>();

    public async Task<IReadOnlyList<QueueChanges>> ExecuteAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QueueChanges> changes;
        await using (var session = await sessions.OpenAsync(cancellationToken))
        {
            await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            changes = await coordinator.ReconcileAsync(session, AllTypes, cancellationToken);
            await session.CommitAsync();
        }

        metrics.RecordCommitted(changes);
        foreach (var change in changes.Where(change => change.Expired.Count > 0 || change.Offers.Count > 0))
        {
            LogReconciled(change.Type, change.Expired.Count, change.Offers.Count);
        }

        return changes;
    }

    [LoggerMessage(EventId = 310, Level = LogLevel.Information, Message = "Fila {MachineType} reconciliada: {Expired} ofertas vencidas devolvidas, {Offers} ofertas novas.")]
    private partial void LogReconciled(MachineType machineType, int expired, int offers);
}
