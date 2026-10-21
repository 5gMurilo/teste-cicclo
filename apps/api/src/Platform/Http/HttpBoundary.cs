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
        return services;
    }

    public static IApplicationBuilder UseHttpBoundary(this IApplicationBuilder app) => app.UseExceptionHandler();
}
