using System.ComponentModel;
using Cicclo.Api.Bookings;
using Cicclo.Api.Platform.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cicclo.Api.Laundry.ReportIssue;

/// <summary>
/// POST /api/v1/bookings/{bookingId}/items/{itemId}/report-issue: registra o relato de defeito de uma execução em
/// andamento (202), sem efeito financeiro; repetir devolve o relato aberto existente.
/// </summary>
public static class ReportIssueEndpoint
{
    public static IEndpointRouteBuilder MapReportIssueEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapPost(
                "/bookings/{bookingId}/items/{itemId}/report-issue",
                async Task<IResult> (
                    [Description("Identificador (UUID) do booking da wallet demo.")] Guid bookingId,
                    [Description("Identificador (UUID) do item em execução, que precisa pertencer ao booking.")] Guid itemId,
                    [FromBody] ReportIssueRequest? request,
                    ReportIssue useCase,
                    PostgresBookingProjection projection,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    if (!IssueDescription.TryNormalize(request?.Description, out var description))
                    {
                        return ApiProblems.Validation(
                            context, new Dictionary<string, string[]> { ["description"] = [$"Use no máximo {IssueDescription.MaxLength} caracteres."] });
                    }

                    if (await projection.FindAsync(bookingId, cancellationToken) is null)
                    {
                        return Bookings.GetBooking.GetBookingEndpoint.BookingNotFound(context);
                    }

                    var result = await useCase.ExecuteAsync(bookingId, itemId, description, cancellationToken);
                    return result.Outcome switch
                    {
                        ReportIssueOutcome.NotFound => Bookings.ActivateItem.ActivateItemEndpoint.ItemNotFound(context),
                        ReportIssueOutcome.InvalidState => ApiProblems.Create(
                            context,
                            StatusCodes.Status409Conflict,
                            ApiProblems.InvalidStateTransition,
                            "Transição de estado inválida",
                            "Só uma execução em andamento (in_progress) aceita relato de defeito; nada foi registrado."),
                        _ => TypedResults.Accepted((string?)null, new ReportIssueResponse(result.IncidentId!.Value, IncidentStatusContract.Reported)),
                    };
                })
            .WithName("ReportItemIssue")
            .WithTags("Bookings")
            .WithSummary("Relatar defeito numa execução em andamento, sem efeito financeiro.")
            .WithDescription(
                "Registra um relato (incidente reported) para um item in_progress do booking, ligado à máquina da execução. O relato é só "
                + "um fato do usuário: não altera item, máquina, saldo nem lançamentos, não confirma defeito e nunca concede estorno; a "
                + "confirmação de defeito é exclusiva da simulação. Responde 202 com incidentId e status=reported. Enquanto houver relato "
                + "aberto para o item, repetir responde 202 com o mesmo incidentId e mantém a descrição original. O corpo é opcional: "
                + "description omitida, null ou só com espaços equivale a relato sem descrição; acima de 1000 caracteres, campo desconhecido "
                + "ou tipo incorreto responde 400 VALIDATION_ERROR. Item fora de in_progress responde 409 INVALID_STATE_TRANSITION sem registro.")
            .Produces<ReportIssueResponse>(StatusCodes.Status202Accepted)
            .ProducesApiProblems(
                "Limite da política de relatos excedido (3/min por IP por padrão).",
                (StatusCodes.Status400BadRequest, "VALIDATION_ERROR: bookingId ou itemId não é UUID, JSON malformado, campo desconhecido, tipo incorreto ou description acima de 1000 caracteres."),
                (StatusCodes.Status404NotFound, "BOOKING_NOT_FOUND (booking inexistente na wallet demo) ou ITEM_NOT_FOUND (item inexistente ou de outro booking); nada é registrado."),
                (StatusCodes.Status409Conflict, "INVALID_STATE_TRANSITION: o item não está in_progress (ainda não iniciou ou já terminou); nada é registrado."),
                (StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE: corpo presente sem Content-Type application/json."))
            .WithResponseExample(
                StatusCodes.Status202Accepted,
                new ReportIssueResponse(Guid.Parse("019a0a3c-6f00-7000-8000-000000000001"), IncidentStatusContract.Reported));
        return api;
    }
}
