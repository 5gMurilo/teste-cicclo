using System.ComponentModel;
using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry.GetMachines;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Services.GetCatalog;

namespace Cicclo.Api.Laundry.ConfirmIncident;

/// <summary>POST /api/v1/simulation/incidents/{incidentId}/confirm: confirma um relato pelo mesmo caso de falha do breakdown.</summary>
public static class ConfirmIncidentEndpoint
{
    public static IEndpointRouteBuilder MapConfirmIncidentEndpoint(this IEndpointRouteBuilder simulation)
    {
        simulation.MapPost(
                "/incidents/{incidentId}/confirm",
                async Task<IResult> (
                    [Description("Identificador (UUID) do incidente relatado.")] Guid incidentId,
                    ConfirmIncident useCase,
                    PostgresMachinesQuery machines,
                    PostgresBookingProjection bookings,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var result = await useCase.ExecuteAsync(incidentId, cancellationToken);
                    switch (result.Outcome)
                    {
                        case ConfirmIncidentOutcome.NotFound:
                            return ApiProblems.Create(
                                context, StatusCodes.Status404NotFound, ApiProblems.IncidentNotFound, "Incidente não encontrado", "Nenhum incidente com esse identificador; nada foi alterado.");
                        case ConfirmIncidentOutcome.InvalidState:
                            return ApiProblems.Create(
                                context,
                                StatusCodes.Status409Conflict,
                                ApiProblems.InvalidStateTransition,
                                "Transição de estado inválida",
                                "O incidente não está ligado a uma execução em andamento; nada foi alterado e não há refund.");
                    }

                    var machine = await machines.FindAsync(result.MachineId!.Value, cancellationToken)
                        ?? throw new InvalidOperationException("Máquina do incidente ausente depois da confirmação.");
                    var booking = await bookings.FindAsync(result.BookingId!.Value, cancellationToken)
                        ?? throw new InvalidOperationException("Booking do incidente ausente da projeção depois da confirmação.");
                    var item = BookingItemResponse.From(booking.Items.Single(candidate => candidate.ItemId == result.ItemId));
                    return TypedResults.Ok(new ConfirmIncidentResponse(incidentId, ConfirmedIncidentStatusContract.Confirmed, MachineResponse.From(machine), item));
                })
            .WithName("ConfirmIncident")
            .WithTags("Simulation")
            .WithSummary("Simulação (somente Development com Laundry:DemoMode=true): confirmar um relato de defeito.")
            .WithDescription(
                "Disponível somente em Development com Laundry:DemoMode=true; fora disso a rota não existe (404) e não aparece neste documento. "
                + "Confirma um relato reported cuja execução ainda está em andamento (máquina do relato running com o item in_progress) "
                + "aplicando a mesma falha do breakdown, numa única transação: o item passa a failed, a máquina a out_of_service, a wallet "
                + "demo recebe de volta o preço histórico do item com um único lançamento refund e o relato passa a confirmed. Repetir sobre "
                + "um incidente já confirmado responde 200 com o estado atual, sem nova falha nem refund. Relato cuja execução já foi "
                + "concluída ou falhou (por exemplo, pelo breakdown) ou cujo vínculo máquina/item não corresponde à execução corrente "
                + "responde 409 INVALID_STATE_TRANSITION sem mudança: o item não é reaberto e o relato continua reported, só como "
                + "histórico, sem dívida financeira. Confirmação e breakdown da mesma execução disputam o mesmo lock: um único refund. "
                + "O corpo da requisição não é lido.")
            .Produces<ConfirmIncidentResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                null,
                (StatusCodes.Status400BadRequest, "incidentId não é UUID (VALIDATION_ERROR)."),
                (StatusCodes.Status404NotFound, "INCIDENT_NOT_FOUND: nenhum incidente com esse id; nada muda."),
                (StatusCodes.Status409Conflict, "INVALID_STATE_TRANSITION: o relato não está ligado a uma execução em andamento; nada muda e não há refund."))
            .WithResponseExample(
                StatusCodes.Status200OK,
                new ConfirmIncidentResponse(
                    Guid.Parse("0199f5a0-7b2c-7d4e-9a10-3c5d7e9f1a2b"),
                    ConfirmedIncidentStatusContract.Confirmed,
                    new MachineResponse(Guid.Parse("00000000-0000-4000-8000-000000000101"), "washer-01", MachineTypeContract.Washer, MachineStateContract.OutOfService, null, false),
                    new BookingItemResponse(Guid.Parse("ee7cefb9-9150-464f-bc77-90c795d6947d"), "wash", 1890, BookingItemStatusContract.Failed, null, null, null)));
        return simulation;
    }
}
