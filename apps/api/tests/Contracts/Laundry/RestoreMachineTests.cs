using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.Time.Testing;
using Microsoft.OpenApi;
using Xunit;
using static Cicclo.Api.Tests.Contracts.Laundry.OccupyMachineTests;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Contracts.Laundry;

/// <summary>
/// POST /api/v1/simulation/machines/{machineId}/restore sobre PostgreSQL 18.1 real (SIM-06/07/08, QUEUE-06/07/14): só
/// out_of_service volta a available; o próximo da fila por ticket recebe a oferta no mesmo commit; failed/cancelled não
/// voltam; nenhuma compra, débito ou refund; restauração e outra promoção concorrente não atribuem o mesmo item duas vezes.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E06")]
[Trait("Task", "T7")]
public sealed class RestoreMachineTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/simulation/machines/{machineId}/restore";
    private const string ApiApp = "cicclo-e06-restore-api";
    private const string HolderApp = "cicclo-e06-restore-holder";
    private const string LedgerSql =
        "SELECT kind || '|' || coalesce(request_item_id::text, 'null') || '|' || amount || '|' || balance_after || '|' || to_char(created_at AT TIME ZONE 'UTC', 'HH24:MI:SS') FROM wallet_transactions ORDER BY created_at, kind";

    private static readonly string WalletLock = $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE";

    // Ciclo real e sintético: falha → restore sem fila deixa a máquina available, sem item, sem lançamento novo e sem
    // reativar o item que falhou.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S01_OutOfServiceMachineWithoutQueueBecomesAvailableWithoutLedgerChange(bool realExecution)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        Guid? failed = null;
        if (realExecution)
        {
            failed = (await RunningWashAsync(client, database, clock)).Item;
        }
        else
        {
            clock.SetUtcNow(T0.AddSeconds(5));
            await AssertOkAsync(client.PostAsync(OccupyMachineTests.Path(WasherId), null));
        }

        clock.SetUtcNow(T0.AddSeconds(30));
        await AssertOkAsync(client.PostAsync(BreakdownPath(WasherId), null));
        var ledger = await database.RowsAsync(LedgerSql);
        var items = await database.RowsAsync("SELECT id || '|' || status || '|' || coalesce(machine_id::text, 'null') FROM service_request_items ORDER BY id");
        clock.SetUtcNow(T0.AddSeconds(40));

        using var response = await client.PostAsync(Path(WasherId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal($"{WasherId}|washer-01|washer|available|null|False", Describe(body.GetProperty("machine")));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("item").ValueKind);
        Assert.Equal("washer-01|available|null|false|12:00:40.000000", (await database.RowsAsync(MachinesSql))[1]);
        Assert.Equal(ledger, await database.RowsAsync(LedgerSql));
        Assert.Equal(items, await database.RowsAsync("SELECT id || '|' || status || '|' || coalesce(machine_id::text, 'null') FROM service_request_items ORDER BY id"));
        if (failed is { } a)
        {
            Assert.Equal([$"{a}|failed|{WasherId}"], items);
            Assert.Equal(["debit|null|18.90|31.10|12:00:00", $"refund|{a}|18.90|50.00|12:00:30"], ledger);
            Assert.Equal(["50.00|2|1"], await database.RowsAsync(MoneySql));
        }
        else
        {
            Assert.Empty(items);
            Assert.Equal(["50.00|0|0"], await database.RowsAsync(MoneySql));
        }

        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), body.GetRawText());
    }

    // SIM-06/SIM-07/QUEUE-06: a fila paga preservada durante a falha é retomada por ticket; o primeiro recebe a oferta de
    // 60 s na máquina restaurada, o segundo continua queued, e o ofertado confirma normalmente. Nenhum débito/refund novo.
    [Fact]
    public async Task S02_RestoreOffersTheMachineToTheFirstQueuedItemByTicket()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        await RunningWashAsync(client, database, clock);
        clock.SetUtcNow(T0.AddSeconds(15));
        var queued = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var b = queued.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(16));
        var c = (await BookingApi.PurchaseAsync(database, clock, ["wash"])).Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));
        await AssertOkAsync(client.PostAsync(BreakdownPath(WasherId), null));
        var ledger = await database.RowsAsync(LedgerSql);
        clock.SetUtcNow(T0.AddSeconds(40));

        using var response = await client.PostAsync(Path(WasherId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal($"{WasherId}|washer-01|washer|reserved|{b}|False", Describe(body.GetProperty("machine")));
        var item = body.GetProperty("item");
        Assert.Equal(
            (b, "to_be_confirmed", WasherId, new DateTime(2026, 10, 21, 12, 1, 40, DateTimeKind.Utc)),
            (item.GetProperty("itemId").GetGuid(), item.GetProperty("status").GetString(), item.GetProperty("machineId").GetString(), item.GetProperty("expiresAt").GetDateTime().ToUniversalTime()));
        var rows = await database.RowsAsync(StateSql);
        Assert.Contains($"item|{b}|to_be_confirmed|2|12:00:15.000000|{WasherId}|12:00:40.000000|12:01:40.000000|null", rows);
        Assert.Contains($"item|{c}|queued|3|12:00:16.000000|null|null|null|null", rows);
        Assert.Equal($"washer-01|reserved|{b}|false|12:00:40.000000", (await database.RowsAsync(MachinesSql))[1]);
        Assert.Equal(ledger, await database.RowsAsync(LedgerSql));
        Assert.Equal(["62.20|4|3"], await database.RowsAsync(MoneySql));

        clock.SetUtcNow(T0.AddSeconds(50));
        using var confirmed = await client.PostAsync($"/api/v1/bookings/{queued.BookingId}/items/{b}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("in_progress", (await BookingApi.JsonAsync(confirmed)).GetProperty("status").GetString());
        Assert.Equal(ledger, await database.RowsAsync(LedgerSql));
    }

    // Itens terminais não voltam: o que falhou na máquina e o que foi cancelado na fila (ticket menor) ficam como estavam;
    // a oferta vai para o próximo elegível.
    [Fact]
    public async Task S03_FailedAndCancelledItemsAreNotReactivated()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (_, a) = await RunningWashAsync(client, database, clock);
        clock.SetUtcNow(T0.AddSeconds(15));
        var cancelled = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var x = cancelled.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(16));
        var b = (await BookingApi.PurchaseAsync(database, clock, ["wash"])).Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));
        await AssertOkAsync(client.PostAsync(BreakdownPath(WasherId), null));
        clock.SetUtcNow(T0.AddSeconds(31));
        await AssertOkAsync(client.PostAsync($"/api/v1/bookings/{cancelled.BookingId}/items/{x}/cancel", null));
        var before = await database.RowsAsync(StateSql);
        var ledger = await database.RowsAsync(LedgerSql);
        clock.SetUtcNow(T0.AddSeconds(40));

        using var response = await client.PostAsync(Path(WasherId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(b, (await BookingApi.JsonAsync(response)).GetProperty("item").GetProperty("itemId").GetGuid());
        var after = await database.RowsAsync(StateSql);
        foreach (var terminal in new[] { a, x })
        {
            var row = Assert.Single(before, line => line.StartsWith($"item|{terminal}|", StringComparison.Ordinal));
            Assert.Contains(row, after);
        }

        Assert.Contains($"item|{a}|failed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000", after);
        Assert.Contains(after, line => line.StartsWith($"item|{x}|cancelled|", StringComparison.Ordinal) && line.EndsWith("|null|null|null|null", StringComparison.Ordinal));
        Assert.Contains($"item|{b}|to_be_confirmed|3|12:00:16.000000|{WasherId}|12:00:40.000000|12:01:40.000000|null", after);
        Assert.Equal(ledger, await database.RowsAsync(LedgerSql));
        Assert.Equal(["debit|null|18.90|81.10|12:00:00", "debit|null|18.90|62.20|12:00:15", "debit|null|18.90|43.30|12:00:16", $"refund|{a}|18.90|62.20|12:00:30", $"refund|{x}|18.90|81.10|12:00:31"], ledger);
    }

    // Restore × promoção concorrente (conclusão da outra lavadora): ambos disputam o lock da wallet; o único item na fila
    // recebe uma só oferta, na máquina liberada primeiro, e a outra fica available.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S04_RestoreAndAConcurrentPromotionNeverAssignTheSameItemTwice(bool restoreFirst)
    {
        var database = await ReadyAsync(
            postgres,
            $"UPDATE wallets SET balance = 100.00; INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('{Washer2Id}', 'washer-02', 'washer', 'available', '2026-10-21 11:00:00+00')");
        var clock = Clock();
        Guid c;
        await using (var setup = BookingApi.Create(database, clock, Demo))
        {
            using var setupClient = setup.CreateClient();
            await RunningWashAsync(setupClient, database, clock);
            clock.SetUtcNow(T0.AddSeconds(12));
            var running = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
            Assert.Equal(Washer2Id, await database.ScalarAsync<string>($"SELECT machine_id::text FROM service_request_items WHERE id = '{running.Items[0].ItemId}'"));
            clock.SetUtcNow(T0.AddSeconds(14));
            await AssertOkAsync(setupClient.PostAsync($"/api/v1/bookings/{running.BookingId}/items/{running.Items[0].ItemId}/confirm", null));
            clock.SetUtcNow(T0.AddSeconds(20));
            await AssertOkAsync(setupClient.PostAsync(BreakdownPath(WasherId), null));
            clock.SetUtcNow(T0.AddSeconds(25));
            c = (await BookingApi.PurchaseAsync(database, clock, ["wash"])).Items[0].ItemId;
        }

        var ledger = await database.RowsAsync(LedgerSql);
        clock.SetUtcNow(T0.AddSeconds(30));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Demo);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);
        Task<HttpResponseMessage> Restore() => client.PostAsync(Path(WasherId), null);
        Task<HttpResponseMessage> Complete() => client.PostAsync($"/api/v1/simulation/machines/{Washer2Id}/complete", null);

        var firstTask = restoreFirst ? Restore() : Complete();
        await WaitUntilWaitingOnLockAsync(database, ApiApp);
        var secondTask = restoreFirst ? Complete() : Restore();
        await WaitUntilWaitingOnLockAsync(database, ApiApp, count: 2);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var first = await firstTask.WaitAsync(SignalTimeout);
        using var second = await secondTask.WaitAsync(SignalTimeout);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        var winner = restoreFirst ? WasherId : Washer2Id;
        var restored = await BookingApi.JsonAsync(restoreFirst ? first : second);
        Assert.Equal(restoreFirst ? JsonValueKind.Object : JsonValueKind.Null, restored.GetProperty("item").ValueKind);
        Assert.Contains($"item|{c}|to_be_confirmed|3|12:00:25.000000|{winner}|12:00:30.000000|12:01:30.000000|null", await database.RowsAsync(StateSql));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items WHERE status = 'to_be_confirmed'"));
        Assert.Equal(
            restoreFirst
                ? ["dryer-01|available|null|false", $"washer-01|reserved|{c}|false", "washer-02|available|null|false"]
                : ["dryer-01|available|null|false", "washer-01|available|null|false", $"washer-02|reserved|{c}|false"],
            await database.RowsAsync("SELECT name || '|' || state || '|' || coalesce(active_item_id::text, 'null') || '|' || is_demo_occupied FROM machines ORDER BY name"));
        Assert.Equal(ledger, await database.RowsAsync(LedgerSql));
    }

    [Theory]
    [InlineData("available")]
    [InlineData("reserved")]
    [InlineData("running")]
    [InlineData("synthetic")]
    public async Task S05_MachineThatIsNotOutOfServiceIsRefusedWith409WithoutMutation(string state)
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        switch (state)
        {
            case "reserved":
                await BookingApi.PurchaseAsync(database, clock, ["wash"]);
                break;
            case "running":
                await RunningWashAsync(client, database, clock);
                clock.SetUtcNow(T0.AddSeconds(15));
                await BookingApi.PurchaseAsync(database, clock, ["wash"]);
                break;
            case "synthetic":
                await AssertOkAsync(client.PostAsync(OccupyMachineTests.Path(WasherId), null));
                break;
        }

        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(40));

        using var response = await client.PostAsync(Path(WasherId), null);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(WasherId));
        Assert.Equal("A máquina não está fora de serviço; não há o que restaurar e nada foi alterado.", problem.GetProperty("detail").GetString());
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains(before, row => row.StartsWith($"washer-01|{(state == "synthetic" ? "running" : state)}|", StringComparison.Ordinal));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "409", "application/problem+json", "post"), problem.GetRawText());
    }

    // Restauração repetida é 409 e não desfaz a oferta feita pela primeira; IDs inválidos/inexistentes não mudam nada.
    [Fact]
    public async Task S06_RepeatedRestoreAndInvalidIdsDoNotMutate()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        await RunningWashAsync(client, database, clock);
        clock.SetUtcNow(T0.AddSeconds(15));
        var b = (await BookingApi.PurchaseAsync(database, clock, ["wash"])).Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));
        await AssertOkAsync(client.PostAsync(BreakdownPath(WasherId), null));
        clock.SetUtcNow(T0.AddSeconds(40));
        await AssertOkAsync(client.PostAsync(Path(WasherId), null));
        var after = await SnapshotAsync(database);
        var unknown = Guid.NewGuid().ToString();
        clock.SetUtcNow(T0.AddSeconds(45));

        using var repeat = await client.PostAsync(Path(WasherId), null);
        using var invalid = await client.PostAsync("/api/v1/simulation/machines/x/restore", null);
        using var missing = await client.PostAsync(Path(unknown), null);

        await ProblemAssert.HasProblemAsync(repeat, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(WasherId));
        await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, "/api/v1/simulation/machines/x/restore");
        var notFound = await ProblemAssert.HasProblemAsync(missing, HttpStatusCode.NotFound, ApiProblems.MachineNotFound, Path(unknown));
        Assert.Equal(after, await SnapshotAsync(database));
        Assert.Contains($"washer-01|reserved|{b}|false|12:00:40.000000", after);
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "404", "application/problem+json", "post"), notFound.GetRawText());
    }

    // Falha ao promover o próximo ou no commit desfaz também a restauração: a máquina segue fora de serviço e a fila intacta.
    [Theory]
    [InlineData("CREATE TRIGGER fail_restore BEFORE UPDATE ON service_request_items FOR EACH ROW WHEN (NEW.status = 'to_be_confirmed') EXECUTE FUNCTION fail_restore();")]
    [InlineData("CREATE CONSTRAINT TRIGGER fail_restore AFTER UPDATE ON machines DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_restore();")]
    public async Task S07_FailureWhilePromotingOrCommittingRevertsTheRestore(string trigger)
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        await RunningWashAsync(client, database, clock);
        clock.SetUtcNow(T0.AddSeconds(15));
        var b = (await BookingApi.PurchaseAsync(database, clock, ["wash"])).Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));
        await AssertOkAsync(client.PostAsync(BreakdownPath(WasherId), null));
        await database.ExecuteAsync(
            $"""
            CREATE FUNCTION fail_restore() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada SELECT secreto'; END $$;
            {trigger}
            """);
        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(40));

        using var response = await client.PostAsync(Path(WasherId), null);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, Path(WasherId));
        Assert.DoesNotContain("secreto", problem.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains("washer-01|out_of_service|null|false|12:00:30.000000", before);
        Assert.Contains($"item|{b}|queued|2|12:00:15.000000|null|null|null|null", await database.RowsAsync(StateSql));
    }

    // SIM-08 e contrato: a rota só existe em Development com DemoMode; fora disso 404 sem efeito e fora do OpenAPI.
    [Theory]
    [InlineData("Development", "false")]
    [InlineData("Production", "true")]
    public async Task S08_RouteIsAbsentOutsideDevelopmentWithDemoModeAndTheContractMatches(string environment, string demoMode)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        clock.SetUtcNow(T0.AddSeconds(5));
        await AssertOkAsync(client.PostAsync(OccupyMachineTests.Path(WasherId), null));
        await AssertOkAsync(client.PostAsync(BreakdownPath(WasherId), null));
        await using var outside = Host(database, environment, demoMode);
        using var plain = outside.CreateClient();
        var before = await SnapshotAsync(database);

        using var unmapped = await plain.PostAsync(Path(WasherId), null);
        using var document = await plain.GetAsync(ApiOpenApi.DocumentPath);

        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains("washer-01|out_of_service|null|false|12:00:05.000000", before);
        if (environment == "Development")
        {
            Assert.Equal(HttpStatusCode.OK, document.StatusCode);
            Assert.DoesNotContain("restore", await document.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        }

        var openApi = await OpenApiContract.FetchAsync(client);
        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "POST", "RestoreMachine")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Post];
        Assert.Equal(["Simulation"], operation.Tags!.Select(tag => tag.Name));
        Assert.Contains("somente Development com Laundry:DemoMode=true", operation.Summary, StringComparison.Ordinal);
        Assert.Equal(["200", "400", "404", "409", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(operation.RequestBody);
        var parameter = Assert.Single(operation.Parameters!);
        Assert.Equal(("machineId", "uuid", ParameterLocation.Path), (parameter.Name, parameter.Schema!.Format, parameter.In!.Value));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), JsonSchemaContract.Example(openApi, Route, "200", method: "post"));
        // Todas as rotas de simulação seguem documentadas como dev-only.
        var simulationPaths = OpenApiContract.Read(openApi).Document!.Paths.Where(path => path.Key.StartsWith("/api/v1/simulation/", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["/api/v1/simulation/incidents/{incidentId}/confirm", "/api/v1/simulation/machines/{machineId}/breakdown", "/api/v1/simulation/machines/{machineId}/complete", "/api/v1/simulation/machines/{machineId}/occupy", Route],
            simulationPaths.Select(path => path.Key).Order(StringComparer.Ordinal));
        Assert.All(
            simulationPaths.SelectMany(path => path.Value.Operations!.Values),
            simulation => Assert.Contains("somente Development com Laundry:DemoMode=true", simulation.Summary, StringComparison.Ordinal));
    }

    private static string Path(string machineId) => $"/api/v1/simulation/machines/{machineId}/restore";

    private static string BreakdownPath(string machineId) => $"/api/v1/simulation/machines/{machineId}/breakdown";

    /// <summary>Compra wash às 12:00:00 (oferta da lavadora) confirmada às 12:00:10: execução real in_progress.</summary>
    private static async Task<(Guid Booking, Guid Item)> RunningWashAsync(HttpClient client, TestDatabase database, FakeTimeProvider clock)
    {
        clock.SetUtcNow(T0);
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(10));
        await AssertOkAsync(client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{booking.Items[0].ItemId}/confirm", null));
        return (booking.BookingId, booking.Items[0].ItemId);
    }

    private static async Task AssertOkAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
