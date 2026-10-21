using Cicclo.Api.Laundry;
using Cicclo.Api.Platform.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cicclo.Api.Services.GetCatalog;

/// <summary>GET /api/v1/services: traduz a projeção do catálogo para o contrato HTTP, sem reavaliar a elegibilidade.</summary>
public static class ServicesEndpoint
{
    public static IEndpointRouteBuilder MapServicesEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapGet("/services", async Task<Ok<ServicesResponse>> (PostgresServiceCatalogQuery query, CancellationToken cancellationToken) =>
                TypedResults.Ok(new ServicesResponse([.. (await query.ExecuteAsync(cancellationToken)).Select(ToResponse)])))
            .WithName("GetServices")
            .WithTags("Services")
            .WithSummary("Catálogo de serviços e contratabilidade atual.")
            .WithDescription("Lê preços e capacidade operacional do PostgreSQL a cada chamada. canBeBooked é informativo; a compra futura revalida serviço, capacidade e preço no servidor.")
            .ProducesReadProblems()
            .WithResponseExample(
                StatusCodes.Status200OK,
                new ServicesResponse(
                [
                    new("dry", "Secagem", MachineTypeContract.Dryer, 2090, MoneyCents.Currency, true, true, null),
                    new("wash", "Lavagem", MachineTypeContract.Washer, 1890, MoneyCents.Currency, false, false, UnavailabilityReasonContract.ServiceDisabled),
                ]));
        return api;
    }

    private static ServiceItemResponse ToResponse(ServiceCatalogEntry entry) =>
        new(
            entry.Code,
            entry.Name,
            entry.MachineType switch
            {
                MachineType.Washer => MachineTypeContract.Washer,
                MachineType.Dryer => MachineTypeContract.Dryer,
                _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.MachineType, "Tipo de máquina sem contrato HTTP."),
            },
            MoneyCents.FromDecimal(entry.CurrentPrice),
            MoneyCents.Currency,
            entry.IsActive,
            entry.Availability.CanBeBooked,
            entry.Availability.Reason switch
            {
                null => null,
                UnavailabilityReason.ServiceDisabled => UnavailabilityReasonContract.ServiceDisabled,
                UnavailabilityReason.NoOperationalMachine => UnavailabilityReasonContract.NoOperationalMachine,
                _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Availability.Reason, "Motivo sem contrato HTTP."),
            });
}
