using System.Diagnostics.Metrics;

namespace Cicclo.Api.Wallet;

/// <summary>
/// Métrica de refund (OPS-11): cicclo_wallet_refunds_total por reason, registrada só depois do commit que lançou o
/// crédito; repetições sem crédito novo não contam. Nenhum id entra como dimensão.
/// </summary>
public sealed class RefundMetrics
{
    public const string MeterName = "Cicclo.Api.Wallet";

    private readonly Counter<long> refunds;

    public RefundMetrics(IMeterFactory meters) =>
        refunds = meters.Create(MeterName).CreateCounter<long>("cicclo_wallet_refunds_total", description: "Refunds creditados na wallet por motivo.");

    public void RecordCommitted(RefundReason reason) =>
        refunds.Add(1, new KeyValuePair<string, object?>("reason", reason switch
        {
            RefundReason.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Motivo de refund sem dimensão."),
        }));
}
