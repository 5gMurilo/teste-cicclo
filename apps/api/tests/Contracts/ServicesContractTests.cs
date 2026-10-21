using Cicclo.Api.Tests.Support;
using Microsoft.OpenApi;
using Xunit;

namespace Cicclo.Api.Tests.Contracts;

[Collection(PostgresCollection.Name)]
[Trait("Increment", "E01")]
[Trait("Task", "T7")]
public sealed class ServicesContractTests(PostgresFixture postgres)
{
    private const string Path = "/api/v1/services";

    [Fact]
    public async Task C10_RealBodiesExampleAndNullsMatchTheGeneratedSchemas()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();
        var openApi = await OpenApiContract.FetchAsync(client);
        using var bookable = await client.GetAsync(Path);
        await database.ExecuteAsync("UPDATE services SET is_active = false WHERE code = 'wash'; UPDATE machines SET state = 'out_of_service' WHERE machine_type = 'dryer';");
        using var unavailable = await client.GetAsync(Path);
        await using var downFactory = ApiFactory.Create(ApiFactory.UnreachableDatabase);
        using var down = await downFactory.CreateClient().GetAsync(Path);

        Assert.Empty(OpenApiContract.Problems(
            openApi,
            new RequiredOperation(Path, "GET", "GetServices"),
            new RequiredOperation("/api/v1/wallet", "GET", "GetWallet")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Path].Operations![HttpMethod.Get];
        Assert.Equal(["Services"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.True(operation.Responses["429"].Headers!["Retry-After"].Required);
        var schema = JsonSchemaContract.ResponsePointer(Path, "200");
        JsonSchemaContract.AssertValid(openApi, schema, await bookable.Content.ReadAsStringAsync());
        var unavailableBody = await unavailable.Content.ReadAsStringAsync();
        Assert.Contains("\"unavailabilityReason\":\"SERVICE_DISABLED\"", unavailableBody);
        Assert.Contains("\"unavailabilityReason\":\"NO_OPERATIONAL_MACHINE\"", unavailableBody);
        JsonSchemaContract.AssertValid(openApi, schema, unavailableBody);
        JsonSchemaContract.AssertValid(openApi, schema, JsonSchemaContract.Example(openApi, Path, "200"));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "503", "application/problem+json"), await down.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{"services":[{"code":"wash","name":"Lavagem","machineType":"washer","unitPriceCents":1890,"currency":"BRL","isActive":true,"canBeBooked":true,"unavailabilityReason":"BROKEN"}]}""")]
    [InlineData("""{"services":[{"code":"wash","name":"Lavagem","machineType":"washer","unitPriceCents":"1890","currency":"BRL","isActive":true,"canBeBooked":true,"unavailabilityReason":null}]}""")]
    [InlineData("""{"services":[{"code":"wash","name":"Lavagem","machineType":"washer","unitPriceCents":1890,"currency":"BRL","isActive":true,"canBeBooked":true}]}""")]
    [InlineData("""{"services":[{"code":"wash","name":"Lavagem","machineType":"ironer","unitPriceCents":1890,"currency":"BRL","isActive":true,"canBeBooked":true,"unavailabilityReason":null}]}""")]
    [InlineData("""{"services":[{"code":"wash","name":"Lavagem","machineType":"washer","unitPriceCents":0,"currency":"BRL","isActive":true,"canBeBooked":true,"unavailabilityReason":null}]}""")]
    [InlineData("""[{"code":"wash"}]""")]
    public async Task C10_HelperRejectsBodiesThatBreakTheGeneratedSchema(string body)
    {
        await using var factory = ApiFactory.Create(connectionString: null);
        var openApi = await OpenApiContract.FetchAsync(factory.CreateClient());

        JsonSchemaContract.AssertInvalid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), body);
    }
}
