using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Services;

[Collection(PostgresCollection.Name)]
[Trait("Increment", "E01")]
[Trait("Task", "T7")]
public sealed class ServicesEndpointTests(PostgresFixture postgres)
{
    private const string Path = "/api/v1/services";

    private static readonly string[] ItemFields =
        ["code", "name", "machineType", "unitPriceCents", "currency", "isActive", "canBeBooked", "unavailabilityReason"];

    [Fact]
    public async Task C01_SeedReturnsBothServicesWithPricesTypesAndBookable()
    {
        var database = await SeededAsync();

        var services = await CatalogAsync(database);

        Assert.Equal(
            [
                "dry|Secagem|dryer|2090|BRL|True|True|null",
                "wash|Lavagem|washer|1890|BRL|True|True|null",
            ],
            services.Select(Describe));
        Assert.All(services, item => Assert.Equal(ItemFields, item.EnumerateObject().Select(property => property.Name)));
        Assert.All(services, item => Assert.Equal(JsonValueKind.Number, item.GetProperty("unitPriceCents").ValueKind));
    }

    [Fact]
    public async Task C02_ChangedWashPriceIsReturnedInCents()
    {
        var database = await SeededAsync("UPDATE services SET current_price = 25.00 WHERE code = 'wash';");

        var wash = (await CatalogAsync(database)).Single(item => item.GetProperty("code").GetString() == "wash");

        Assert.Equal(2500L, wash.GetProperty("unitPriceCents").GetInt64());
    }

    [Theory]
    [InlineData("UPDATE services SET is_active = false WHERE code = 'wash';")]
    [InlineData("UPDATE services SET is_active = false WHERE code = 'wash'; UPDATE machines SET state = 'out_of_service' WHERE machine_type = 'washer';")]
    public async Task C03_InactiveServiceIsDisabledEvenWithoutCapacity(string fixture)
    {
        var database = await SeededAsync(fixture);

        var services = await CatalogAsync(database);

        Assert.Equal(
            [
                "dry|Secagem|dryer|2090|BRL|True|True|null",
                "wash|Lavagem|washer|1890|BRL|False|False|SERVICE_DISABLED",
            ],
            services.Select(Describe));
    }

    [Fact]
    public async Task C04_AllMachinesOfTheTypeOutOfServiceMakesTheServiceUnavailable()
    {
        var database = await SeededAsync(
            """
            INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES (gen_random_uuid(), 'dryer-02', 'dryer', 'out_of_service', now());
            UPDATE machines SET state = 'out_of_service' WHERE machine_type = 'dryer';
            """);

        var services = await CatalogAsync(database);

        Assert.Equal(
            [
                "dry|Secagem|dryer|2090|BRL|True|False|NO_OPERATIONAL_MACHINE",
                "wash|Lavagem|washer|1890|BRL|True|True|null",
            ],
            services.Select(Describe));
    }

    [Fact]
    public async Task C05_NoMachineOfTheTypeMakesTheServiceUnavailable()
    {
        var database = await SeededAsync($"DELETE FROM machines WHERE id = '{DemoData.WasherId}';");

        var wash = (await CatalogAsync(database)).Single(item => item.GetProperty("code").GetString() == "wash");

        Assert.Equal("wash|Lavagem|washer|1890|BRL|True|False|NO_OPERATIONAL_MACHINE", Describe(wash));
    }

    [Theory]
    [InlineData("reserved")]
    [InlineData("running")]
    public async Task C06_ReservedOrRunningMachinesKeepTheServiceBookable(string state)
    {
        var database = await SeededAsync($"UPDATE machines SET state = '{state}';");

        var services = await CatalogAsync(database);

        Assert.All(services, item => Assert.True(item.GetProperty("canBeBooked").GetBoolean()));
        Assert.All(services, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("unavailabilityReason").ValueKind));
    }

    [Fact]
    public async Task C08_CatalogUsesOneQueryAndWritesNothing()
    {
        var database = await SeededAsync();
        const string versions =
            "SELECT (SELECT string_agg(xmin::text, ',' ORDER BY code) FROM services) || '/' || (SELECT string_agg(xmin::text, ',' ORDER BY id) FROM machines)";
        var before = await database.ScalarAsync<string>(versions);
        var logs = new CapturingLoggerProvider();
        await using var factory = ApiFactory.Create(database.ConnectionString)
            .WithWebHostBuilder(host => host.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient();
        var baseline = Commands(logs).Count;
        using var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var command = Assert.Single(Commands(logs).Skip(baseline));
        Assert.Contains("EXISTS", command, StringComparison.Ordinal);
        foreach (var write in new[] { "INSERT", "UPDATE", "DELETE", "FOR UPDATE" })
        {
            Assert.DoesNotContain(write, command, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, await database.ScalarAsync<string>(versions));
    }

    [Theory]
    [InlineData("down")]
    [InlineData("missing-connection")]
    [InlineData("without-seed")]
    public async Task C09_DatabaseFailuresReturn503(string mode)
    {
        string? connection = mode switch
        {
            "down" => ApiFactory.UnreachableDatabase,
            "missing-connection" => null,
            _ => (await MigratedAsync()).ConnectionString,
        };
        await using var factory = ApiFactory.Create(connection);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        var body = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path);
        Assert.DoesNotContain("nao-vazar-down", body.GetRawText());
    }

    [Fact]
    public async Task C09_UnexpectedFailureReturnsSanitized500()
    {
        var database = await SeededAsync("ALTER TABLE services RENAME COLUMN current_price TO price_renamed;");
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        var raw = (await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, Path)).GetRawText();
        foreach (var leak in new[] { "current_price", "column", "SELECT", "EXISTS", "42703", "Npgsql" })
        {
            Assert.DoesNotContain(leak, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task C11_WalletAndServicesShareTheReadLimitAndForwardedHeadersDoNotEscapeIt()
    {
        var database = await SeededAsync();
        var remote = new RemoteIpStartupFilter();
        await using var factory = LimitedFactory(database, remote);
        using var client = factory.CreateClient();

        using var wallet = await Send(client, "/api/v1/wallet", "203.0.113.10", forwardedFor: "198.51.100.1");
        using var services = await Send(client, Path, "203.0.113.10", forwardedFor: "198.51.100.2");
        using var blocked = await Send(client, Path, "203.0.113.10", forwardedFor: "198.51.100.3");
        using var otherIp = await Send(client, Path, "203.0.113.20", forwardedFor: null);

        Assert.Equal(HttpStatusCode.OK, wallet.StatusCode);
        Assert.Equal(HttpStatusCode.OK, services.StatusCode);
        await ProblemAssert.HasProblemAsync(blocked, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, Path);
        Assert.True(blocked.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, otherIp.StatusCode);
    }

    [Fact]
    public async Task C12_HealthStaysAvailableAfterExhaustionAndUnknownPartitionIsConsistent()
    {
        var database = await SeededAsync();
        var remote = new RemoteIpStartupFilter();
        await using var factory = LimitedFactory(database, remote);
        using var client = factory.CreateClient();

        using var first = await client.GetAsync(Path);
        using var second = await client.GetAsync("/api/v1/wallet");
        using var third = await client.GetAsync(Path);

        Assert.All(remote.Observed, Assert.Null);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], new[] { first.StatusCode, second.StatusCode, third.StatusCode });
        foreach (var health in new[] { "/health", "/health/live", "/health/ready" })
        {
            using var response = await client.GetAsync(health);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var fourth = await client.GetAsync("/api/v1/wallet");
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
    }

    private static string Describe(JsonElement item) =>
        string.Join(
            '|',
            item.GetProperty("code").GetString(),
            item.GetProperty("name").GetString(),
            item.GetProperty("machineType").GetString(),
            item.GetProperty("unitPriceCents").GetInt64(),
            item.GetProperty("currency").GetString(),
            item.GetProperty("isActive").GetBoolean(),
            item.GetProperty("canBeBooked").GetBoolean(),
            item.GetProperty("unavailabilityReason").ValueKind == JsonValueKind.Null ? "null" : item.GetProperty("unavailabilityReason").GetString());

    private static List<string> Commands(CapturingLoggerProvider logs) =>
        [.. logs.Entries.Where(log => log.Category == "Microsoft.EntityFrameworkCore.Database.Command" && log.EventId.Name == "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted").Select(log => log.Message)];

    private static WebApplicationFactory<Program> LimitedFactory(TestDatabase database, RemoteIpStartupFilter remote) =>
        ApiFactory.Create(database.ConnectionString, new Dictionary<string, string> { ["RateLimiting:Reads:PermitLimit"] = "2" })
            .WithWebHostBuilder(host => host.ConfigureServices(services => services.AddSingleton<IStartupFilter>(remote)));

    private static Task<HttpResponseMessage> Send(HttpClient client, string path, string remoteIp, string? forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(RemoteIpStartupFilter.Header, remoteIp);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return client.SendAsync(request);
    }

    private async Task<TestDatabase> SeededAsync(string? fixture = null)
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        if (fixture is not null)
        {
            await database.ExecuteAsync(fixture);
        }

        return database;
    }

    private async Task<TestDatabase> MigratedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync();
        return database;
    }

    private static async Task<List<JsonElement>> CatalogAsync(TestDatabase database)
    {
        await using var factory = ApiFactory.Create(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["services"], body.RootElement.EnumerateObject().Select(property => property.Name));
        return [.. body.RootElement.GetProperty("services").EnumerateArray().Select(item => item.Clone())];
    }
}
