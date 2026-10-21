using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.OpenApi;
using Xunit;
using static Cicclo.Api.Tests.Contracts.Laundry.OccupyMachineTests;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;
using RefundCounter = Cicclo.Api.Tests.Contracts.Bookings.CancelItemTests.RefundCounter;

namespace Cicclo.Api.Tests.Contracts.Laundry;

/// <summary>
/// POST /api/v1/simulation/incidents/{incidentId}/confirm sobre PostgreSQL 18.1 real (SIM-03/04/05/08, REFUND-02/06,
/// HTTP-04/06..08): o relato reported da execução corrente é confirmado pelo mesmo caso de falha do breakdown (item
/// failed, máquina out_of_service, refund histórico único) e passa a confirmed; repetir devolve o estado atual sem novo
/// refund; execução já concluída, vínculo incoerente ou corrida perdida para o breakdown recusam sem reabrir o item.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E06")]
[Trait("Task", "T6")]
public sealed class ConfirmIncidentTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/simulation/incidents/{incidentId}/confirm";
    private const string ApiApp = "cicclo-e06-incident-api";
    private const string HolderApp = "cicclo-e06-incident-holder";
    private const string LedgerSql =
        "SELECT kind || '|' || coalesce(request_item_id::text, 'null') || '|' || amount || '|' || balance_after || '|' || to_char(created_at AT TIME ZONE 'UTC', 'HH24:MI:SS') FROM wallet_transactions ORDER BY created_at, kind";

    /// <summary>Incidentes: id|máquina|item|status|descrição (ou null).</summary>
    private const string IncidentsSql =
        "SELECT id || '|' || machine_id || '|' || request_item_id || '|' || status || '|' || coalesce(description, 'null') FROM machine_incidents ORDER BY reported_at, id";

    private static readonly string WalletLock = $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE";

    [Fact]
    public async Task I01_ReportedIncidentOfTheCurrentRunFailsItWithASingleHistoricalRefund()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        using var counter = new RefundCounter(factory.Services.GetRequiredService<IMeterFactory>());
        var (booking, wash) = await RunningWashAsync(client, database, clock);
        var incident = await ReportAsync(client, booking, wash, clock);
        await database.ExecuteAsync("UPDATE services SET current_price = 25.00 WHERE code = 'wash'");
        clock.SetUtcNow(T0.AddSeconds(40));

        using var response = await client.PostAsync(Path(incident), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal(["incidentId", "status", "machine", "item"], body.EnumerateObject().Select(property => property.Name));
        Assert.Equal((incident, "confirmed"), (body.GetProperty("incidentId").GetGuid(), body.GetProperty("status").GetString()));
        Assert.Equal($"{WasherId}|washer-01|washer|out_of_service|null|False", Describe(body.GetProperty("machine")));
        AssertFailedItem(body.GetProperty("item"), wash);
        Assert.Equal([$"{incident}|{WasherId}|{wash}|confirmed|parou"], await database.RowsAsync(IncidentsSql));
        Assert.Contains($"item|{wash}|failed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000", await database.RowsAsync(StateSql));
        Assert.Equal("washer-01|out_of_service|null|false|12:00:40.000000", (await database.RowsAsync(MachinesSql))[1]);
        Assert.Equal(["debit|null|18.90|31.10|12:00:00", $"refund|{wash}|18.90|50.00|12:00:40"], await database.RowsAsync(LedgerSql));
        Assert.Equal(["50.00|2|1"], await database.RowsAsync(MoneySql));
        Assert.Equal(["reason=machine_failure"], counter.Measurements);
        var detail = await BookingApi.JsonAsync(await client.GetAsync($"/api/v1/bookings/{booking}"));
        Assert.Equal(("failed", 5000L), (detail.GetProperty("items")[0].GetProperty("status").GetString(), detail.GetProperty("remainingBalanceCents").GetInt64()));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), body.GetRawText());
    }

    // Confirmação repetida devolve o estado atual (200) sem nova falha, refund nem métrica; o breakdown depois dela
    // também não credita de novo.
    [Fact]
    public async Task I02_RepeatedConfirmationReturnsTheCurrentStateWithoutASecondRefund()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        using var counter = new RefundCounter(factory.Services.GetRequiredService<IMeterFactory>());
        var (booking, wash) = await RunningWashAsync(client, database, clock);
        var incident = await ReportAsync(client, booking, wash, clock);
        clock.SetUtcNow(T0.AddSeconds(30));
        using var first = await client.PostAsync(Path(incident), null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var after = await SnapshotAsync(database);
        var incidents = await database.RowsAsync(IncidentsSql);
        clock.SetUtcNow(T0.AddSeconds(35));

        using var repeat = await client.PostAsync(Path(incident), null);
        using var breakdown = await client.PostAsync($"/api/v1/simulation/machines/{WasherId}/breakdown", null);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal((await BookingApi.JsonAsync(first)).GetRawText(), (await BookingApi.JsonAsync(repeat)).GetRawText());
        Assert.Equal(HttpStatusCode.Conflict, breakdown.StatusCode);
        Assert.Equal(after, await SnapshotAsync(database));
        Assert.Equal(incidents, await database.RowsAsync(IncidentsSql));
        Assert.Equal(["debit|null|18.90|31.10|12:00:00", $"refund|{wash}|18.90|50.00|12:00:30"], await database.RowsAsync(LedgerSql));
        Assert.Equal(["reason=machine_failure"], counter.Measurements);
    }

    // SIM-05/REFUND-02 entre as duas rotas: confirmação e breakdown da mesma execução disputam o lock da wallet; em
    // qualquer ordem há um único refund. Breakdown primeiro deixa o relato reported (histórico) e a confirmação 409.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I03_ConfirmationAndBreakdownOfTheSameRunProduceASingleRefund(bool confirmFirst)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        Guid incident;
        await using (var setup = BookingApi.Create(database, clock, Demo))
        {
            using var setupClient = setup.CreateClient();
            var (booking, item) = await RunningWashAsync(setupClient, database, clock);
            incident = await ReportAsync(setupClient, booking, item, clock);
        }

        var wash = await database.ScalarAsync<Guid>("SELECT id FROM service_request_items");
        clock.SetUtcNow(T0.AddSeconds(30));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Demo);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);
        Task<HttpResponseMessage> Confirm() => client.PostAsync(Path(incident), null);
        Task<HttpResponseMessage> Breakdown() => client.PostAsync($"/api/v1/simulation/machines/{WasherId}/breakdown", null);

        var firstTask = confirmFirst ? Confirm() : Breakdown();
        await WaitUntilWaitingOnLockAsync(database, ApiApp);
        var secondTask = confirmFirst ? Breakdown() : Confirm();
        await WaitUntilWaitingOnLockAsync(database, ApiApp, count: 2);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var first = await firstTask.WaitAsync(SignalTimeout);
        using var second = await secondTask.WaitAsync(SignalTimeout);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Conflict), (first.StatusCode, second.StatusCode));
        Assert.Equal(["debit|null|18.90|31.10|12:00:00", $"refund|{wash}|18.90|50.00|12:00:30"], await database.RowsAsync(LedgerSql));
        Assert.Contains($"item|{wash}|failed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000", await database.RowsAsync(StateSql));
        Assert.Equal("washer-01|out_of_service|null|false|12:00:30.000000", (await database.RowsAsync(MachinesSql))[1]);
        Assert.Equal([$"{incident}|{WasherId}|{wash}|{(confirmFirst ? "confirmed" : "reported")}|parou"], await database.RowsAsync(IncidentsSql));
        if (!confirmFirst)
        {
            var problem = await ProblemAssert.HasProblemAsync(second, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(incident));
            Assert.Equal("O incidente não está ligado a uma execução em andamento; nada foi alterado e não há refund.", problem.GetProperty("detail").GetString());
        }
    }

    // Um relato não impede a conclusão: se complete venceu, confirmar responde 409, não reabre o item, não credita e o
    // relato continua reported, só como histórico.
    [Fact]
    public async Task I04_RunAlreadyCompletedRefusesConfirmationWithoutReopeningTheItem()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(client, database, clock);
        var incident = await ReportAsync(client, booking, wash, clock);
        clock.SetUtcNow(T0.AddSeconds(30));
        using (var complete = await client.PostAsync($"/api/v1/simulation/machines/{WasherId}/complete", null))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(35));

        using var response = await client.PostAsync(Path(incident), null);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(incident));
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains($"item|{wash}|completed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000", before);
        Assert.Equal("washer-01|available|null|false|12:00:30.000000", (await database.RowsAsync(MachinesSql))[1]);
        Assert.Equal([$"{incident}|{WasherId}|{wash}|reported|parou"], await database.RowsAsync(IncidentsSql));
        Assert.Equal(["31.10|1|1"], await database.RowsAsync(MoneySql));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "409", "application/problem+json", "post"), problem.GetRawText());
    }

    [Fact]
    public async Task I05_UnknownOrInvalidIncidentIdIsRefusedWithoutMutation()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(client, database, clock);
        await ReportAsync(client, booking, wash, clock);
        var before = await SnapshotAsync(database);
        var incidents = await database.RowsAsync(IncidentsSql);
        var unknown = Guid.NewGuid().ToString();

        using var invalid = await client.PostAsync("/api/v1/simulation/incidents/x/confirm", null);
        using var missing = await client.PostAsync(Path(unknown), null);
        // O id de um item (ou máquina) não é id de incidente.
        using var itemId = await client.PostAsync(Path(wash.ToString()), null);

        await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, "/api/v1/simulation/incidents/x/confirm");
        var notFound = await ProblemAssert.HasProblemAsync(missing, HttpStatusCode.NotFound, ApiProblems.IncidentNotFound, Path(unknown));
        Assert.Equal("Nenhum incidente com esse identificador; nada foi alterado.", notFound.GetProperty("detail").GetString());
        await ProblemAssert.HasProblemAsync(itemId, HttpStatusCode.NotFound, ApiProblems.IncidentNotFound, Path(wash.ToString()));
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Equal(incidents, await database.RowsAsync(IncidentsSql));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "404", "application/problem+json", "post"), notFound.GetRawText());
    }

    // Vínculo incoerente: o relato aponta outra máquina (do mesmo tipo ou de outro) que não a da execução do item.
    // Recusa sem falhar nenhuma máquina, sem refund e sem mudar o relato.
    [Theory]
    [InlineData(Washer2Id)]
    [InlineData(DryerId)]
    public async Task I06_IncidentLinkedToAnotherMachineIsRefusedWithoutMutation(string machineId)
    {
        var database = await ReadyAsync(postgres, $"INSERT INTO machines (id, name, machine_type, state, updated_at) VALUES ('{Washer2Id}', 'washer-02', 'washer', 'available', '2026-10-21 11:00:00+00')");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (_, wash) = await RunningWashAsync(client, database, clock);
        await AssertOkAsync(client.PostAsync(OccupyMachineTests.Path(machineId), null));
        var incident = Guid.NewGuid();
        await database.ExecuteAsync(
            $"INSERT INTO machine_incidents (id, machine_id, request_item_id, status, description, reported_at) VALUES ('{incident}', '{machineId}', '{wash}', 'reported', NULL, '2026-10-21 12:00:20+00')");
        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(30));

        using var response = await client.PostAsync(Path(incident.ToString()), null);

        await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(incident.ToString()));
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains($"washer-01|running|{wash}|false|12:00:10.000000", before);
        Assert.Contains(before, row => row.StartsWith($"{(machineId == DryerId ? "dryer-01" : "washer-02")}|running|null|true|", StringComparison.Ordinal));
        Assert.Equal([$"{incident}|{machineId}|{wash}|reported|null"], await database.RowsAsync(IncidentsSql));
        Assert.Equal(["31.10|1|1"], await database.RowsAsync(MoneySql));
    }

    // Falha no commit (constraint trigger diferido no relato) desfaz a falha, o refund e a confirmação juntos.
    [Fact]
    public async Task I07_FailureAtCommitRevertsFailureRefundAndConfirmation()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(client, database, clock);
        var incident = await ReportAsync(client, booking, wash, clock);
        await database.ExecuteAsync(
            """
            CREATE FUNCTION fail_confirmation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada SELECT secreto'; END $$;
            CREATE CONSTRAINT TRIGGER fail_confirmation AFTER UPDATE ON machine_incidents DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_confirmation();
            """);
        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(30));

        using var response = await client.PostAsync(Path(incident), null);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, Path(incident));
        Assert.DoesNotContain("secreto", problem.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains($"washer-01|running|{wash}|false|12:00:10.000000", before);
        Assert.Equal([$"{incident}|{WasherId}|{wash}|reported|parou"], await database.RowsAsync(IncidentsSql));
        Assert.Equal(["31.10|1|1"], await database.RowsAsync(MoneySql));
    }

    // SIM-08 e contrato: a rota só existe em Development com DemoMode; fora disso 404 sem efeito e fora do OpenAPI.
    [Theory]
    [InlineData("Development", "false")]
    [InlineData("Production", "true")]
    public async Task I08_RouteIsAbsentOutsideDevelopmentWithDemoModeAndTheContractMatches(string environment, string demoMode)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(client, database, clock);
        var incident = await ReportAsync(client, booking, wash, clock);
        await using var outside = Host(database, environment, demoMode);
        using var plain = outside.CreateClient();
        var before = await SnapshotAsync(database);

        using var unmapped = await plain.PostAsync(Path(incident), null);
        using var document = await plain.GetAsync(ApiOpenApi.DocumentPath);

        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Equal([$"{incident}|{WasherId}|{wash}|reported|parou"], await database.RowsAsync(IncidentsSql));
        if (environment == "Development")
        {
            Assert.Equal(HttpStatusCode.OK, document.StatusCode);
            Assert.DoesNotContain("incidents/", await document.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        }

        var openApi = await OpenApiContract.FetchAsync(client);
        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "POST", "ConfirmIncident")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Post];
        Assert.Equal(["Simulation"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "400", "404", "409", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(operation.RequestBody);
        var parameter = Assert.Single(operation.Parameters!);
        Assert.Equal(("incidentId", "uuid", ParameterLocation.Path), (parameter.Name, parameter.Schema!.Format, parameter.In!.Value));
        var ok = JsonSchemaContract.ResponsePointer(Route, "200", method: "post");
        JsonSchemaContract.AssertValid(openApi, ok, JsonSchemaContract.Example(openApi, Route, "200", method: "post"));
        var example = JsonSchemaContract.Example(openApi, Route, "200", method: "post");
        JsonSchemaContract.AssertInvalid(openApi, ok, example.Replace("\"confirmed\"", "\"reported\"", StringComparison.Ordinal));
        JsonSchemaContract.AssertInvalid(openApi, ok, example.Replace("\"item\":", "\"other\":", StringComparison.Ordinal));
    }

    private static string Path(Guid incidentId) => Path(incidentId.ToString());

    private static string Path(string incidentId) => $"/api/v1/simulation/incidents/{incidentId}/confirm";

    /// <summary>Compra wash às 12:00:00 (oferta da lavadora) confirmada às 12:00:10: execução real in_progress.</summary>
    private static async Task<(Guid Booking, Guid Item)> RunningWashAsync(HttpClient client, TestDatabase database, FakeTimeProvider clock)
    {
        clock.SetUtcNow(T0);
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(10));
        await AssertOkAsync(client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{booking.Items[0].ItemId}/confirm", null));
        return (booking.BookingId, booking.Items[0].ItemId);
    }

    /// <summary>Relato "parou" às 12:00:20 pela rota do consumidor; devolve o incidentId.</summary>
    private static async Task<Guid> ReportAsync(HttpClient client, Guid booking, Guid wash, FakeTimeProvider clock)
    {
        clock.SetUtcNow(T0.AddSeconds(20));
        using var response = await client.PostAsJsonAsync($"/api/v1/bookings/{booking}/items/{wash}/report-issue", new { description = "parou" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await BookingApi.JsonAsync(response)).GetProperty("incidentId").GetGuid();
    }

    private static void AssertFailedItem(JsonElement item, Guid wash) =>
        Assert.Equal(
            (wash, "wash", 1890L, "failed"),
            (item.GetProperty("itemId").GetGuid(), item.GetProperty("serviceCode").GetString(), item.GetProperty("unitPriceCents").GetInt64(), item.GetProperty("status").GetString()));

    private static async Task AssertOkAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
