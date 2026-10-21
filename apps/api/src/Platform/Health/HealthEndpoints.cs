using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cicclo.Api.Platform.Health;

/// <summary>
/// Endpoints de saúde como route handlers sobre o HealthCheckService nativo: mantêm o corpo texto legado
/// e entram no OpenAPI gerado, o que não ocorre com endpoints de MapHealthChecks.
/// Liveness não executa checks; readiness executa somente os checks marcados com <see cref="DatabaseReadinessCheck.Tag"/>.
/// </summary>
public static class HealthEndpoints
{
    public static IServiceCollection AddApiHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: [DatabaseReadinessCheck.Tag]);
        return services;
    }

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        MapLiveness(app, "/health", "GetHealth", "Liveness do processo (alias legado).");
        MapLiveness(app, "/health/live", "GetLiveness", "Liveness do processo.");
        app.MapGet("/health/ready", (HealthCheckService health, CancellationToken cancellationToken) =>
                CheckAsync(health, registration => registration.Tags.Contains(DatabaseReadinessCheck.Tag), cancellationToken))
            .WithName("GetReadiness")
            .WithTags("Health")
            .WithSummary("Readiness da base demo.")
            .WithDescription("Healthy quando o PostgreSQL responde e a base demo foi preparada pelo bootstrap; Unhealthy (503) caso contrário. Não expõe detalhes do banco.")
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .Produces<string>(StatusCodes.Status503ServiceUnavailable, "text/plain");
        return app;
    }

    private static void MapLiveness(IEndpointRouteBuilder app, string pattern, string operationId, string summary) =>
        app.MapGet(pattern, (HealthCheckService health, CancellationToken cancellationToken) =>
                CheckAsync(health, _ => false, cancellationToken))
            .WithName(operationId)
            .WithTags("Health")
            .WithSummary(summary)
            .WithDescription("Responde Healthy em text/plain enquanto o processo atende requisições. Não consulta o PostgreSQL.")
            .Produces<string>(StatusCodes.Status200OK, "text/plain");

    private static async Task<IResult> CheckAsync(
        HealthCheckService health,
        Func<HealthCheckRegistration, bool> predicate,
        CancellationToken cancellationToken)
    {
        var report = await health.CheckHealthAsync(predicate, cancellationToken);
        var status = report.Status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK;
        return TypedResults.Text(report.Status.ToString(), "text/plain", statusCode: status);
    }
}
