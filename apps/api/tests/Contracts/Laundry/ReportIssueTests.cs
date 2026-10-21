using System.Globalization;
using System.Net;
using System.Text;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.OpenApi;
using Npgsql;
using Xunit;
using static Cicclo.Api.Tests.Contracts.Laundry.OccupyMachineTests;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Contracts.Laundry;

/// <summary>
/// POST /api/v1/bookings/{bookingId}/items/{itemId}/report-issue sobre PostgreSQL 18.1 real (SIM-03, HTTP-01/04/06..08,
/// OPS-04): relato de item in_progress com 202 e incidente aberto único, sem tocar item, máquina, saldo ou ledger;
/// repetição devolve o relato aberto sem trocar a descrição; recusas não registram nada. HTTP e banco juntos.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E06")]
[Trait("Task", "T4")]
public sealed class ReportIssueTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/bookings/{bookingId}/items/{itemId}/report-issue";
    private const string ApiApp = "cicclo-e06-report-api";
    private const string HolderApp = "cicclo-e06-report-holder";
    private static readonly string WalletLock = $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE";

    /// <summary>Incidentes: id|máquina|item|status|descrição (ou null)|reported_at.</summary>
    private const string IncidentsSql =
        "SELECT id || '|' || machine_id || '|' || request_item_id || '|' || status || '|' || coalesce(description, 'null') || '|' || to_char(reported_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM machine_incidents ORDER BY reported_at, id";

    private static readonly Dictionary<string, string> Settings = new(Demo) { ["RateLimiting:IssueReports:PermitLimit"] = "1000" };

    [Fact]
    public async Task R01_RunningItemReportIsAcceptedWith202AndRecordedWithoutTouchingMoneyItemOrMachine()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(database, client, clock);
        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(20));

        using var response = await ReportAsync(client, booking, wash, """{"description":"  A máquina parou de girar.  "}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal(["incidentId", "status"], body.EnumerateObject().Select(property => property.Name));
        Assert.Equal("reported", body.GetProperty("status").GetString());
        var incident = body.GetProperty("incidentId").GetGuid();
        Assert.Equal([$"{incident}|{WasherId}|{wash}|reported|A máquina parou de girar.|12:00:20.000000"], await database.RowsAsync(IncidentsSql));
        // Relato não muda item, máquina, saldo nem lançamentos.
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains($"item|{wash}|in_progress|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000", before);
        Assert.Contains($"washer-01|running|{wash}|false|12:00:10.000000", before);
        Assert.Equal(["31.10|1|1"], await database.RowsAsync(MoneySql));
        var detail = await BookingApi.JsonAsync(await client.GetAsync($"/api/v1/bookings/{booking}"));
        Assert.Equal(("in_progress", 3110L), (detail.GetProperty("items")[0].GetProperty("status").GetString(), detail.GetProperty("remainingBalanceCents").GetInt64()));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "202", method: "post"), body.GetRawText());
    }

    [Fact]
    public async Task R02_RepeatedReportReturnsTheOpenIncidentWithoutReplacingItsDescription()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(database, client, clock);
        clock.SetUtcNow(T0.AddSeconds(20));
        using var first = await ReportAsync(client, booking, wash, """{"description":"barulho"}""");
        clock.SetUtcNow(T0.AddSeconds(25));

        using var repeat = await ReportAsync(client, booking, wash, """{"description":"vazamento"}""");
        using var withoutBody = await ReportAsync(client, booking, wash, null);

        var incident = (await BookingApi.JsonAsync(first)).GetProperty("incidentId").GetGuid();
        foreach (var response in new[] { repeat, withoutBody })
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await BookingApi.JsonAsync(response);
            Assert.Equal((incident, "reported"), (body.GetProperty("incidentId").GetGuid(), body.GetProperty("status").GetString()));
        }

        Assert.Equal([$"{incident}|{WasherId}|{wash}|reported|barulho|12:00:20.000000"], await database.RowsAsync(IncidentsSql));
        Assert.Equal(["31.10|1|1"], await database.RowsAsync(MoneySql));
    }

    // Dois relatos simultâneos disputam o lock da wallet: o segundo encontra o relato do primeiro; o índice único
    // parcial é a defesa do banco contra um segundo relato aberto do mesmo item.
    [Fact]
    public async Task R03_TwoConcurrentReportsLeaveASingleOpenIncidentAlsoEnforcedByTheDatabase()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var setup = BookingApi.Create(database, clock, Settings);
        var (booking, wash) = await RunningWashAsync(database, setup.CreateClient(), clock);
        clock.SetUtcNow(T0.AddSeconds(20));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Settings);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);

        var firstTask = ReportAsync(client, booking, wash, """{"description":"um"}""");
        var secondTask = ReportAsync(client, booking, wash, """{"description":"dois"}""");
        await WaitUntilWaitingOnLockAsync(database, ApiApp, count: 2);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var a = await firstTask.WaitAsync(SignalTimeout);
        using var b = await secondTask.WaitAsync(SignalTimeout);

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (a.StatusCode, b.StatusCode));
        var incident = (await BookingApi.JsonAsync(a)).GetProperty("incidentId").GetGuid();
        Assert.Equal(incident, (await BookingApi.JsonAsync(b)).GetProperty("incidentId").GetGuid());
        var row = Assert.Single(await database.RowsAsync(IncidentsSql));
        Assert.StartsWith($"{incident}|{WasherId}|{wash}|reported|", row, StringComparison.Ordinal);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"INSERT INTO machine_incidents (id, machine_id, request_item_id, status, description, reported_at) VALUES (gen_random_uuid(), '{WasherId}', '{wash}', 'reported', NULL, now())"));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_machine_incidents_item_reported"), (duplicate.SqlState, duplicate.ConstraintName));
        var blank = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"INSERT INTO machine_incidents (id, machine_id, request_item_id, status, description, reported_at) VALUES (gen_random_uuid(), '{WasherId}', '{wash}', 'confirmed', '', now())"));
        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_machine_incidents_description"), (blank.SqlState, blank.ConstraintName));
        Assert.Single(await database.RowsAsync(IncidentsSql));
    }

    [Theory]
    [InlineData("pending_activation")]
    [InlineData("queued")]
    [InlineData("to_be_confirmed")]
    [InlineData("cancelled")]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task R04_ItemThatIsNotInProgressIsRefusedWith409WithoutRecording(string status)
    {
        var database = await ReadyAsync(postgres, status == "queued" ? $"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{WasherId}'" : null);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var purchase = await BookingApi.PurchaseAsync(database, clock, status == "pending_activation" ? ["wash", "dry"] : ["wash"]);
        var booking = purchase.BookingId;
        var wash = purchase.Items.Single(item => item.ServiceCode == "wash").ItemId;
        if (status is "completed" or "failed")
        {
            await ConfirmAsync(client, booking, wash, clock);
        }

        switch (status)
        {
            case "cancelled":
                await AssertOkAsync(client.PostAsync($"/api/v1/bookings/{booking}/items/{wash}/cancel", null));
                break;
            case "completed":
                await AssertOkAsync(client.PostAsync($"/api/v1/simulation/machines/{WasherId}/complete", null));
                break;
            case "failed":
                // Desfecho de falha preparado como fixture consistente (o comando que o produz é da tarefa seguinte).
                await database.ExecuteAsync(
                    $"""
                    UPDATE service_request_items SET status = 'failed' WHERE id = '{wash}';
                    UPDATE machines SET state = 'available', active_item_id = NULL WHERE id = '{WasherId}';
                    """);
                break;
        }

        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(20));

        using var response = await ReportAsync(client, booking, wash, """{"description":"parou"}""");

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(booking, wash));
        Assert.Equal("Só uma execução em andamento (in_progress) aceita relato de defeito; nada foi registrado.", problem.GetProperty("detail").GetString());
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains(before, row => row.StartsWith($"item|{wash}|{status}|", StringComparison.Ordinal));
        Assert.Empty(await database.RowsAsync(IncidentsSql));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "409", "application/problem+json", "post"), problem.GetRawText());
    }

    [Fact]
    public async Task R05_UnknownOrForeignIdsAreRejectedWithoutRecording()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(database, client, clock);
        var other = await BookingApi.PurchaseAsync(database, clock, ["dry"]);
        var before = await SnapshotAsync(database);
        var unknown = Guid.NewGuid();

        using var foreign = await ReportAsync(client, booking, other.Items[0].ItemId, null);
        using var missingItem = await ReportAsync(client, booking, unknown, null);
        using var missingBooking = await ReportAsync(client, unknown, wash, null);
        using var invalid = await client.PostAsync($"/api/v1/bookings/{booking}/items/x/report-issue", null);

        await ProblemAssert.HasProblemAsync(foreign, HttpStatusCode.NotFound, ApiProblems.ItemNotFound, Path(booking, other.Items[0].ItemId));
        await ProblemAssert.HasProblemAsync(missingItem, HttpStatusCode.NotFound, ApiProblems.ItemNotFound, Path(booking, unknown));
        var notFound = await ProblemAssert.HasProblemAsync(missingBooking, HttpStatusCode.NotFound, ApiProblems.BookingNotFound, Path(unknown, wash));
        await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, $"/api/v1/bookings/{booking}/items/x/report-issue");
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Empty(await database.RowsAsync(IncidentsSql));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "404", "application/problem+json", "post"), notFound.GetRawText());
    }

    // Corpo opcional: ausente, {}, null ou só espaços é relato sem descrição; até 1000 caracteres aceitos; excesso, campo
    // inesperado, tipo incorreto e Content-Type que não é JSON recusam sem registrar.
    [Theory]
    [InlineData("none", null, "null")]
    [InlineData("{}", null, "null")]
    [InlineData("""{"description":null}""", null, "null")]
    [InlineData("""{"description":"   "}""", null, "null")]
    [InlineData("max", null, "max")]
    [InlineData("over", "description", null)]
    [InlineData("""{"description":"ok","walletId":"00000000-0000-4000-8000-000000000001"}""", "walletId", null)]
    [InlineData("""{"description":5}""", "description", null)]
    [InlineData("text", "415", null)]
    public async Task R06_DescriptionIsOptionalLimitedTo1000CharactersAndStrict(string body, string? rejectedField, string? stored)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Settings);
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(database, client, clock);
        var before = await SnapshotAsync(database);
        var max = new string('d', 1000);
        clock.SetUtcNow(T0.AddSeconds(20));

        using var response = body switch
        {
            "none" => await ReportAsync(client, booking, wash, null),
            "max" => await ReportAsync(client, booking, wash, $$"""{"description":"{{max}}"}"""),
            "over" => await ReportAsync(client, booking, wash, $$"""{"description":"{{max}}d"}"""),
            "text" => await client.PostAsync(Path(booking, wash), new StringContent("parou", Encoding.UTF8, "text/plain")),
            _ => await ReportAsync(client, booking, wash, body),
        };

        Assert.Equal(before, await SnapshotAsync(database));
        if (rejectedField is "415")
        {
            await ProblemAssert.HasProblemAsync(response, HttpStatusCode.UnsupportedMediaType, ApiProblems.UnsupportedMediaType, Path(booking, wash));
            Assert.Empty(await database.RowsAsync(IncidentsSql));
            return;
        }

        if (rejectedField is not null)
        {
            var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.BadRequest, ApiProblems.ValidationError, Path(booking, wash));
            Assert.Equal([rejectedField], problem.GetProperty("errors").EnumerateObject().Select(property => property.Name));
            Assert.Empty(await database.RowsAsync(IncidentsSql));
            return;
        }

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var incident = (await BookingApi.JsonAsync(response)).GetProperty("incidentId").GetGuid();
        Assert.Equal([$"{incident}|{WasherId}|{wash}|reported|{(stored == "max" ? max : stored)}|12:00:20.000000"], await database.RowsAsync(IncidentsSql));
    }

    [Fact]
    public async Task R07_ThreePerMinuteLimitContractAndConsumerRouteOutsideTheSimulationBarrier()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, new Dictionary<string, string>(Demo) { ["RateLimiting:IssueReports:PermitLimit"] = "3" });
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(database, client, clock);
        var openApi = await OpenApiContract.FetchAsync(client);
        clock.SetUtcNow(T0.AddSeconds(20));

        var accepted = new List<HttpResponseMessage>();
        for (var call = 0; call < 3; call++)
        {
            accepted.Add(await ReportAsync(client, booking, wash, null));
        }

        using var limited = await ReportAsync(client, booking, wash, null);
        // Rota de consumidor: existe fora da barreira da simulação (Production sem DemoMode) e não confirma nem credita.
        await using var production = Host(database, "Production", "false");
        using var outside = await ReportAsync(production.CreateClient(), booking, wash, null);
        await using var withoutDemo = Host(database, "Development", "false");
        var plainOpenApi = await OpenApiContract.FetchAsync(withoutDemo.CreateClient());
        await using var down = BookingApi.Create(new TestDatabase(ApiFactory.UnreachableDatabase), clock, Settings);
        using var unavailable = await ReportAsync(down.CreateClient(), booking, wash, null);
        await using var defaults = BookingApi.Create(database, clock);

        var incident = Assert.Single(await database.RowsAsync("SELECT id::text FROM machine_incidents"));
        foreach (var response in accepted.Append(outside))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await BookingApi.JsonAsync(response);
            Assert.Equal(incident, body.GetProperty("incidentId").GetString());
            JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "202", method: "post"), body.GetRawText());
            response.Dispose();
        }

        var problem = await ProblemAssert.HasProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, Path(booking, wash));
        Assert.InRange(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture), 1, 60);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "429", "application/problem+json", "post"), problem.GetRawText());
        await ProblemAssert.HasProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path(booking, wash));
        Assert.Equal(3, defaults.Services.GetRequiredService<IOptionsMonitor<RateLimitPolicyOptions>>().Get(ApiRateLimits.IssueReports).PermitLimit);
        Assert.Equal(["31.10|1|1"], await database.RowsAsync(MoneySql));

        foreach (var document in new[] { openApi, plainOpenApi })
        {
            Assert.Empty(OpenApiContract.Problems(document, new RequiredOperation(Route, "POST", "ReportItemIssue")));
        }

        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Post];
        Assert.Equal(["Bookings"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["202", "400", "404", "409", "415", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["bookingId", "itemId"], operation.Parameters!.Select(parameter => parameter.Name));
        Assert.All(operation.Parameters!, parameter => Assert.Equal(("uuid", ParameterLocation.Path), (parameter.Schema!.Format, parameter.In!.Value)));
        Assert.False(operation.RequestBody!.Required);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "202", method: "post"), JsonSchemaContract.Example(openApi, Route, "202", method: "post"));
        var request = JsonSchemaContract.RequestPointer(Route);
        JsonSchemaContract.AssertValid(openApi, request, $$"""{"description":"{{new string('d', 1000)}}"}""");
        JsonSchemaContract.AssertValid(openApi, request, """{"description":null}""");
        JsonSchemaContract.AssertInvalid(openApi, request, $$"""{"description":"{{new string('d', 1001)}}"}""");
        var reported = JsonSchemaContract.ResponsePointer(Route, "202", method: "post");
        JsonSchemaContract.AssertInvalid(openApi, reported, $$"""{"incidentId":"{{incident}}","status":"confirmed"}""");
        JsonSchemaContract.AssertInvalid(openApi, reported, """{"incidentId":"x","status":"reported"}""");
    }

    private static string Path(Guid bookingId, Guid itemId) => $"/api/v1/bookings/{bookingId}/items/{itemId}/report-issue";

    private static Task<HttpResponseMessage> ReportAsync(HttpClient client, Guid bookingId, Guid itemId, string? json) =>
        client.PostAsync(Path(bookingId, itemId), json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>Compra wash às 12:00:00 (oferta da lavadora) confirmada às 12:00:10: execução real in_progress.</summary>
    private static async Task<(Guid Booking, Guid Item)> RunningWashAsync(TestDatabase database, HttpClient client, FakeTimeProvider clock)
    {
        clock.SetUtcNow(T0);
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        await ConfirmAsync(client, booking.BookingId, booking.Items[0].ItemId, clock);
        return (booking.BookingId, booking.Items[0].ItemId);
    }

    private static async Task ConfirmAsync(HttpClient client, Guid booking, Guid item, FakeTimeProvider clock)
    {
        clock.SetUtcNow(T0.AddSeconds(10));
        await AssertOkAsync(client.PostAsync($"/api/v1/bookings/{booking}/items/{item}/confirm", null));
    }

    private static async Task AssertOkAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
