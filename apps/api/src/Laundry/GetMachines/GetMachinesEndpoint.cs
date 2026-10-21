using Cicclo.Api.Platform.Http;
using Cicclo.Api.Services.GetCatalog;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cicclo.Api.Laundry.GetMachines;

/// <summary>GET /api/v1/machines: estado persistido das máquinas, distinguindo execução real de ocupação sintética.</summary>
public static class GetMachinesEndpoint
{
    public static IEndpointRouteBuilder MapGetMachinesEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapGet("/machines", async Task<Ok<MachinesResponse>> (PostgresMachinesQuery query, CancellationToken cancellationToken) =>
                TypedResults.Ok(new MachinesResponse([.. (await query.ListAsync(cancellationToken)).Select(MachineResponse.From)])))
            .WithName("GetMachines")
            .WithTags("Machines")
            .WithSummary("Estado atual das máquinas da demonstração.")
            .WithDescription(
                "Lê do PostgreSQL, a cada chamada e sem alterar nada, o estado de cada máquina em ordem de name. activeItemId aponta o item "
                + "com a oferta corrente (reserved) ou em execução real (running). Running com isDemoOccupied true e activeItemId null é ocupação "
                + "sintética da simulação, sem booking nem cobrança. available e out_of_service nunca têm item. A leitura não expira ofertas nem "
                + "promove a fila.")
            .ProducesApiProblems("Limite da política de leituras de contratações, histórico e máquinas excedido (janela fixa por IP).")
            .WithResponseExample(
                StatusCodes.Status200OK,
                new MachinesResponse(
                [
                    new(Guid.Parse("00000000-0000-4000-8000-000000000102"), "dryer-01", MachineTypeContract.Dryer, MachineStateContract.Running, null, true),
                    new(
                        Guid.Parse("00000000-0000-4000-8000-000000000101"),
                        "washer-01",
                        MachineTypeContract.Washer,
                        MachineStateContract.Running,
                        Guid.Parse("ee7cefb9-9150-464f-bc77-90c795d6947d"),
                        false),
                ]));
        return api;
    }
}
