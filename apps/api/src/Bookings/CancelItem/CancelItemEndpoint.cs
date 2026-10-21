using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Cicclo.Api.Platform.Http;

namespace Cicclo.Api.Bookings.CancelItem;

/// <summary>Execução cancelada: item atual, refund histórico do item e saldo atual da wallet demo.</summary>
public sealed record CancelItemResponse(
    [property: Required, Description("Item no estado atual (cancelled).")] BookingItemResponse Item,
    [property: Required, Range(1, 999_999_999_999), Description("Valor devolvido pelo refund único deste item, em centavos inteiros (int64): o preço histórico da compra, não o preço atual do catálogo.")] long RefundedAmountCents,
    [property: Required, Range(0, 999_999_999_999), Description("Saldo atual da wallet demo em centavos inteiros (int64), lido depois do cancelamento; numa repetição pode refletir operações posteriores.")] long RemainingBalanceCents);

/// <summary>
/// POST /api/v1/bookings/{bookingId}/items/{itemId}/cancel: cancela uma execução ainda não iniciada com refund
/// histórico único; repetir sobre item cancelado devolve o mesmo refund sem novo crédito.
/// </summary>
public static class CancelItemEndpoint
{
    public static IEndpointRouteBuilder MapCancelItemEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapPost(
                "/bookings/{bookingId}/items/{itemId}/cancel",
                async Task<IResult> (
                    [Description("Identificador (UUID) do booking da wallet demo.")] Guid bookingId,
                    [Description("Identificador (UUID) do item, que precisa pertencer ao booking.")] Guid itemId,
                    CancelItem useCase,
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
                        case CancelItemOutcome.NotFound:
                            return ActivateItem.ActivateItemEndpoint.ItemNotFound(context);
                        case CancelItemOutcome.InvalidState:
                            return ApiProblems.Create(
                                context,
                                StatusCodes.Status409Conflict,
                                ApiProblems.InvalidStateTransition,
                                "Transição de estado inválida",
                                "O item já iniciou ou terminou; o cancelamento só vale antes do início e nada foi alterado.");
                    }

                    var booking = await projection.FindAsync(bookingId, cancellationToken)
                        ?? throw new InvalidOperationException("Booking ausente da projeção depois do cancelamento.");
                    return TypedResults.Ok(new CancelItemResponse(
                        BookingItemResponse.From(booking.Items.Single(item => item.ItemId == itemId)),
                        MoneyCents.FromDecimal(result.RefundedAmount!.Value),
                        MoneyCents.FromDecimal(booking.RemainingBalance)));
                })
            .WithName("CancelBookingItem")
            .WithTags("Bookings")
            .WithSummary("Cancelar uma execução ainda não iniciada, com refund do preço histórico.")
            .WithDescription(
                "Cancela um item pending_activation, queued ou to_be_confirmed numa única transação: o item passa a cancelled, a wallet demo "
                + "recebe de volta o preço histórico do item (unitPriceCents da compra, nunca o preço atual do catálogo) com um único lançamento "
                + "refund, a máquina reservada para ele (se havia oferta) é liberada e o próximo da fila recebe a oferta. Repetir o cancelamento "
                + "de um item cancelled responde 200 com o mesmo refundedAmountCents e o saldo atual, sem novo crédito. Itens in_progress, "
                + "completed ou failed respondem 409 INVALID_STATE_TRANSITION sem estorno. O corpo da requisição não é lido.")
            .Produces<CancelItemResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                "Limite da política de cancelamento excedido (10/min por IP por padrão).",
                (StatusCodes.Status400BadRequest, "bookingId ou itemId não é UUID (VALIDATION_ERROR)."),
                (StatusCodes.Status404NotFound, "BOOKING_NOT_FOUND (booking inexistente na wallet demo) ou ITEM_NOT_FOUND (item inexistente ou de outro booking); nada muda."),
                (StatusCodes.Status409Conflict, "INVALID_STATE_TRANSITION: o item está in_progress, completed ou failed; nada muda e não há estorno."))
            .WithResponseExample(
                StatusCodes.Status200OK,
                new CancelItemResponse(
                    new BookingItemResponse(
                        Guid.Parse("ee7cefb9-9150-464f-bc77-90c795d6947d"), "wash", 1890, BookingItemStatusContract.Cancelled, null, null, null),
                    1890,
                    5000));
        return api;
    }
}
