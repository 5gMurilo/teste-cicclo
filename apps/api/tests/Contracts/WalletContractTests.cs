using Cicclo.Api.Tests.Support;
using Microsoft.OpenApi;
using Xunit;

namespace Cicclo.Api.Tests.Contracts;

[Collection(PostgresCollection.Name)]
[Trait("Increment", "E01")]
[Trait("Task", "T6")]
public sealed class WalletContractTests(PostgresFixture postgres)
{
    private const string Path = "/api/v1/wallet";

    [Fact]
    public async Task W08_RealBodiesAndExampleMatchTheGeneratedSchemas()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await using var factory = ApiFactory.Create(database.ConnectionString, new Dictionary<string, string> { ["RateLimiting:Reads:PermitLimit"] = "1" });
        using var client = factory.CreateClient();
        var openApi = await OpenApiContract.FetchAsync(client);
        using var ok = await client.GetAsync(Path);
        using var limited = await client.GetAsync(Path);
        await using var downFactory = ApiFactory.Create(ApiFactory.UnreachableDatabase);
        using var unavailable = await downFactory.CreateClient().GetAsync(Path);

        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Path, "GET", "GetWallet")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Path].Operations![HttpMethod.Get];
        Assert.Equal(["Wallet"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.True(operation.Responses["429"].Headers!["Retry-After"].Required);
        Assert.Empty(operation.Parameters ?? []);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), await ok.Content.ReadAsStringAsync());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), JsonSchemaContract.Example(openApi, Path, "200"));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "429", "application/problem+json"), await limited.Content.ReadAsStringAsync());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "503", "application/problem+json"), await unavailable.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{"balanceCents":"5000","currency":"BRL","updatedAt":"2026-10-21T12:00:00Z"}""")]
    [InlineData("""{"currency":"BRL","updatedAt":"2026-10-21T12:00:00Z"}""")]
    [InlineData("""{"balanceCents":-1,"currency":"BRL","updatedAt":"2026-10-21T12:00:00Z"}""")]
    [InlineData("""{"balanceCents":5000.5,"currency":"BRL","updatedAt":"2026-10-21T12:00:00Z"}""")]
    [InlineData("""{"balanceCents":5000,"currency":"BRL","updatedAt":"ontem"}""")]
    [InlineData("""{"balanceCents":5000,"currency":"BRL","updatedAt":"2026-10-21T12:00:00Z","walletId":"x"}""")]
    public async Task W08_HelperRejectsBodiesThatBreakTheGeneratedSchema(string body)
    {
        await using var factory = ApiFactory.Create(connectionString: null);
        var openApi = await OpenApiContract.FetchAsync(factory.CreateClient());

        JsonSchemaContract.AssertInvalid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), body);
    }
}
