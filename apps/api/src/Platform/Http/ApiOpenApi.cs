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
}
