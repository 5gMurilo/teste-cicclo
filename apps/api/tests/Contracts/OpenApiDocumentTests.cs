using System.Net;
using System.Text.Json.Nodes;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.OpenApi;
using Xunit;

namespace Cicclo.Api.Tests.Contracts;

[Trait("Increment", "E01")]
[Trait("Task", "T4")]
public sealed class OpenApiDocumentTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly RequiredOperation Health = new("/health", "GET", "GetHealth");

    [Fact]
    public async Task O01_DevelopmentDocumentIsReadByTheOfficialParserAndDescribesHealth()
    {
        using var client = Development().CreateClient();

        var json = await OpenApiContract.FetchAsync(client);

        Assert.Empty(OpenApiContract.Problems(json, Health));
        var document = OpenApiContract.Read(json).Document!;
        Assert.Equal("Cicclo API", document.Info.Title);
        Assert.Equal("v1", document.Info.Version);
        var operation = document.Paths["/health"].Operations![HttpMethod.Get];
        Assert.Equal(["Health"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200"], operation.Responses!.Keys);
        var content = operation.Responses["200"].Content!;
        Assert.Equal(["text/plain"], content.Keys);
        Assert.Equal(JsonSchemaType.String, content["text/plain"].Schema!.Type);
    }

    [Fact]
    public async Task O02_ProductionDoesNotPublishTheDocument()
    {
        using var client = factory.WithWebHostBuilder(host => host.UseEnvironment("Production")).CreateClient();

        using var response = await client.GetAsync(ApiOpenApi.DocumentPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task O03_DocumentListsOnlyDeliveredOperations()
    {
        using var client = Development().CreateClient();

        var json = await OpenApiContract.FetchAsync(client);

        var document = OpenApiContract.Read(json).Document!;
        Assert.Equal(
            ["/api/v1/bookings: GET,POST", "/api/v1/bookings/{bookingId}: GET", "/api/v1/bookings/{bookingId}/items/{itemId}/activate: POST", "/api/v1/services: GET", "/api/v1/wallet: GET", "/api/v1/wallet/transactions: GET", "/health: GET", "/health/live: GET", "/health/ready: GET"],
            document.Paths.OrderBy(path => path.Key, StringComparer.Ordinal).Select(path => $"{path.Key}: {string.Join(',', path.Value.Operations!.Keys.Select(method => method.Method))}"));
        foreach (var future in new[] { "simulation", "machines", "/confirm", "cancel", "refund" })
        {
            Assert.DoesNotContain(future, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task O04_HelperRejectsACopyWithoutARequiredOperation()
    {
        using var client = Development().CreateClient();
        var copy = JsonNode.Parse(await OpenApiContract.FetchAsync(client))!;

        copy["paths"]!.AsObject().Remove("/health");

        Assert.Contains("operação ausente: GET /health (GetHealth)", OpenApiContract.Problems(copy.ToJsonString(), Health));
    }

    [Fact]
    public async Task O05_HelperRejectsDuplicatedOperationIds()
    {
        using var client = Development().CreateClient();
        var copy = JsonNode.Parse(await OpenApiContract.FetchAsync(client))!;

        copy["paths"]!["/health-copy"] = copy["paths"]!["/health"]!.DeepClone();

        Assert.Contains("operationId duplicado: GetHealth", OpenApiContract.Problems(copy.ToJsonString(), Health));
    }

    private WebApplicationFactory<Program> Development() =>
        factory.WithWebHostBuilder(host => host.UseEnvironment("Development"));
}
