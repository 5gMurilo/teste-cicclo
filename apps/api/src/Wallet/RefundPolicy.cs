using Cicclo.Api.Bookings;

namespace Cicclo.Api.Wallet;

/// <summary>Motivo de um refund; vira a dimensão reason de cicclo_wallet_refunds_total.</summary>
public enum RefundReason
{
    /// <summary>Cancelamento voluntário antes de a execução iniciar.</summary>
    Cancelled,

    /// <summary>Falha confirmada da máquina durante a execução (breakdown ou incidente confirmado pela simulação).</summary>
    MachineFailure,
}

/// <summary>Refund a lançar: valor positivo igual ao preço histórico do item e o motivo.</summary>
public sealed record RefundPlan(RefundReason Reason, decimal Amount);

/// <summary>
/// Regra pura de refund: só itens ainda não iniciados (pending_activation, queued, to_be_confirmed) podem ser cancelados,
/// só a execução em andamento (in_progress) pode falhar com refund, e o valor devolvido é sempre o unit_price persistido
/// na compra, nunca o preço atual do catálogo.
/// </summary>
public static class RefundPolicy
{
    public static bool IsCancellable(BookingItemStatus status) =>
        status is BookingItemStatus.PendingActivation or BookingItemStatus.Queued or BookingItemStatus.ToBeConfirmed;

    /// <summary>Plano do refund de cancelamento; recusa estado fora da regra e preço histórico não positivo.</summary>
    public static RefundPlan PlanCancellation(BookingItemStatus status, decimal historicalUnitPrice)
    {
        if (!IsCancellable(status))
        {
            throw new InvalidOperationException($"Item em {status} não pode ser cancelado com refund.");
        }

        return Plan(RefundReason.Cancelled, historicalUnitPrice);
    }

    /// <summary>Plano do refund de falha confirmada; só a execução em andamento falha com refund.</summary>
    public static RefundPlan PlanMachineFailure(BookingItemStatus status, decimal historicalUnitPrice) =>
        status == BookingItemStatus.InProgress
            ? Plan(RefundReason.MachineFailure, historicalUnitPrice)
            : throw new InvalidOperationException($"Item em {status} não está em execução para falhar com refund.");

    private static RefundPlan Plan(RefundReason reason, decimal historicalUnitPrice) =>
        historicalUnitPrice > 0
            ? new(reason, historicalUnitPrice)
            : throw new ArgumentOutOfRangeException(nameof(historicalUnitPrice), historicalUnitPrice, "Preço histórico precisa ser positivo.");
}
