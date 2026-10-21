using System.Net;
using Cicclo.Api.Tests.Support;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Health;

[Collection(PostgresCollection.Name)]
[Trait("Increment", "E01")]
[Trait("Task", "T5")]
public sealed class ReadinessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task H01_AliasAndLivenessKeepPlainHealthy()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        await AssertHealthAsync(client, "/health", HttpStatusCode.OK, "Healthy");
        await AssertHealthAsync(client, "/health/live", HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task H02_DatabaseWithoutTablesIsNotReady()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        await AssertHealthAsync(client, "/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy");
        await AssertHealthAsync(client, "/health/live", HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task H03_SchemaWithoutSeedIsNotReady()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync();
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        await AssertHealthAsync(client, "/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy");
    }

    [Fact]
    public async Task H04_BootstrappedDatabaseIsReady()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        await AssertHealthAsync(client, "/health/ready", HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task H05_DatabaseDownMakesReadyFailButKeepsLiveness()
    {
        await using var factory = ApiFactory.Create(ApiFactory.UnreachableDatabase);
        using var client = factory.CreateClient();

        await AssertHealthAsync(client, "/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy");
        await AssertHealthAsync(client, "/health/live", HttpStatusCode.OK, "Healthy");
        await AssertHealthAsync(client, "/health", HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task H06_MissingConnectionKeepsProcessAliveAndNotReady()
    {
        await using var factory = ApiFactory.Create(connectionString: null);
        using var client = factory.CreateClient();

        await AssertHealthAsync(client, "/health/live", HttpStatusCode.OK, "Healthy");
        await AssertHealthAsync(client, "/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy");
    }

    [Fact]
    public async Task H07_UsedWalletChangedPriceAndUnavailableMachinesStayReady()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await database.ExecuteAsync(
            """
            UPDATE wallets SET balance = 0.00;
            UPDATE services SET current_price = 25.00 WHERE code = 'wash';
            UPDATE services SET is_active = false WHERE code = 'dry';
            UPDATE machines SET state = 'out_of_service';
            """);
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        await AssertHealthAsync(client, "/health/ready", HttpStatusCode.OK, "Healthy");
    }

    private static async Task AssertHealthAsync(HttpClient client, string path, HttpStatusCode status, string body)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }
}
