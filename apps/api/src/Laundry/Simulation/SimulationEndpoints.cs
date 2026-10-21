using Cicclo.Api.Platform;
using Cicclo.Api.Platform.Http;
using Microsoft.Extensions.Options;

namespace Cicclo.Api.Laundry.Simulation;

/// <summary>
/// Barreira única das rotas de simulação (SIM-08): mapeadas em /api/v1/simulation somente em Development com
/// Laundry:DemoMode=true. Fora dessa conjunção não existem (404) nem aparecem no OpenAPI; não há pseudo-autorização.
/// </summary>
public static class SimulationEndpoints
{
    public static WebApplication MapSimulationEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment() && app.Services.GetRequiredService<IOptions<LaundryOptions>>().Value.DemoMode)
        {
            app.MapGroup("/api/v1/simulation").MapOccupyMachineEndpoint();
        }

        return app;
    }

    internal static IResult MachineNotFound(HttpContext context) =>
        ApiProblems.Create(
            context, StatusCodes.Status404NotFound, ApiProblems.MachineNotFound, "Máquina não encontrada", "Nenhuma máquina com esse identificador; nada foi alterado.");
}
