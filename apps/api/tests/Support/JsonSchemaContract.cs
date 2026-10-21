using System.Text.Json;
using Json.Schema;
using Xunit;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Avalia JSON real contra o schema efetivamente gerado no documento OpenAPI 3.1 (draft 2020-12),
/// resolvendo as referências locais do próprio documento. Não mantém cópia manual dos DTOs.
/// </summary>
public static class JsonSchemaContract
{
    public static string ResponsePointer(string path, string status, string mediaType = "application/json") =>
        $"#/paths/{Escape(path)}/get/responses/{status}/content/{Escape(mediaType)}/schema";

    public static EvaluationResults Evaluate(string openApiJson, string schemaPointer, string instanceJson)
    {
        var uri = new Uri($"https://cicclo.test/openapi/{Guid.NewGuid():N}.json");
        using var document = JsonDocument.Parse(openApiJson);
        var registry = new SchemaRegistry();
        registry.Register(new JsonElementBaseDocument(document.RootElement.Clone(), uri));
        var schema = JsonSchema.FromText(
            $$"""{"$schema":"https://json-schema.org/draft/2020-12/schema","$ref":"{{uri}}{{schemaPointer}}"}""",
            new BuildOptions { SchemaRegistry = registry, Dialect = Dialect.Draft202012 });
        using var instance = JsonDocument.Parse(instanceJson);
        return schema.Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
    }

    public static void AssertValid(string openApiJson, string schemaPointer, string instanceJson)
    {
        var result = Evaluate(openApiJson, schemaPointer, instanceJson);
        Assert.True(result.IsValid, $"{instanceJson} inválido em {schemaPointer}: {Describe(result)}");
    }

    public static void AssertInvalid(string openApiJson, string schemaPointer, string instanceJson) =>
        Assert.False(Evaluate(openApiJson, schemaPointer, instanceJson).IsValid, $"{instanceJson} deveria violar {schemaPointer}");

    /// <summary>Exemplo publicado em uma resposta, serializado de volta como texto JSON.</summary>
    public static string Example(string openApiJson, string path, string status, string mediaType = "application/json")
    {
        using var document = JsonDocument.Parse(openApiJson);
        return document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get").GetProperty("responses")
            .GetProperty(status).GetProperty("content").GetProperty(mediaType).GetProperty("example").GetRawText();
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment.Replace("~", "~0").Replace("/", "~1"));

    private static string Describe(EvaluationResults result) =>
        string.Join("; ", (result.Details ?? []).Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Key} {error.Value}")));
}
