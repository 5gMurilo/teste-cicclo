using System.ComponentModel;
using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry.GetMachines;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Services.GetCatalog;

namespace Cicclo.Api.Laundry.Simulation;

/// <summary>POST /api/v1/simulation/machines/{machineId}/complete: conclui a execução real ou sintética e avança a fila.</summary>
public static class CompleteMachineEndpoint
{
    public static IEndpointRouteBuilder MapCompleteMachineEndpoint(this IEndpointRouteBuilder simulation)
    {
        simulation.MapPost(
                "/machines/{machineId}/complete",
                async Task<IResult> (
                    [Description("Identificador (UUID) da máquina.")] Guid machineId,
                    CompleteMachine useCase,
                    PostgresMachinesQuery machines,
                    PostgresBookingProjection bookings,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var result = await useCase.ExecuteAsync(machineId, cancellationToken);
                    switch (result.Outcome)
                    {
                        case CompleteMachineOutcome.NotFound:
                            return SimulationEndpoints.MachineNotFound(context);
                        case CompleteMachineOutcome.InvalidState:
                            return ApiProblems.Create(
                                context,
                                StatusCodes.Status409Conflict,
                                ApiProblems.InvalidStateTransition,
                                "Transição de estado inválida",
                                "A máquina não está em execução; não há execução para concluir e nada foi alterado.");
                    }

                    var machine = await machines.FindAsync(machineId, cancellationToken)
                        ?? throw new InvalidOperationException("Máquina ausente depois da conclusão.");
                    BookingItemResponse? item = null;
                    if (result.BookingId is { } bookingId)
                    {
                        var booking = await bookings.FindAsync(bookingId, cancellationToken)
                            ?? throw new InvalidOperationException("Booking ausente da projeção depois da conclusão.");
                        item = BookingItemResponse.From(booking.Items.Single(candidate => candidate.ItemId == result.ItemId));
                    }

                    return TypedResults.Ok(new SimulationResponse(MachineResponse.From(machine), item));
                })
            .WithName("CompleteMachine")
            .WithTags("Simulation")
            .WithSummary("Simulação (somente Development com Laundry:DemoMode=true): concluir a execução de uma máquina.")
            .WithDescription(
                "Disponível somente em Development com Laundry:DemoMode=true; fora disso a rota não existe (404) e não aparece neste documento. "
                + "Numa única transação, conclui a execução de uma máquina running: com item real, o item passa a completed e a máquina a "
                + "available; com ocupação sintética, só a máquina é liberada. Em seguida o próximo item da fila do tipo (por ticket) recebe a "
                + "oferta de 60 s. Não há débito, refund nem lançamento. Responde a máquina atual e o item concluído (null na ocupação "
                + "sintética). Máquina available, reserved ou out_of_service, inclusive uma conclusão repetida, responde 409 "
                + "INVALID_STATE_TRANSITION sem mudança. O corpo da requisição não é lido.")
            .Produces<SimulationResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                null,
                (StatusCodes.Status400BadRequest, "machineId não é UUID (VALIDATION_ERROR)."),
                (StatusCodes.Status404NotFound, "MACHINE_NOT_FOUND: nenhuma máquina com esse id; nada muda."),
                (StatusCodes.Status409Conflict, "INVALID_STATE_TRANSITION: a máquina não está running; nada muda."))
            .WithResponseExample(
                StatusCodes.Status200OK,
                new SimulationResponse(
                    new MachineResponse(Guid.Parse("00000000-0000-4000-8000-000000000101"), "washer-01", MachineTypeContract.Washer, MachineStateContract.Available, null, false),
                    new BookingItemResponse(Guid.Parse("ee7cefb9-9150-464f-bc77-90c795d6947d"), "wash", 1890, BookingItemStatusContract.Completed, null, null, null)));
        return simulation;
    }
}
