using System.Globalization;
using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Contracts.Laundry;

/// <summary>
/// GET /api/v1/machines sobre PostgreSQL 18.1 real (SIM-01/02/09 base de projeção, HTTP-06..08, OPS-04): estado persistido
/// de cada máquina, item real em oferta/execução distinto da ocupação sintética, constraints do vínculo e leitura sem escrita.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E06")]
[Trait("Task", "T1")]
public sealed class GetMachinesTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/machines";
    private static readonly string[] MachineFields = ["machineId", "name", "machineType", "state", "activeItemId", "isDemoOccupied"];

    /// <summary>Máquinas por nome: nome|tipo|estado|item|sintética.</summary>
    private const string MachinesSql =
        "SELECT name || '|' || machine_type || '|' || state || '|' || coalesce(active_item_id::text, 'null') || '|' || is_demo_occupied FROM machines ORDER BY name";

    /// <summary>Versão de linha de máquinas, itens e wallet: qualquer escrita, mesmo sem mudar valor, altera o xmin.</summary>
    private const string VersionsSql =
        "SELECT coalesce((SELECT string_agg(xmin::text, ',' ORDER BY id) FROM machines), '') || '/' || coalesce((SELECT string_agg(xmin::text, ',' ORDER BY id) FROM service_request_items), '') || '/' || (SELECT string_agg(xmin::text, ',') FROM wallets)";

    [Fact]
    public async Task M01_SeedMachinesAreAvailableWithoutAssignmentAndTheReadWritesNothing()
    {
        var database = await ReadyAsync(postgres);
        var before = await database.ScalarAsync<string>(VersionsSql);
        await using var factory = BookingApi.Create(database, Clock());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal(["machines"], body.EnumerateObject().Select(property => property.Name));
        var machines = body.GetProperty("machines").EnumerateArray().ToList();
        Assert.All(machines, machine => Assert.Equal(MachineFields, machine.EnumerateObject().Select(property => property.Name)));
        Assert.Equal(
            [$"{DryerId}|dryer-01|dryer|available|null|False", $"{WasherId}|washer-01|washer|available|null|False"],
            machines.Select(Describe));
        Assert.Equal(before, await database.ScalarAsync<string>(VersionsSql));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200"), body.GetRawText());
    }

    [Fact]
    public async Task M02_OfferedMachineShowsTheRealItemItIsReservedFor()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        await using var factory = BookingApi.Create(database, clock);
        using var client = factory.CreateClient();

        var machines = await MachinesAsync(client);

        Assert.Equal(
            [$"{DryerId}|dryer-01|dryer|available|null|False", $"{WasherId}|washer-01|washer|reserved|{wash}|False"],
            machines.Select(Describe));
        var detail = await BookingApi.JsonAsync(await client.GetAsync($"/api/v1/bookings/{booking.BookingId}"));
        Assert.Equal(
            ("to_be_confirmed", Guid.Parse(WasherId)),
            (detail.GetProperty("items")[0].GetProperty("status").GetString(), detail.GetProperty("items")[0].GetProperty("machineId").GetGuid()));
    }

    [Fact]
    public async Task M03_RealExecutionIsRunningWithItsInProgressItemAndOutOfServiceHasNoItem()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'out_of_service' WHERE id = '{DryerId}'");
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(10));
        await using var factory = BookingApi.Create(database, clock);
        using var client = factory.CreateClient();
        using (var confirmed = await client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{wash}/confirm", null))
        {
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        }

        var machines = await MachinesAsync(client);

        Assert.Equal(
            [$"{DryerId}|dryer-01|dryer|out_of_service|null|False", $"{WasherId}|washer-01|washer|running|{wash}|False"],
            machines.Select(Describe));
        Assert.Contains($"item|{wash}|in_progress|", (await database.RowsAsync(StateSql))[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task M04_SyntheticOccupationIsRunningWithoutItemBookingOrWallet()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{DryerId}'");
        await using var factory = BookingApi.Create(database, Clock());
        using var client = factory.CreateClient();

        var machines = await MachinesAsync(client);

        Assert.Equal(
            [$"{DryerId}|dryer-01|dryer|running|null|True", $"{WasherId}|washer-01|washer|available|null|False"],
            machines.Select(Describe));
        Assert.Equal(
            "50.00|0|0|0",
            await database.ScalarAsync<string>(
                "SELECT (SELECT balance::text FROM wallets) || '|' || (SELECT count(*) FROM wallet_transactions) || '|' || (SELECT count(*) FROM service_requests) || '|' || (SELECT count(*) FROM service_request_items)"));
    }

    [Theory]
    [InlineData("running without item nor synthetic mark", $"UPDATE machines SET state = 'running' WHERE id = '{WasherId}'")]
    [InlineData("synthetic mark on an available machine", $"UPDATE machines SET is_demo_occupied = true WHERE id = '{WasherId}'")]
    [InlineData("synthetic mark on an out of service machine", $"UPDATE machines SET state = 'out_of_service', is_demo_occupied = true WHERE id = '{WasherId}'")]
    [InlineData("synthetic mark on a machine reserved for an item", "UPDATE machines SET is_demo_occupied = true WHERE state = 'reserved'")]
    [InlineData("synthetic mark on a real execution", "UPDATE machines SET state = 'running', is_demo_occupied = true WHERE state = 'reserved'")]
    public async Task M05_IncoherentOccupancyIsRejectedByTheDatabaseWithoutChange(string scenario, string sql)
    {
        var database = await ReadyAsync(postgres);
        await BookingApi.PurchaseAsync(database, Clock(), ["dry"]);
        var before = await database.RowsAsync(MachinesSql);

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql));

        Assert.True(error.SqlState == PostgresErrorCodes.CheckViolation, scenario);
        Assert.Equal("ck_machines_demo_occupied", error.ConstraintName);
        Assert.Equal(before, await database.RowsAsync(MachinesSql));
    }

    // Antes desta migration, running sem item já era o único jeito de ocupar sem compra; a marca passa a ser explícita.
    [Fact]
    public async Task M06_MigrationMarksExistingItemlessRunningMachinesAsSyntheticWithoutChangingState()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.MigrateAsync("CancellationRefund");
        await database.SeedAsync();
        await database.ExecuteAsync($"UPDATE machines SET state = 'running', updated_at = '2026-10-21 09:00:00+00' WHERE id = '{DryerId}'");

        await database.MigrateAsync();

        Assert.Equal(["dryer-01|dryer|running|null|true", "washer-01|washer|available|null|false"], await database.RowsAsync(MachinesSql));
        Assert.Equal("09:00:00", await database.ScalarAsync<string>($"SELECT to_char(updated_at AT TIME ZONE 'UTC', 'HH24:MI:SS') FROM machines WHERE id = '{DryerId}'"));
        Assert.Equal(TestDatabase.MigrationIds, await database.RowsAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\""));
    }

    [Fact]
    public async Task M07_ContractSharedThirtyPerMinuteLimitAndDatabaseFailuresMatchTheSpec()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, new Dictionary<string, string> { ["RateLimiting:BookingReads:PermitLimit"] = "2" });
        using var client = factory.CreateClient();
        var openApi = await OpenApiContract.FetchAsync(client);

        using var ledger = await client.GetAsync("/api/v1/wallet/transactions");
        using var allowed = await client.GetAsync(Route);
        using var limited = await client.GetAsync(Route);
        await using var down = BookingApi.Create(new TestDatabase(ApiFactory.UnreachableDatabase), clock);
        using var unavailable = await down.CreateClient().GetAsync(Route);
        var migrated = await postgres.CreateDatabaseAsync();
        await migrated.MigrateAsync();
        await using var withoutSeed = BookingApi.Create(migrated, clock);
        using var notPrepared = await withoutSeed.CreateClient().GetAsync(Route);
        await using var defaults = BookingApi.Create(database, clock);

        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "GET", "GetMachines")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Get];
        Assert.Equal(["Machines"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.True(operation.Parameters is null or { Count: 0 });
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200"), JsonSchemaContract.Example(openApi, Route, "200"));
        var pointer = JsonSchemaContract.ResponsePointer(Route, "200");
        var valid = $$"""{"machines":[{"machineId":"{{WasherId}}","name":"washer-01","machineType":"washer","state":"out_of_service","activeItemId":null,"isDemoOccupied":false}]}""";
        JsonSchemaContract.AssertValid(openApi, pointer, valid);
        JsonSchemaContract.AssertInvalid(openApi, pointer, valid.Replace("out_of_service", "broken", StringComparison.Ordinal));
        JsonSchemaContract.AssertInvalid(openApi, pointer, valid.Replace(",\"isDemoOccupied\":false", "", StringComparison.Ordinal));
        JsonSchemaContract.AssertInvalid(openApi, pointer, valid.Replace("\"activeItemId\":null", "\"activeItemId\":\"x\"", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, ledger.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var problem = await ProblemAssert.HasProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, Route);
        Assert.InRange(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture), 1, 60);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "429", "application/problem+json"), problem.GetRawText());
        await ProblemAssert.HasProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Route);
        await ProblemAssert.HasProblemAsync(notPrepared, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Route);
        Assert.Equal(30, defaults.Services.GetRequiredService<IOptionsMonitor<RateLimitPolicyOptions>>().Get(ApiRateLimits.BookingReads).PermitLimit);
    }

    private static async Task<IReadOnlyList<JsonElement>> MachinesAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await BookingApi.JsonAsync(response)).GetProperty("machines").EnumerateArray()];
    }

    private static string Describe(JsonElement machine) =>
        string.Join(
            '|',
            machine.GetProperty("machineId").GetGuid(),
            machine.GetProperty("name").GetString(),
            machine.GetProperty("machineType").GetString(),
            machine.GetProperty("state").GetString(),
            machine.GetProperty("activeItemId").ValueKind == JsonValueKind.Null ? "null" : machine.GetProperty("activeItemId").GetGuid().ToString(),
            machine.GetProperty("isDemoOccupied").GetBoolean() ? "True" : "False");
}
