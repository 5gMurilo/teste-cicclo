using System.Net;
using Cicclo.Api.Platform.Http;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Xunit;

namespace Cicclo.Api.Tests.Support;

public sealed record RequiredOperation(string Path, string Method, string OperationId);

/// <summary>
/// Lê o documento gerado com o parser oficial (Microsoft.OpenApi) e lista violações do contrato mínimo:
/// diagnósticos do leitor, operações requeridas ausentes, operationId duplicado e metadados obrigatórios.
/// Opera sobre o JSON recebido; nunca altera spec ou implementação.
/// </summary>
public static class OpenApiContract
{
    public static async Task<string> FetchAsync(HttpClient client)
    {
        using var response = await client.GetAsync(ApiOpenApi.DocumentPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    public static ReadResult Read(string json) => OpenApiDocument.Parse(json, OpenApiConstants.Json);

    public static IReadOnlyList<string> Problems(string json, params RequiredOperation[] required)
    {
        var result = Read(json);
        var problems = result.Diagnostic?.Errors.Select(error => $"parser: {error}").ToList() ?? ["parser: sem diagnóstico"];
        if (result.Document?.Paths is null)
        {
            problems.Add("documento sem paths");
            return problems;
        }

        var operations = result.Document.Paths
            .SelectMany(path => (path.Value.Operations ?? []).Select(operation => (Path: path.Key, Method: operation.Key.Method, Operation: operation.Value)))
            .ToList();
        problems.AddRange(operations
            .GroupBy(entry => entry.Operation.OperationId)
            .Where(group => group.Count() > 1)
            .Select(group => $"operationId duplicado: {group.Key}"));
        foreach (var (path, method, operation) in operations)
        {
            if (string.IsNullOrWhiteSpace(operation.OperationId) || string.IsNullOrWhiteSpace(operation.Summary)
                || string.IsNullOrWhiteSpace(operation.Description) || operation.Tags is not { Count: > 0 })
            {
                problems.Add($"metadados incompletos: {method} {path}");
            }
        }

        problems.AddRange(required
            .Where(expected => !operations.Any(entry => entry.Path == expected.Path
                && entry.Method == expected.Method
                && entry.Operation.OperationId == expected.OperationId))
            .Select(expected => $"operação ausente: {expected.Method} {expected.Path} ({expected.OperationId})"));
        return problems;
    }
}
