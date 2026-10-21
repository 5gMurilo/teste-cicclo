using Cicclo.Api.Laundry;
using Cicclo.Api.Wallet;
using Cicclo.Api.Wallet.ReadCaching;

namespace Cicclo.Api.Bookings.CancelItem;

public enum CancelItemOutcome
{
    /// <summary>Item não iniciado cancelado agora, com refund do preço histórico e liberação/promoção no mesmo commit.</summary>
    Cancelled,

    /// <summary>O item já estava cancelado: nada mudou e o refund histórico é devolvido como estava (repetição).</summary>
    AlreadyCancelled,

    /// <summary>Item iniciado ou com desfecho (in_progress, completed, failed): nada mudou.</summary>
    InvalidState,

    /// <summary>Item inexistente ou de outro booking.</summary>
    NotFound,
}

/// <summary>Resultado do cancelamento; RefundedAmount é o valor do refund único do item (null fora de cancelado).</summary>
public sealed record CancelItemResult(CancelItemOutcome Outcome, decimal? RefundedAmount = null);

/// <summary>
/// Cancela uma execução ainda não iniciada numa única transação, na ordem comum de locks: wallet (mutex das escritas)
/// → máquinas do tipo → itens. Sob os locks, valida o estado; marca cancelled, libera a máquina reservada, credita o
/// preço histórico com um lançamento refund e reconcilia a fila do tipo (próximo elegível recebe a oferta) antes do
/// commit. Item já cancelado é repetição sem crédito novo. Métricas e invalidação do cache de leitura da wallet só
/// depois do commit, com a sessão já descartada.
/// </summary>
public sealed partial class CancelItem(
    ILaundrySessions sessions,
    QueueCoordinator coordinator,
    LaundryMetrics laundryMetrics,
    RefundMetrics refundMetrics,
    TimeProvider time,
    WalletCacheInvalidation walletCache,
    ILogger<CancelItem> logger)
{
    public async Task<CancelItemResult> ExecuteAsync(Guid bookingId, Guid itemId, CancellationToken cancellationToken)
    {
        RefundPlan plan;
        IReadOnlyList<QueueChanges> changes;
        await using (var session = await sessions.OpenAsync(cancellationToken))
        {
            await session.LockWalletAsync(DemoWallet.Id, cancellationToken);
            if (await session.LocateItemAsync(itemId, cancellationToken) is not { } location || location.BookingId != bookingId)
            {
                return new(CancelItemOutcome.NotFound);
            }

            await session.LockMachinesAsync(location.MachineType, cancellationToken);
            var items = await session.LockItemsAsync(location.MachineType, itemId, cancellationToken);
            var now = OfferPolicy.DecisionInstant(time.GetUtcNow());
            var item = items.Single(candidate => candidate.ItemId == itemId);
            var refundable = await session.ReadRefundableAsync(itemId, cancellationToken);
            if (item.Status == BookingItemStatus.Cancelled)
            {
                return new(
                    CancelItemOutcome.AlreadyCancelled,
                    refundable.RefundedAmount ?? throw new InvalidOperationException($"Item {itemId} cancelado sem refund registrado."));
            }

            if (!RefundPolicy.IsCancellable(item.Status))
            {
                LogRefused(itemId, item.Status);
                return new(CancelItemOutcome.InvalidState);
            }

            plan = RefundPolicy.PlanCancellation(item.Status, refundable.UnitPrice);
            await session.CancelWithRefundAsync(DemoWallet.Id, bookingId, itemId, item.Status, item.MachineId, plan.Amount, now, cancellationToken);
            changes = await coordinator.ReconcileAsync(session, [location.MachineType], cancellationToken);
            await session.CommitAsync();
        }

        await walletCache.AfterCommitAsync();
        laundryMetrics.RecordCommitted(changes);
        refundMetrics.RecordCommitted(plan.Reason);
        LogCancelled(itemId, plan.Amount);
        return new(CancelItemOutcome.Cancelled, plan.Amount);
    }

    [LoggerMessage(EventId = 500, Level = LogLevel.Information, Message = "Item {ItemId} cancelado com refund de {Amount}.")]
    private partial void LogCancelled(Guid itemId, decimal amount);

    [LoggerMessage(EventId = 501, Level = LogLevel.Information, Message = "Cancelamento do item {ItemId} recusado no estado {Status}.")]
    private partial void LogRefused(Guid itemId, BookingItemStatus status);
}
