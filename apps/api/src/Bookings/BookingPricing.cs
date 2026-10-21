using Cicclo.Api.Platform.Http;
using Cicclo.Api.Services;

namespace Cicclo.Api.Bookings;

/// <summary>Estado de um serviço lido sob lock no momento da compra.</summary>
public sealed record CatalogEntry(string Code, decimal CurrentPrice, bool IsActive, bool HasOperationalMachine);

/// <summary>Uma execução precificada, na ordem canônica da intenção.</summary>
public sealed record PricedItem(string ServiceCode, decimal UnitPrice);

public sealed record BookingPrice(IReadOnlyList<PricedItem> Items, decimal Total);

/// <summary>Preço autoritativo ou recusa; em PRICE_CHANGED informa o total calculado em centavos.</summary>
public sealed record PricingResult(BookingPrice? Price, BookingRejection? Rejection, long? CalculatedTotalCents);

/// <summary>
/// Regra pura de preço: valida o lote inteiro contra o catálogo (inexistente, depois inativo, depois sem capacidade),
/// soma os preços atuais em decimal e só aceita quando o total coincide exatamente com o consentimento do cliente.
/// Preço com mais de duas casas ou total fora de numeric(12,2) é falha de invariante, nunca arredondado.
/// </summary>
public static class BookingPricing
{
    public static PricingResult Price(BookingIntent intent, IReadOnlyCollection<CatalogEntry> catalog)
    {
        var entries = catalog.ToDictionary(entry => entry.Code, StringComparer.Ordinal);
        if (intent.ServiceCodes.Any(code => !entries.ContainsKey(code)))
        {
            return new(null, BookingRejection.ServiceNotFound, null);
        }

        var availability = intent.ServiceCodes
            .Distinct(StringComparer.Ordinal)
            .Select(code => ServiceAvailability.Evaluate(entries[code].IsActive, entries[code].HasOperationalMachine))
            .ToList();
        if (availability.Any(item => item.Reason == UnavailabilityReason.ServiceDisabled))
        {
            return new(null, BookingRejection.ServiceDisabled, null);
        }

        if (availability.Any(item => item.Reason == UnavailabilityReason.NoOperationalMachine))
        {
            return new(null, BookingRejection.NoOperationalMachine, null);
        }

        var items = intent.ServiceCodes.Select(code => new PricedItem(code, entries[code].CurrentPrice)).ToList();
        foreach (var item in items)
        {
            _ = MoneyCents.FromDecimal(item.UnitPrice); // recusa precisão inválida sem arredondar
        }

        var total = items.Sum(item => item.UnitPrice);
        var totalCents = MoneyCents.FromDecimal(total);
        return totalCents == intent.ExpectedTotalCents
            ? new(new BookingPrice(items, total), null, null)
            : new(null, BookingRejection.PriceChanged, totalCents);
    }
}
