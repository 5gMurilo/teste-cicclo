using System.ComponentModel;
using Cicclo.Api.Platform.Http;

namespace Cicclo.Api.Bookings.GetBooking;

/// <summary>GET /api/v1/bookings/{bookingId}: projeção autoritativa de um booking da wallet demo, sem efeito colateral.</summary>
public static class GetBookingEndpoint
{
    public static IEndpointRouteBuilder MapGetBookingEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapGet(
                "/bookings/{bookingId}",
                async Task<IResult> (
                    [Description("Identificador (UUID) do booking da wallet demo.")] Guid bookingId,
                    PostgresBookingProjection projection,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                    await projection.FindAsync(bookingId, cancellationToken) is { } booking
                        ? TypedResults.Ok(BookingResponse.From(booking))
                        : BookingNotFound(context))
            .WithName("GetBooking")
            .WithTags("Bookings")
            .WithSummary("Estado autoritativo de um booking.")
            .WithDescription(
                "Lê do PostgreSQL, num único snapshot, a cobrança e os preços históricos, o saldo atual da wallet demo e o estado atual de cada execução. "
                + "Não expira ofertas nem promove a fila: uma oferta vencida aparece com expiresAt no passado até a reconciliação. "
                + "Somente bookings da wallet demo, resolvida no servidor; parâmetros como walletId ou userId são ignorados.")
            .Produces<BookingResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                "Limite da política de leituras de contratações excedido (janela fixa por IP).",
                (StatusCodes.Status400BadRequest, "bookingId não é um UUID (VALIDATION_ERROR)."),
                (StatusCodes.Status404NotFound, "Booking inexistente na wallet demo (BOOKING_NOT_FOUND)."))
            .WithResponseExample(StatusCodes.Status200OK, Example);
        return api;
    }

    internal static IResult BookingNotFound(HttpContext context) =>
        ApiProblems.NotFound(context, ApiProblems.BookingNotFound, "Booking não encontrado", "Não existe booking com este identificador na wallet demo.");

    private static readonly BookingResponse Example = new(
        Guid.Parse("fb0cb8f8-ced3-4ad7-9ee5-e7df973abc19"),
        MoneyCents.Currency,
        3980,
        1020,
        new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc),
        [
            new(Guid.Parse("3fc7b7f5-4b2a-4d35-bd16-1dfbcab7d98f"), "dry", 2090, BookingItemStatusContract.PendingActivation, null, null, null),
            new(
                Guid.Parse("ee7cefb9-9150-464f-bc77-90c795d6947d"),
                "wash",
                1890,
                BookingItemStatusContract.ToBeConfirmed,
                Guid.Parse("00000000-0000-4000-8000-000000000101"),
                null,
                new DateTime(2026, 10, 21, 12, 1, 0, DateTimeKind.Utc)),
        ]);
}
