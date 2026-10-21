using System.ComponentModel;
using Cicclo.Api.Laundry.GetMachines;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Services.GetCatalog;

namespace Cicclo.Api.Laundry.Simulation;

/// <summary>POST /api/v1/simulation/machines/{machineId}/occupy: ocupação sintética de uma máquina livre, sem cobrança.</summary>
public static class OccupyMachineEndpoint
{
    public static IEndpointRouteBuilder MapOccupyMachineEndpoint(this IEndpointRouteBuilder simulation)
    {
        simulation.MapPost(
                "/machines/{machineId}/occupy",
                async Task<IResult> (
                    [Description("Identificador (UUID) da máquina.")] Guid machineId,
                    OccupyMachine useCase,
                    PostgresMachinesQuery machines,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    switch (await useCase.ExecuteAsync(machineId, cancellationToken))
                    {
                        case OccupyMachineOutcome.NotFound:
                            return SimulationEndpoints.MachineNotFound(context);
                        case OccupyMachineOutcome.InvalidState:
                            return ApiProblems.Create(
                                context,
                                StatusCodes.Status409Conflict,
                                ApiProblems.InvalidStateTransition,
                                "Transição de estado inválida",
                                "A máquina não está livre ou há itens na fila do seu tipo; a ocupação sintética não fura a fila e nada foi alterado.");
                    }

                    var machine = await machines.FindAsync(machineId, cancellationToken)
                        ?? throw new InvalidOperationException("Máquina ausente depois da ocupação.");
                    return TypedResults.Ok(new SimulationResponse(MachineResponse.From(machine), null));
                })
            .WithName("OccupyMachine")
            .WithTags("Simulation")
            .WithSummary("Simulação (somente Development com Laundry:DemoMode=true): ocupar uma máquina livre.")
            .WithDescription(
                "Disponível somente em Development com Laundry:DemoMode=true; fora disso a rota não existe (404) e não aparece neste documento. "
                + "Marca uma máquina available, sem item e sem ninguém na fila do seu tipo, como running com isDemoOccupied true e activeItemId null: "
                + "ocupação sintética, sem booking, débito, lançamento ou wallet. Disputa a mesma serialização da ativação: um item que entrou na "
                + "fila antes faz a ocupação responder 409 INVALID_STATE_TRANSITION, assim como máquina reserved, running ou out_of_service. "
                + "O corpo da requisição não é lido e nenhum parâmetro escolhe wallet.")
            .Produces<SimulationResponse>(StatusCodes.Status200OK)
            .ProducesApiProblems(
                null,
                (StatusCodes.Status400BadRequest, "machineId não é UUID (VALIDATION_ERROR)."),
                (StatusCodes.Status404NotFound, "MACHINE_NOT_FOUND: nenhuma máquina com esse id; nada muda."),
                (StatusCodes.Status409Conflict, "INVALID_STATE_TRANSITION: máquina não livre ou fila anterior no tipo; nada muda."))
            .WithResponseExample(
                StatusCodes.Status200OK,
                new SimulationResponse(
                    new MachineResponse(Guid.Parse("00000000-0000-4000-8000-000000000101"), "washer-01", MachineTypeContract.Washer, MachineStateContract.Running, null, true),
                    null));
        return simulation;
    }
}
