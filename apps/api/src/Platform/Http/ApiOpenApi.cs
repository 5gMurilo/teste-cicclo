using System.Text.Json;
using Microsoft.OpenApi;

namespace Cicclo.Api.Platform.Http;

/// <summary>Documento OpenAPI v1 gerado a partir dos endpoints reais; publicado somente em Development.</summary>
public static class ApiOpenApi
{
    public const string DocumentPath = "/openapi/v1.json";

    public static IServiceCollection AddApiOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Cicclo API";
            document.Info.Version = "v1";
            document.Info.Description = "API da demonstração Cicclo. Valores monetários em centavos inteiros (BRL).";
            return Task.CompletedTask;
        }));

    public static WebApplication MapApiOpenApi(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi(DocumentPath);
        }

        return app;
    }

    /// <summary>Erros comuns das leituras: 429 com Retry-After, 500 e 503, todos com o corpo <see cref="ApiProblem"/>.</summary>
    public static RouteHandlerBuilder ProducesReadProblems(this RouteHandlerBuilder builder) =>
        builder
            .Produces<ApiProblem>(StatusCodes.Status429TooManyRequests, ApiProblems.ContentType)
            .Produces<ApiProblem>(StatusCodes.Status500InternalServerError, ApiProblems.ContentType)
            .Produces<ApiProblem>(StatusCodes.Status503ServiceUnavailable, ApiProblems.ContentType)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                var limited = (OpenApiResponse)operation.Responses!["429"];
                limited.Description = "Limite da política de leituras excedido (janela fixa por IP).";
                limited.Headers ??= new Dictionary<string, IOpenApiHeader>();
                limited.Headers["Retry-After"] = new OpenApiHeader
                {
                    Description = "Segundos inteiros até a próxima janela.",
                    Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1" },
                };
                operation.Responses["500"].Description = "Falha inesperada; corpo sanitizado com traceId.";
                operation.Responses["503"].Description = "PostgreSQL indisponível ou base demo não preparada.";
                return Task.CompletedTask;
            });

    /// <summary>Exemplo JSON da resposta de sucesso, serializado com as mesmas convenções web da API.</summary>
    public static RouteHandlerBuilder WithResponseExample<T>(this RouteHandlerBuilder builder, int status, T example) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Responses![status.ToString(System.Globalization.CultureInfo.InvariantCulture)].Content!["application/json"].Example =
                JsonSerializer.SerializeToNode(example, JsonSerializerOptions.Web);
            return Task.CompletedTask;
        });
}
