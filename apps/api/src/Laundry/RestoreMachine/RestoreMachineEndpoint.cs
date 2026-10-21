using System.ComponentModel;
using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry.GetMachines;
using Cicclo.Api.Laundry.Simulation;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Services.GetCatalog;

namespace Cicclo.Api.Laundry.RestoreMachine;

/// <summary>POST /api/v1/simulation/machines/{machineId}/restore: devolve a máquina fora de serviço à operação e retoma a fila.</summary>
public static class RestoreMachineEndpoint
{
    public static IEndpointRouteBuilder MapRestoreMachineEndpoint(this IEndpointRouteBuilder simulation)
    {
        simulation.MapPost(
                "/machines/{machineId}/restore",
                async Task<IResult> (
                    [Description("Identificador (UUID) da máquina.")] Guid machineId,
                    RestoreMachine useCase,
                    PostgresMachinesQuery machines,
                    PostgresBookingProjection bookings,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var result = await useCase.ExecuteAsync(machineId, cancellationToken);
                    switch (result.Outcome)
                    {
                        case RestoreMachineOutcome.NotFound:
                            return SimulationEndpoints.MachineNotFound(context);
                        case RestoreMachineOutcome.InvalidState:
                            return ApiProblems.Create(
                                context,
                                StatusCodes.Status409Conflict,
                                ApiProblems.InvalidStateTransition,
                                "Transição de estado inválida",
                                "A máquina não está fora de serviço; não há o que restaurar e nada foi alterado.");
                    }

                    var machine = await machines.FindAsync(machineId, cancellationToken)
                        ?? throw new InvalidOperationException("Máquina ausente depois da restauração.");
                    BookingItemResponse? item = null;
                    if (result.BookingId is { } bookingId)
                    {
                        var booking = await bookings.FindAsync(bookingId, cancellationToken)
                            ?? throw new InvalidOperationException("Booking ausente da projeção depois da restauração.");
                        item = BookingItemResponse.From(booking.Items.Single(candidate => candidate.ItemId == result.ItemId));
                    }

                    return TypedResults.Ok(new SimulationResponse(MachineResponse.From(machine), item));
                })
            .WithName("RestoreMachine")
            .WithTags("Simulation")
            .WithSummary("Simulação (somente Development com Laundry:DemoMode=true): restaurar uma máquina fora de serviço.")
            .WithDescription(
                "Disponível somente em Development com Laundry:DemoMode=true; fora disso a rota não existe (404) e não aparece neste documento. "
                + "Numa única transação, devolve uma máquina out_of_service ao estado available e, em seguida, o próximo item da fila do "
                + "tipo (por ticket) recebe a oferta de 60 s nela. Itens failed ou cancelled não voltam à fila e nenhum item é reativado; "
                + "não há compra, débito, refund nem lançamento. Responde a máquina atual e o item que recebeu a oferta desta máquina "
                + "(null quando não havia fila). Máquina available, reserved ou running, inclusive uma restauração repetida, responde 409 "
                + "INVALID_STATE_TRANSITION sem mudança. O corpo da requisição não é lido.")
            .Produces<SimulationResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                null,
                (StatusCodes.Status400BadRequest, "machineId não é UUID (VALIDATION_ERROR)."),
                (StatusCodes.Status404NotFound, "MACHINE_NOT_FOUND: nenhuma máquina com esse id; nada muda."),
                (StatusCodes.Status409Conflict, "INVALID_STATE_TRANSITION: a máquina não está out_of_service; nada muda."))
            .WithResponseExample(
                StatusCodes.Status200OK,
                new SimulationResponse(
                    new MachineResponse(Guid.Parse("00000000-0000-4000-8000-000000000101"), "washer-01", MachineTypeContract.Washer, MachineStateContract.Reserved, Guid.Parse("ee7cefb9-9150-464f-bc77-90c795d6947d"), false),
                    new BookingItemResponse(
                        Guid.Parse("ee7cefb9-9150-464f-bc77-90c795d6947d"),
                        "wash",
                        1890,
                        BookingItemStatusContract.ToBeConfirmed,
                        Guid.Parse("00000000-0000-4000-8000-000000000101"),
                        null,
                        new DateTime(2026, 10, 21, 12, 1, 30, DateTimeKind.Utc))));
        return simulation;
    }
}
