using System.ComponentModel;
using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry.ConfirmMachineFailure;
using Cicclo.Api.Laundry.GetMachines;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Services.GetCatalog;

namespace Cicclo.Api.Laundry.Simulation;

/// <summary>POST /api/v1/simulation/machines/{machineId}/breakdown: confirma a falha da execução real ou sintética.</summary>
public static class BreakdownMachineEndpoint
{
    public static IEndpointRouteBuilder MapBreakdownMachineEndpoint(this IEndpointRouteBuilder simulation)
    {
        simulation.MapPost(
                "/machines/{machineId}/breakdown",
                async Task<IResult> (
                    [Description("Identificador (UUID) da máquina.")] Guid machineId,
                    ConfirmMachineFailure.ConfirmMachineFailure useCase,
                    PostgresMachinesQuery machines,
                    PostgresBookingProjection bookings,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var result = await useCase.ExecuteAsync(machineId, cancellationToken);
                    switch (result.Outcome)
                    {
                        case ConfirmMachineFailureOutcome.NotFound:
                            return SimulationEndpoints.MachineNotFound(context);
                        case ConfirmMachineFailureOutcome.InvalidState:
                            return ApiProblems.Create(
                                context,
                                StatusCodes.Status409Conflict,
                                ApiProblems.InvalidStateTransition,
                                "Transição de estado inválida",
                                "A máquina não está em execução; não há execução para falhar e nada foi alterado.");
                    }

                    var machine = await machines.FindAsync(machineId, cancellationToken)
                        ?? throw new InvalidOperationException("Máquina ausente depois da falha.");
                    BookingItemResponse? item = null;
                    if (result.BookingId is { } bookingId)
                    {
                        var booking = await bookings.FindAsync(bookingId, cancellationToken)
                            ?? throw new InvalidOperationException("Booking ausente da projeção depois da falha.");
                        item = BookingItemResponse.From(booking.Items.Single(candidate => candidate.ItemId == result.ItemId));
                    }

                    return TypedResults.Ok(new SimulationResponse(MachineResponse.From(machine), item));
                })
            .WithName("BreakdownMachine")
            .WithTags("Simulation")
            .WithSummary("Simulação (somente Development com Laundry:DemoMode=true): confirmar a falha de uma máquina em execução.")
            .WithDescription(
                "Disponível somente em Development com Laundry:DemoMode=true; fora disso a rota não existe (404) e não aparece neste documento. "
                + "Numa única transação, confirma a falha de uma máquina running: com item real, o item passa a failed, a máquina a "
                + "out_of_service e a wallet demo recebe de volta o preço histórico do item (unitPriceCents da compra, nunca o preço atual) com "
                + "um único lançamento refund; com ocupação sintética, só a máquina passa a out_of_service, sem item, refund ou lançamento. "
                + "Conclusão e falha da mesma execução são exclusivas: só uma acontece. Itens pagos na fila ou pendentes continuam consultáveis "
                + "e canceláveis. Responde a máquina atual e o item que falhou (null na ocupação sintética). Máquina available, reserved ou "
                + "out_of_service, inclusive uma falha repetida, responde 409 INVALID_STATE_TRANSITION sem mudança e sem novo refund. O corpo "
                + "da requisição não é lido.")
            .Produces<SimulationResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                null,
                (StatusCodes.Status400BadRequest, "machineId não é UUID (VALIDATION_ERROR)."),
                (StatusCodes.Status404NotFound, "MACHINE_NOT_FOUND: nenhuma máquina com esse id; nada muda."),
                (StatusCodes.Status409Conflict, "INVALID_STATE_TRANSITION: a máquina não está running; nada muda e não há refund."))
            .WithResponseExample(
                StatusCodes.Status200OK,
                new SimulationResponse(
                    new MachineResponse(Guid.Parse("00000000-0000-4000-8000-000000000101"), "washer-01", MachineTypeContract.Washer, MachineStateContract.OutOfService, null, false),
                    new BookingItemResponse(Guid.Parse("ee7cefb9-9150-464f-bc77-90c795d6947d"), "wash", 1890, BookingItemStatusContract.Failed, null, null, null)));
        return simulation;
    }
}
