using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Cicclo.Api.Platform.Http;

/// <summary>Registro coeso da borda HTTP: erros, JSON e limite de body. Rotas de negócio não repetem esse tratamento.</summary>
public static class HttpBoundary
{
    public const long MaxRequestBodyBytes = 16 * 1024;

    public static IServiceCollection AddHttpBoundary(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        services.Configure<KestrelServerOptions>(options => options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
        // Binding inválido vira exceção em todo ambiente, para o ApiExceptionHandler responder com o contrato de erro.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        return services;
    }

    /// <summary>
    /// Tratamento de exceções e limite de corpo: Content-Length acima do limite é recusado com 413 antes de qualquer
    /// leitura (também no TestServer, que não aplica o limite do Kestrel). Um 413 ou 415 sem corpo, que o binding de
    /// Minimal APIs produz sem lançar exceção (corpo chunked acima do limite, Content-Type que não é JSON), recebe o
    /// mesmo Problem Details.
    /// </summary>
    public static IApplicationBuilder UseHttpBoundary(this IApplicationBuilder app) =>
        app.UseExceptionHandler().Use(async (context, next) =>
        {
            if (context.Request.ContentLength > MaxRequestBodyBytes)
            {
                await ApiProblems.TooLarge(context).ExecuteAsync(context);
                return;
            }

            await next(context);
            if (!context.Response.HasStarted)
            {
                var problem = context.Response.StatusCode switch
                {
                    StatusCodes.Status413PayloadTooLarge => ApiProblems.TooLarge(context),
                    StatusCodes.Status415UnsupportedMediaType => ApiProblems.Unsupported(context),
                    _ => null,
                };
                await (problem?.ExecuteAsync(context) ?? Task.CompletedTask);
            }
        });
}
