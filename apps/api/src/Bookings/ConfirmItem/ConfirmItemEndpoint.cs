using System.ComponentModel;
using System.Globalization;
using Cicclo.Api.Laundry;
using Cicclo.Api.Laundry.ConfirmOffer;
using Cicclo.Api.Platform.Http;

namespace Cicclo.Api.Bookings.ConfirmItem;

/// <summary>
/// POST /api/v1/bookings/{bookingId}/items/{itemId}/confirm: confirma a oferta corrente do item com prazo estrito
/// (now &lt; expiresAt) e devolve o item atual; oferta vencida responde 410 só depois do commit da reconciliação.
/// </summary>
public static class ConfirmItemEndpoint
{
    public static IEndpointRouteBuilder MapConfirmItemEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapPost(
                "/bookings/{bookingId}/items/{itemId}/confirm",
                async Task<IResult> (
                    [Description("Identificador (UUID) do booking da wallet demo.")] Guid bookingId,
                    [Description("Identificador (UUID) do item, que precisa pertencer ao booking.")] Guid itemId,
                    ConfirmOffer useCase,
                    PostgresBookingProjection projection,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    if (await projection.FindAsync(bookingId, cancellationToken) is null)
                    {
                        return GetBooking.GetBookingEndpoint.BookingNotFound(context);
                    }

                    var result = await useCase.ExecuteAsync(bookingId, itemId, cancellationToken);
                    switch (result.Outcome)
                    {
                        case ConfirmOfferOutcome.NotFound:
                            return ActivateItem.ActivateItemEndpoint.ItemNotFound(context);
                        case ConfirmOfferOutcome.InvalidState:
                            return ApiProblems.Create(
                                context,
                                StatusCodes.Status409Conflict,
                                ApiProblems.InvalidStateTransition,
                                "Transição de estado inválida",
                                "O item não tem uma oferta de máquina aguardando confirmação; nada foi alterado.");
                        case ConfirmOfferOutcome.Expired:
                            return ApiProblems.Create(
                                context,
                                StatusCodes.Status410Gone,
                                ApiProblems.MachineOfferExpired,
                                "Oferta de máquina vencida",
                                Expired(result.Item!));
                    }

                    var booking = await projection.FindAsync(bookingId, cancellationToken)
                        ?? throw new InvalidOperationException("Booking ausente da projeção depois da confirmação.");
                    return TypedResults.Ok(BookingItemResponse.From(booking.Items.Single(item => item.ItemId == itemId)));
                })
            .WithName("ConfirmBookingItem")
            .WithTags("Bookings")
            .WithSummary("Confirmar a oferta de máquina de uma execução.")
            .WithDescription(
                "Confirma a oferta corrente do item (to_be_confirmed) se o instante do servidor, lido depois dos locks, for anterior a expiresAt: "
                + "o item passa a in_progress e a máquina a running, sem nova cobrança. Exatamente em expiresAt ou depois, a mesma transação devolve "
                + "a oferta vencida ao fim da fila (ticket novo), oferta a máquina ao próximo da fila e confirma essa reconciliação; só então responde "
                + "410 MACHINE_OFFER_EXPIRED, com a situação atual do item no detail, mesmo que o worker ainda não tenha rodado. Repetir a confirmação "
                + "de um item in_progress responde 200 com o estado atual, sem reiniciar a execução. Itens sem oferta aguardando confirmação "
                + "respondem 409 INVALID_STATE_TRANSITION sem mudança. Não há token de oferta: a confirmação vale para a oferta corrente do item. "
                + "O corpo da requisição não é lido.")
            .Produces<BookingItemResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                "Limite da política de comandos de item excedido (15/min por IP por padrão, compartilhado entre activate e confirm).",
                (StatusCodes.Status400BadRequest, "bookingId ou itemId não é UUID (VALIDATION_ERROR)."),
                (StatusCodes.Status404NotFound, "BOOKING_NOT_FOUND (booking inexistente na wallet demo) ou ITEM_NOT_FOUND (item inexistente ou de outro booking); nada muda."),
                (StatusCodes.Status409Conflict, "INVALID_STATE_TRANSITION: o item não está em to_be_confirmed com a máquina reservada para ele (inclusive oferta já devolvida à fila pelo worker); nada muda."),
                (StatusCodes.Status410Gone, "MACHINE_OFFER_EXPIRED: a oferta venceu (now >= expiresAt); a reentrada na fila já foi confirmada e o detail informa a posição atual ou a nova oferta."))
            .WithResponseExample(
                StatusCodes.Status200OK,
                new BookingItemResponse(
                    Guid.Parse("ee7cefb9-9150-464f-bc77-90c795d6947d"),
                    "wash",
                    1890,
                    BookingItemStatusContract.InProgress,
                    Guid.Parse("00000000-0000-4000-8000-000000000101"),
                    null,
                    null));
        return api;
    }

    /// <summary>Situação do item depois da reentrada (G2): posição na fila ou a nova oferta recebida na mesma transação.</summary>
    internal static string Expired(ItemPlacement item) => item.Status switch
    {
        BookingItemStatus.Queued => string.Create(
            CultureInfo.InvariantCulture, $"A oferta venceu antes da confirmação. O item voltou ao fim da fila e está na posição {item.QueuePosition}."),
        BookingItemStatus.ToBeConfirmed => string.Create(
            CultureInfo.InvariantCulture,
            $"A oferta venceu antes da confirmação. Sem ninguém à frente, o item recebeu nova oferta da máquina {item.MachineId} até {item.ExpiresAt:yyyy-MM-ddTHH:mm:ss.FFFFFFZ}."),
        _ => throw new InvalidOperationException($"Estado inesperado depois de uma oferta vencida: {item.Status}."),
    };
}
