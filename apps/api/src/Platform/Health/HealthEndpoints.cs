using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cicclo.Api.Platform.Health;

/// <summary>
/// Endpoints de saúde como route handlers sobre o HealthCheckService nativo: mantêm o corpo texto legado
/// e entram no OpenAPI gerado, o que não ocorre com endpoints de MapHealthChecks.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (HealthCheckService health, CancellationToken cancellationToken) =>
                CheckAsync(health, _ => false, cancellationToken))
            .WithName("GetHealth")
            .WithTags("Health")
            .WithSummary("Liveness do processo (alias legado).")
            .WithDescription("Responde Healthy em text/plain enquanto o processo atende requisições. Não consulta o PostgreSQL.")
            .Produces<string>(StatusCodes.Status200OK, "text/plain");
        return app;
    }

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
