using System.Net;
using System.Text.Json;
using Xunit;

namespace Cicclo.Api.Tests.Support;

/// <summary>Confere o contrato comum de erro (HTTP-06) em uma resposta real.</summary>
public static class ProblemAssert
{
    private static readonly string[] Fields = ["type", "title", "status", "detail", "instance", "errorCode", "traceId", "errors"];

    public static async Task<JsonElement> HasProblemAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode, string instance)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement.Clone();
        Assert.Equal(Fields.Order(StringComparer.Ordinal), body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal($"urn:cicclo:problem:{errorCode.ToLowerInvariant().Replace('_', '-')}", body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        Assert.Equal(instance, body.GetProperty("instance").GetString());
        Assert.Equal(errorCode, body.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        return body;
    }
}
