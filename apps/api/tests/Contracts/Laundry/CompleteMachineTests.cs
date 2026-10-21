using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Microsoft.OpenApi;
using Xunit;
using static Cicclo.Api.Tests.Contracts.Laundry.OccupyMachineTests;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Contracts.Laundry;

/// <summary>
/// POST /api/v1/simulation/machines/{machineId}/complete sobre PostgreSQL 18.1 real (SIM-02/08/09, QUEUE-06/07/14):
/// execução real → item completed e máquina livre; ocupação sintética só libera; o próximo da fila recebe a oferta no
/// mesmo commit; sem débito, crédito ou lançamento; recusas e falhas não deixam efeito parcial. HTTP e banco juntos.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E06")]
[Trait("Task", "T3")]
public sealed class CompleteMachineTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/simulation/machines/{machineId}/complete";
    private const string LedgerSql = "SELECT kind || '|' || coalesce(request_item_id::text, 'null') || '|' || amount || '|' || balance_after FROM wallet_transactions ORDER BY created_at";

    [Fact]
    public async Task C01_RealExecutionCompletesTheItemAndFreesTheMachineWithoutMoney()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var (booking, wash) = await RunningWashAsync(database, clock);
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        await ConfirmAsync(client, booking, wash, clock);
        clock.SetUtcNow(T0.AddSeconds(40));

        using var response = await client.PostAsync(Path(WasherId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal($"{WasherId}|washer-01|washer|available|null|False", Describe(body.GetProperty("machine")));
        var item = body.GetProperty("item");
        Assert.Equal(
            (wash, "wash", 1890L, "completed"),
            (item.GetProperty("itemId").GetGuid(), item.GetProperty("serviceCode").GetString(), item.GetProperty("unitPriceCents").GetInt64(), item.GetProperty("status").GetString()));
        Assert.Contains($"item|{wash}|completed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000", await database.RowsAsync(StateSql));
        Assert.Equal("washer-01|available|null|false|12:00:40.000000", (await database.RowsAsync(MachinesSql))[1]);
        Assert.Equal(["debit|null|18.90|31.10"], await database.RowsAsync(LedgerSql));
        Assert.Equal(["31.10|1|1"], await database.RowsAsync(MoneySql));
        var detail = await BookingApi.JsonAsync(await client.GetAsync($"/api/v1/bookings/{booking}"));
        Assert.Equal("completed", detail.GetProperty("items")[0].GetProperty("status").GetString());
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), body.GetRawText());
    }

    [Fact]
    public async Task C02_SyntheticOccupationIsReleasedWithoutBookingDebitOrRefund()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock(T0.AddSeconds(5));
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        using (var occupied = await client.PostAsync(OccupyMachineTests.Path(WasherId), null))
        {
            Assert.Equal(HttpStatusCode.OK, occupied.StatusCode);
        }

        clock.SetUtcNow(T0.AddSeconds(20));

        using var response = await client.PostAsync(Path(WasherId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal($"{WasherId}|washer-01|washer|available|null|False", Describe(body.GetProperty("machine")));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("item").ValueKind);
        Assert.Equal("washer-01|available|null|false|12:00:20.000000", (await database.RowsAsync(MachinesSql))[1]);
        Assert.Equal(["50.00|0|0"], await database.RowsAsync(MoneySql));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items"));
    }

    // QUEUE-06: com B (ticket menor) e C na fila, a máquina liberada é ofertada a B no mesmo commit; C continua na fila.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task C03_CompletionOffersTheReleasedMachineToTheFirstInLine(bool realExecution)
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        Guid? running = null;
        if (realExecution)
        {
            var (booking, wash) = await RunningWashAsync(database, clock);
            await ConfirmAsync(client, booking, wash, clock);
            running = wash;
        }
        else
        {
            using var occupied = await client.PostAsync(OccupyMachineTests.Path(WasherId), null);
            Assert.Equal(HttpStatusCode.OK, occupied.StatusCode);
        }

        clock.SetUtcNow(T0.AddSeconds(15));
        var b = (await BookingApi.PurchaseAsync(database, clock, ["wash"])).Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(16));
        var c = (await BookingApi.PurchaseAsync(database, clock, ["wash"])).Items[0].ItemId;
        var ticket = realExecution ? 2 : 1;
        clock.SetUtcNow(T0.AddSeconds(30));

        using var response = await client.PostAsync(Path(WasherId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal($"{WasherId}|washer-01|washer|reserved|{b}|False", Describe(body.GetProperty("machine")));
        var rows = await database.RowsAsync(StateSql);
        Assert.Contains($"item|{b}|to_be_confirmed|{ticket}|12:00:15.000000|{WasherId}|12:00:30.000000|12:01:30.000000|null", rows);
        Assert.Contains($"item|{c}|queued|{ticket + 1}|12:00:16.000000|null|null|null|null", rows);
        Assert.Equal($"washer-01|reserved|{b}|false|12:00:30.000000", (await database.RowsAsync(MachinesSql))[1]);
        if (running is { } a)
        {
            Assert.Equal(a, body.GetProperty("item").GetProperty("itemId").GetGuid());
            Assert.Contains(rows, row => row.StartsWith($"item|{a}|completed|", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty("item").ValueKind);
        }

        Assert.DoesNotContain(await database.RowsAsync(LedgerSql), row => row.StartsWith("refund|", StringComparison.Ordinal));
        var next = await BookingApi.JsonAsync(await client.PostAsync($"/api/v1/bookings/{await BookingOfAsync(database, b)}/items/{b}/confirm", null));
        Assert.Equal("in_progress", next.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("available")]
    [InlineData("reserved")]
    [InlineData("out_of_service")]
    public async Task C04_MachineThatIsNotRunningIsRefusedWith409WithoutMutation(string state)
    {
        var database = await ReadyAsync(postgres, state == "out_of_service" ? $"UPDATE machines SET state = 'out_of_service' WHERE id = '{WasherId}'" : null);
        var clock = Clock();
        if (state == "reserved")
        {
            await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        }

        var before = await SnapshotAsync(database);
        await using var factory = BookingApi.Create(database, clock, Demo);

        using var response = await factory.CreateClient().PostAsync(Path(WasherId), null);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(WasherId));
        Assert.Equal("A máquina não está em execução; não há execução para concluir e nada foi alterado.", problem.GetProperty("detail").GetString());
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains(before, row => row.StartsWith($"washer-01|{state}|", StringComparison.Ordinal));
    }

    // Repetir a conclusão não conclui de novo nem desfaz a oferta que a primeira conclusão deu ao próximo.
    [Fact]
    public async Task C05_RepeatedCompletionIsRefusedAndDoesNotCompleteTwice()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        var (booking, wash) = await RunningWashAsync(database, clock);
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        await ConfirmAsync(client, booking, wash, clock);
        clock.SetUtcNow(T0.AddSeconds(15));
        var next = (await BookingApi.PurchaseAsync(database, clock, ["wash"])).Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));
        using (var first = await client.PostAsync(Path(WasherId), null))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var after = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(35));

        using var repeat = await client.PostAsync(Path(WasherId), null);

        await ProblemAssert.HasProblemAsync(repeat, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(WasherId));
        Assert.Equal(after, await SnapshotAsync(database));
        Assert.Single(after, row => row.Contains("|completed|", StringComparison.Ordinal));
        Assert.Contains($"washer-01|reserved|{next}|false|12:00:30.000000", after);
        Assert.Equal(["debit|null|18.90|81.10", "debit|null|18.90|62.20"], await database.RowsAsync(LedgerSql));
    }

    // Falha ao promover o próximo ou no commit desfaz também a conclusão: item, máquina e fila ficam como estavam.
    [Theory]
    [InlineData("CREATE TRIGGER fail_complete BEFORE UPDATE ON service_request_items FOR EACH ROW WHEN (NEW.status = 'to_be_confirmed') EXECUTE FUNCTION fail_complete();")]
    [InlineData("CREATE CONSTRAINT TRIGGER fail_complete AFTER UPDATE ON machines DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_complete();")]
    public async Task C06_FailureWhilePromotingOrCommittingRevertsTheCompletion(string trigger)
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        var (booking, wash) = await RunningWashAsync(database, clock);
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        await ConfirmAsync(client, booking, wash, clock);
        clock.SetUtcNow(T0.AddSeconds(15));
        var next = (await BookingApi.PurchaseAsync(database, clock, ["wash"])).Items[0].ItemId;
        await database.ExecuteAsync(
            $"""
            CREATE FUNCTION fail_complete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada SELECT secreto'; END $$;
            {trigger}
            """);
        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(30));

        using var response = await client.PostAsync(Path(WasherId), null);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, Path(WasherId));
        Assert.DoesNotContain("secreto", problem.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains(before, row => row.StartsWith($"item|{wash}|in_progress|", StringComparison.Ordinal));
        Assert.Contains(before, row => row.StartsWith($"item|{next}|queued|", StringComparison.Ordinal));
        Assert.Contains($"washer-01|running|{wash}|false|12:00:10.000000", before);
    }

    [Fact]
    public async Task C07_IdsEnvironmentAndContractMatchTheSpec()
    {
        var database = await ReadyAsync(postgres);
        var before = await SnapshotAsync(database);
        await using var factory = BookingApi.Create(database, Clock(), Demo);
        using var client = factory.CreateClient();
        await using var withoutDemo = Host(database, "Development", "false");
        using var plain = withoutDemo.CreateClient();
        var unknown = Guid.NewGuid().ToString();
        var openApi = await OpenApiContract.FetchAsync(client);

        using var invalid = await client.PostAsync("/api/v1/simulation/machines/x/complete", null);
        using var missing = await client.PostAsync(Path(unknown), null);
        using var unmapped = await plain.PostAsync(Path(WasherId), null);
        var plainOpenApi = await OpenApiContract.FetchAsync(plain);

        await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, "/api/v1/simulation/machines/x/complete");
        var notFound = await ProblemAssert.HasProblemAsync(missing, HttpStatusCode.NotFound, ApiProblems.MachineNotFound, Path(unknown));
        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
        Assert.DoesNotContain("/complete", plainOpenApi, StringComparison.Ordinal);
        Assert.DoesNotContain("simulation", plainOpenApi, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "POST", "CompleteMachine")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Post];
        Assert.Equal(["Simulation"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "400", "404", "409", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(operation.RequestBody);
        var parameter = Assert.Single(operation.Parameters!);
        Assert.Equal(("machineId", "uuid", ParameterLocation.Path), (parameter.Name, parameter.Schema!.Format, parameter.In!.Value));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), JsonSchemaContract.Example(openApi, Route, "200", method: "post"));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "404", "application/problem+json", "post"), notFound.GetRawText());
        var pointer = JsonSchemaContract.ResponsePointer(Route, "200", method: "post");
        var synthetic = $$"""{"machine":{"machineId":"{{WasherId}}","name":"washer-01","machineType":"washer","state":"available","activeItemId":null,"isDemoOccupied":false},"item":null}""";
        JsonSchemaContract.AssertValid(openApi, pointer, synthetic);
        JsonSchemaContract.AssertInvalid(openApi, pointer, synthetic.Replace(",\"item\":null", "", StringComparison.Ordinal));
    }

    private static string Path(string machineId) => $"/api/v1/simulation/machines/{machineId}/complete";

    /// <summary>Compra wash às 12:00:00 (oferta da lavadora), a ser confirmada às 12:00:10 por <see cref="ConfirmAsync"/>.</summary>
    private static async Task<(Guid Booking, Guid Item)> RunningWashAsync(TestDatabase database, FakeTimeProvider clock)
    {
        clock.SetUtcNow(T0);
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        return (booking.BookingId, booking.Items[0].ItemId);
    }

    private static async Task ConfirmAsync(HttpClient client, Guid booking, Guid item, FakeTimeProvider clock)
    {
        clock.SetUtcNow(T0.AddSeconds(10));
        using var confirmed = await client.PostAsync($"/api/v1/bookings/{booking}/items/{item}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
    }

    private static Task<Guid> BookingOfAsync(TestDatabase database, Guid item) =>
        database.ScalarAsync<Guid>($"SELECT request_id FROM service_request_items WHERE id = '{item}'");
}
