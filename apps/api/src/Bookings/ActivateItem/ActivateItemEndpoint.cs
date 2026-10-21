using System.ComponentModel;
using Cicclo.Api.Platform.Http;

namespace Cicclo.Api.Bookings.ActivateItem;

/// <summary>POST /api/v1/bookings/{bookingId}/items/{itemId}/activate: ativa um item pago pendente e devolve o item atual.</summary>
public static class ActivateItemEndpoint
{
    public static IEndpointRouteBuilder MapActivateItemEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapPost(
                "/bookings/{bookingId}/items/{itemId}/activate",
                async Task<IResult> (
                    [Description("Identificador (UUID) do booking da wallet demo.")] Guid bookingId,
                    [Description("Identificador (UUID) do item, que precisa pertencer ao booking.")] Guid itemId,
                    ActivateItem useCase,
                    PostgresBookingProjection projection,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    if (await projection.FindAsync(bookingId, cancellationToken) is null)
                    {
                        return GetBooking.GetBookingEndpoint.BookingNotFound(context);
                    }

                    if (await useCase.ExecuteAsync(bookingId, itemId, cancellationToken) == ActivateItemOutcome.NotFound)
                    {
                        return ItemNotFound(context);
                    }

                    var booking = await projection.FindAsync(bookingId, cancellationToken)
                        ?? throw new InvalidOperationException("Booking ausente da projeção depois da ativação.");
                    return TypedResults.Ok(BookingItemResponse.From(booking.Items.Single(item => item.ItemId == itemId)));
                })
            .WithName("ActivateBookingItem")
            .WithTags("Bookings")
            .WithSummary("Ativar uma execução paga e pendente.")
            .WithDescription(
                "Coloca um item pending_activation na fila FIFO do seu tipo de máquina com ticket novo; se houver máquina livre e ninguém à frente, "
                + "o item recebe oferta exclusiva de 60 s (to_be_confirmed, com machineId e expiresAt). Com máquina ocupada ou fila anterior, fica "
                + "queued com queuePosition derivada, sem ultrapassar quem chegou antes. Não cobra nada: o débito aconteceu na compra. Repetir a "
                + "ativação de um item queued, to_be_confirmed ou in_progress responde 200 com o estado atual, sem ticket novo; como todo comando, "
                + "a fila do tipo é reconciliada antes da resposta, então uma oferta já vencida volta ao fim da fila. O corpo da requisição não é lido.")
            .Produces<BookingItemResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                "Limite da política de comandos de item excedido (15/min por IP por padrão, compartilhado entre activate e confirm).",
                (StatusCodes.Status400BadRequest, "bookingId ou itemId não é UUID (VALIDATION_ERROR)."),
                (StatusCodes.Status404NotFound, "BOOKING_NOT_FOUND (booking inexistente na wallet demo) ou ITEM_NOT_FOUND (item inexistente ou de outro booking); nada muda."))
            .WithResponseExample(StatusCodes.Status200OK, GetBooking.GetBookingEndpoint.Example.Items[1]);
        return api;
    }

    internal static IResult ItemNotFound(HttpContext context) =>
        ApiProblems.NotFound(context, ApiProblems.ItemNotFound, "Item não encontrado", "Não existe item com este identificador no booking informado.");
}
