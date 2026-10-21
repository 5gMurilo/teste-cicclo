using System.Diagnostics.Metrics;

namespace Cicclo.Api.Laundry;

/// <summary>
/// Métricas de fila (OPS-11): espera real entre queued_at e a oferta e ofertas expiradas, por machine_type. Só são
/// registradas depois do commit que tornou as mudanças fatos; nenhum id entra como dimensão.
/// </summary>
public sealed class LaundryMetrics
{
    public const string MeterName = "Cicclo.Api.Laundry";

    private readonly Histogram<double> queueWait;
    private readonly Counter<long> offersExpired;

    public LaundryMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        queueWait = meter.CreateHistogram<double>("cicclo_queue_wait_seconds", "s", "Espera na fila entre queued_at e a oferta da máquina.");
        offersExpired = meter.CreateCounter<long>("cicclo_offers_expired_total", description: "Ofertas de máquina vencidas e devolvidas ao fim da fila.");
    }

    /// <summary>Registra mudanças já confirmadas pelo commit.</summary>
    public void RecordCommitted(IEnumerable<QueueChanges> changes)
    {
        foreach (var change in changes)
        {
            var type = new KeyValuePair<string, object?>("machine_type", change.Type.ToString().ToLowerInvariant());
            foreach (var offer in change.Offers)
            {
                queueWait.Record((offer.OfferedAt - offer.QueuedAt).TotalSeconds, type);
            }

            if (change.Expired.Count > 0)
            {
                offersExpired.Add(change.Expired.Count, type);
            }
        }
    }
}
