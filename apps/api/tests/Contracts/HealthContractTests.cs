using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.OpenApi;
using Xunit;

namespace Cicclo.Api.Tests.Contracts;

[Trait("Increment", "E01")]
[Trait("Task", "T5")]
public sealed class HealthContractTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task H08_OpenApiDescribesLivenessAndReadinessTextResponses()
    {
        using var client = factory.CreateClient();

        var json = await OpenApiContract.FetchAsync(client);

        Assert.Empty(OpenApiContract.Problems(
            json,
            new("/health", "GET", "GetHealth"),
            new("/health/live", "GET", "GetLiveness"),
            new("/health/ready", "GET", "GetReadiness")));
        var paths = OpenApiContract.Read(json).Document!.Paths;
        Assert.Equal(["200"], paths["/health/live"].Operations![HttpMethod.Get].Responses!.Keys);
        var ready = paths["/health/ready"].Operations![HttpMethod.Get].Responses!;
        Assert.Equal(["200", "503"], ready.Keys.Order(StringComparer.Ordinal));
        Assert.All(ready.Values, response =>
        {
            Assert.Equal(["text/plain"], response.Content!.Keys);
            Assert.Equal(JsonSchemaType.String, response.Content["text/plain"].Schema!.Type);
        });
    }
}
