using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.OpenApi;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Contracts.Laundry;

/// <summary>
/// POST /api/v1/simulation/machines/{machineId}/occupy sobre PostgreSQL 18.1 real (SIM-01/08/10, FIN-12 base, QUEUE-07
/// regressão): ocupação sintética sem débito, sem furar fila, disputando a mesma serialização da ativação, e rota existente
/// somente em Development com Laundry:DemoMode=true. HTTP e banco conferidos juntos.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E06")]
[Trait("Task", "T2")]
public sealed class OccupyMachineTests(PostgresFixture postgres)
{
    internal const string Route = "/api/v1/simulation/machines/{machineId}/occupy";
    private const string ApiApp = "cicclo-e06-api";
    private const string HolderApp = "cicclo-e06-holder";
    private static readonly string WalletLock = $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE";

    /// <summary>
    /// Simulação ligada e worker de reconciliação com intervalo de 1 h: avançar o relógio do teste em segundos não dispara
    /// o worker, então toda promoção observada vem do próprio comando, na mesma transação.
    /// </summary>
    internal static readonly Dictionary<string, string> Demo = new()
    {
        ["Laundry:DemoMode"] = "true",
        ["Laundry:QueueWorkerIntervalSeconds"] = "3600",
        ["RateLimiting:ItemCommands:PermitLimit"] = "1000",
        ["RateLimiting:BookingReads:PermitLimit"] = "1000",
    };

    /// <summary>Máquinas por nome: nome|estado|item|sintética|updated_at.</summary>
    internal const string MachinesSql =
        "SELECT name || '|' || state || '|' || coalesce(active_item_id::text, 'null') || '|' || is_demo_occupied || '|' || to_char(updated_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM machines ORDER BY name";

    /// <summary>Saldo, lançamentos e compras: a simulação nunca cria nenhum deles.</summary>
    internal const string MoneySql =
        "SELECT (SELECT balance::text FROM wallets) || '|' || (SELECT count(*) FROM wallet_transactions) || '|' || (SELECT count(*) FROM service_requests)";

    [Fact]
    public async Task O01_IdleMachineBecomesSyntheticRunningWithoutBookingDebitOrWallet()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock(T0.AddSeconds(5));
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();

        // Corpo com walletId/estado é ignorado: não há seletor de conta nem pseudo-autorização.
        using var response = await client.PostAsJsonAsync(Path(WasherId), new { walletId = Guid.NewGuid(), state = "available", isDemoOccupied = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal(["machine", "item"], body.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("item").ValueKind);
        Assert.Equal($"{WasherId}|washer-01|washer|running|null|True", Describe(body.GetProperty("machine")));
        Assert.Equal(["dryer-01|available|null|false|" + await UpdatedAtAsync(database, DryerId), "washer-01|running|null|true|12:00:05.000000"], await database.RowsAsync(MachinesSql));
        Assert.Equal(["50.00|0|0"], await database.RowsAsync(MoneySql));
        var machines = await BookingApi.JsonAsync(await client.GetAsync("/api/v1/machines"));
        Assert.Equal(WasherId, machines.GetProperty("machines")[1].GetProperty("machineId").GetString());
        Assert.True(machines.GetProperty("machines")[1].GetProperty("isDemoOccupied").GetBoolean());
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), body.GetRawText());
    }

    // SIM-10: um item que entrou na fila antes tem prioridade sobre a máquina que acaba de ficar livre.
    [Fact]
    public async Task O02_EarlierQueueRefusesTheOccupationWith409WithoutMutation()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{WasherId}'");
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        await database.ExecuteAsync($"UPDATE machines SET state = 'available', is_demo_occupied = false WHERE id = '{WasherId}'");
        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(5));
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(Path(WasherId), null);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(WasherId));
        Assert.Equal(
            "A máquina não está livre ou há itens na fila do seu tipo; a ocupação sintética não fura a fila e nada foi alterado.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains($"item|{wash}|queued|1|12:00:00.000000|null|null|null|null", before);
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "409", "application/problem+json", "post"), problem.GetRawText());
    }

    [Theory]
    [InlineData("reserved")]
    [InlineData("running real")]
    [InlineData("running synthetic")]
    [InlineData("out_of_service")]
    public async Task O03_MachineThatIsNotIdleIsRefusedWith409WithoutMutation(string state)
    {
        var database = await ReadyAsync(postgres, state switch
        {
            "running synthetic" => $"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{WasherId}'",
            "out_of_service" => $"UPDATE machines SET state = 'out_of_service' WHERE id = '{WasherId}'",
            _ => null,
        });
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        if (state.StartsWith("r", StringComparison.Ordinal) && state != "running synthetic")
        {
            var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
            if (state == "running real")
            {
                using var confirmed = await client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{booking.Items[0].ItemId}/confirm", null);
                Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            }
        }

        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(5));

        using var response = await client.PostAsync(Path(WasherId), null);

        await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(WasherId));
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains(before, row => row.StartsWith($"washer-01|{state.Split(' ')[0]}|", StringComparison.Ordinal));
    }

    // Ocupação e ativação do mesmo tipo disputam o lock da wallet: em qualquer ordem só uma toma a máquina livre e a
    // outra vê o resultado (fila ou recusa), nunca máquina sintética e reservada ao mesmo tempo.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task O04_OccupyAndActivateOnTheSameIdleMachineHaveASingleWinner(bool occupyFirst)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var wash = booking.Items.Single(item => item.ServiceCode == "wash").ItemId;
        clock.SetUtcNow(T0.AddSeconds(5));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Demo);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);
        Task<HttpResponseMessage> Occupy() => client.PostAsync(Path(WasherId), null);
        Task<HttpResponseMessage> Activate() => client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{wash}/activate", null);

        var firstTask = occupyFirst ? Occupy() : Activate();
        await WaitUntilWaitingOnLockAsync(database, ApiApp);
        var secondTask = occupyFirst ? Activate() : Occupy();
        await WaitUntilWaitingOnLockAsync(database, ApiApp, count: 2);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var firstResponse = await firstTask.WaitAsync(SignalTimeout);
        using var secondResponse = await secondTask.WaitAsync(SignalTimeout);
        var (occupy, activate) = occupyFirst ? (firstResponse, secondResponse) : (secondResponse, firstResponse);

        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        var item = await BookingApi.JsonAsync(activate);
        var rows = await database.RowsAsync(StateSql);
        var machines = await database.RowsAsync(MachinesSql);
        if (occupyFirst)
        {
            Assert.Equal(HttpStatusCode.OK, occupy.StatusCode);
            Assert.Equal(("queued", 1), (item.GetProperty("status").GetString(), item.GetProperty("queuePosition").GetInt32()));
            Assert.Contains($"item|{wash}|queued|1|12:00:05.000000|null|null|null|null", rows);
            Assert.Contains("washer-01|running|null|true|12:00:05.000000", machines);
        }
        else
        {
            await ProblemAssert.HasProblemAsync(occupy, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(WasherId));
            Assert.Equal(("to_be_confirmed", WasherId), (item.GetProperty("status").GetString(), item.GetProperty("machineId").GetString()));
            Assert.Contains($"item|{wash}|to_be_confirmed|1|12:00:05.000000|{WasherId}|12:00:05.000000|12:01:05.000000|null", rows);
            Assert.Contains($"washer-01|reserved|{wash}|false|12:00:05.000000", machines);
        }

        // Só o débito da compra original: nem a ocupação nem a ativação cobram.
        Assert.Equal(["10.20|1|1"], await database.RowsAsync(MoneySql));
    }

    [Fact]
    public async Task O05_InvalidOrUnknownMachineIdsAreRejectedWithoutMutation()
    {
        var database = await ReadyAsync(postgres);
        var before = await SnapshotAsync(database);
        await using var factory = BookingApi.Create(database, Clock(), Demo);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        using var invalid = await client.PostAsync("/api/v1/simulation/machines/x/occupy", null);
        using var missing = await client.PostAsync(Path(unknown.ToString()), null);

        await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, "/api/v1/simulation/machines/x/occupy");
        var problem = await ProblemAssert.HasProblemAsync(missing, HttpStatusCode.NotFound, ApiProblems.MachineNotFound, Path(unknown.ToString()));
        Assert.Equal("Nenhuma máquina com esse identificador; nada foi alterado.", problem.GetProperty("detail").GetString());
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "404", "application/problem+json", "post"), problem.GetRawText());
        Assert.Equal(before, await SnapshotAsync(database));
    }

    // SIM-08: a rota só existe na conjunção Development AND DemoMode=true; fora dela é 404 sem efeito e some do OpenAPI.
    [Theory]
    [InlineData("Development", "true")]
    [InlineData("Development", "false")]
    [InlineData("Production", "true")]
    [InlineData("Production", "false")]
    public async Task O06_RouteExistsOnlyInDevelopmentWithDemoModeAndIsDocumentedOnlyThere(string environment, string demoMode)
    {
        var database = await ReadyAsync(postgres);
        var before = await SnapshotAsync(database);
        await using var factory = Host(database, environment, demoMode);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(Path(WasherId), null);
        using var document = await client.GetAsync(ApiOpenApi.DocumentPath);

        if (environment == "Development" && demoMode == "true")
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("washer-01|running|null|true|", (await database.RowsAsync(MachinesSql))[1], StringComparison.Ordinal);
            var openApi = await document.Content.ReadAsStringAsync();
            Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "POST", "OccupyMachine")));
            var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Post];
            Assert.Equal(["Simulation"], operation.Tags!.Select(tag => tag.Name));
            Assert.Equal(["200", "400", "404", "409", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
            Assert.Null(operation.RequestBody);
            var parameter = Assert.Single(operation.Parameters!);
            Assert.Equal(("machineId", "uuid", ParameterLocation.Path), (parameter.Name, parameter.Schema!.Format, parameter.In!.Value));
            JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), JsonSchemaContract.Example(openApi, Route, "200", method: "post"));
            return;
        }

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(database));
        if (environment == "Development")
        {
            Assert.Equal(HttpStatusCode.OK, document.StatusCode);
            var openApi = await document.Content.ReadAsStringAsync();
            Assert.DoesNotContain("simulation", openApi, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("occupy", openApi, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        }
    }

    internal static string Path(string machineId) => $"/api/v1/simulation/machines/{machineId}/occupy";

    internal static WebApplicationFactory<Program> Host(TestDatabase database, string environment, string demoMode) =>
        BookingApi.Create(database, Clock(), new Dictionary<string, string> { ["Laundry:DemoMode"] = demoMode })
            .WithWebHostBuilder(host => host.UseEnvironment(environment));

    /// <summary>Itens/máquinas/wallet (StateSql), marca sintética das máquinas e lançamentos.</summary>
    internal static async Task<IReadOnlyList<string>> SnapshotAsync(TestDatabase database) =>
        [
            .. await database.RowsAsync(StateSql),
            .. await database.RowsAsync(MachinesSql),
            .. await database.RowsAsync("SELECT kind || '|' || amount || '|' || balance_after FROM wallet_transactions ORDER BY created_at"),
        ];

    internal static string Describe(JsonElement machine) =>
        string.Join(
            '|',
            machine.GetProperty("machineId").GetString(),
            machine.GetProperty("name").GetString(),
            machine.GetProperty("machineType").GetString(),
            machine.GetProperty("state").GetString(),
            machine.GetProperty("activeItemId").ValueKind == JsonValueKind.Null ? "null" : machine.GetProperty("activeItemId").GetString(),
            machine.GetProperty("isDemoOccupied").GetBoolean() ? "True" : "False");

    private static Task<string> UpdatedAtAsync(TestDatabase database, string machineId) =>
        database.ScalarAsync<string>($"SELECT to_char(updated_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM machines WHERE id = '{machineId}'");
}
