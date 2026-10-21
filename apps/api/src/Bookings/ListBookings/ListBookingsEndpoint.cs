using System.ComponentModel;
using Cicclo.Api.Platform.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cicclo.Api.Bookings.ListBookings;

/// <summary>GET /api/v1/bookings: bookings da wallet demo paginados, com a mesma projeção do detalhe e sem efeito colateral.</summary>
public static class ListBookingsEndpoint
{
    public static IEndpointRouteBuilder MapListBookingsEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapGet(
                "/bookings",
                async Task<IResult> (
                    [FromQuery, Description("Página 1-based; padrão 1.")] int? page,
                    [FromQuery, Description("Itens por página, de 1 a 100; padrão 20.")] int? pageSize,
                    PostgresBookingProjection projection,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    if (Paging.Validate(context, page, pageSize, out var resolvedPage, out var resolvedPageSize) is { } invalid)
                    {
                        return invalid;
                    }

                    var (bookings, hasNextPage) = await projection.ListAsync(resolvedPage, resolvedPageSize, cancellationToken);
                    return TypedResults.Ok(new PageResponse<BookingResponse>([.. bookings.Select(BookingResponse.From)], resolvedPage, resolvedPageSize, hasNextPage));
                })
            .WithName("ListBookings")
            .WithTags("Bookings")
            .WithSummary("Bookings da wallet demo, paginados.")
            .WithDescription(
                "Lê do PostgreSQL, num único snapshot, uma página de bookings da wallet demo em ordem createdAt decrescente e bookingId decrescente "
                + "(ordem total, estável com instantes empatados). Cada booking usa a mesma projeção do GET por id: cobrança e preços históricos, "
                + "saldo atual e estado atual dos itens. Não altera saldo, itens nem fila. Parâmetros como walletId ou userId são ignorados.")
            .Produces<PageResponse<BookingResponse>>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                "Limite da política de leituras de contratações excedido (janela fixa por IP).",
                (StatusCodes.Status400BadRequest, "page ou pageSize não inteiro ou fora dos limites (VALIDATION_ERROR)."))
            .WithResponseExample(
                StatusCodes.Status200OK,
                new PageResponse<BookingResponse>([GetBooking.GetBookingEndpoint.Example], 1, Paging.DefaultPageSize, false));
        return api;
    }
}
