using System.Diagnostics.Metrics;

namespace Cicclo.Api.Bookings;

/// <summary>
/// Métrica de compra (OPS-05): cicclo_bookings_total por outcome accepted/rejected, sem ids como dimensão. Conta
/// decisões do comando de compra: replay válido conta como accepted, porque a chamada foi aceita; o número contábil de
/// bookings e débitos únicos vem do ledger, não deste contador.
/// </summary>
public sealed class BookingMetrics
{
    public const string MeterName = "Cicclo.Api.Bookings";

    private readonly Counter<long> bookings;

    public BookingMetrics(IMeterFactory meters) =>
        bookings = meters.Create(MeterName).CreateCounter<long>("cicclo_bookings_total", description: "Decisões do comando de compra por outcome (accepted/rejected).");

    public void Accepted() => bookings.Add(1, new KeyValuePair<string, object?>("outcome", "accepted"));

    public void Rejected() => bookings.Add(1, new KeyValuePair<string, object?>("outcome", "rejected"));
}
