using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Cicclo.Api.Bookings;
using Cicclo.Api.Bookings.CreateBooking;
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
/// POST /api/v1/simulation/machines/{machineId}/breakdown sobre PostgreSQL 18.1 real (SIM-04/05/07/08, FIN-12,
/// REFUND-01/02/06, OPS-11): execução real → failed, out_of_service e refund do preço histórico com crédito no mesmo
/// commit; sintética → out_of_service sem item nem ledger; um único desfecho contra complete e contra outra falha;
/// falhas revertem tudo; fila paga preservada e cancelável; compra serializada com a perda da última capacidade.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E06")]
[Trait("Task", "T5")]
public sealed class BreakdownMachineTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/simulation/machines/{machineId}/breakdown";
    private const string ApiApp = "cicclo-e06-failure-api";
    private const string PurchaseApp = "cicclo-e06-failure-purchase";
    private const string HolderApp = "cicclo-e06-failure-holder";
    private const string LedgerSql =
        "SELECT kind || '|' || coalesce(request_item_id::text, 'null') || '|' || amount || '|' || balance_after || '|' || to_char(created_at AT TIME ZONE 'UTC', 'HH24:MI:SS') FROM wallet_transactions ORDER BY created_at, kind";

    private static readonly string WalletLock = $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE";

    [Fact]
    public async Task B01_RealExecutionFailsWithOutOfServiceAndASingleHistoricalRefund()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (booking, wash) = await RunningWashAsync(database, client, clock);
        clock.SetUtcNow(T0.AddSeconds(40));

        using var response = await client.PostAsync(Path(WasherId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal($"{WasherId}|washer-01|washer|out_of_service|null|False", Describe(body.GetProperty("machine")));
        var item = body.GetProperty("item");
        Assert.Equal(
            (wash, "wash", 1890L, "failed"),
            (item.GetProperty("itemId").GetGuid(), item.GetProperty("serviceCode").GetString(), item.GetProperty("unitPriceCents").GetInt64(), item.GetProperty("status").GetString()));
        Assert.Contains($"item|{wash}|failed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000", await database.RowsAsync(StateSql));
        Assert.Equal("washer-01|out_of_service|null|false|12:00:40.000000", (await database.RowsAsync(MachinesSql))[1]);
        Assert.Equal(["debit|null|18.90|31.10|12:00:00", $"refund|{wash}|18.90|50.00|12:00:40"], await database.RowsAsync(LedgerSql));
        Assert.Equal(["50.00|2|1"], await database.RowsAsync(MoneySql));
        var detail = await BookingApi.JsonAsync(await client.GetAsync($"/api/v1/bookings/{booking}"));
        Assert.Equal(("failed", 5000L), (detail.GetProperty("items")[0].GetProperty("status").GetString(), detail.GetProperty("remainingBalanceCents").GetInt64()));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), body.GetRawText());
    }

    [Fact]
    public async Task B02_SyntheticOccupationFailsWithoutItemRefundOrLedger()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock(T0.AddSeconds(5));
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        await AssertOkAsync(client.PostAsync(OccupyMachineTests.Path(WasherId), null));
        clock.SetUtcNow(T0.AddSeconds(20));

        using var response = await client.PostAsync(Path(WasherId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal($"{WasherId}|washer-01|washer|out_of_service|null|False", Describe(body.GetProperty("machine")));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("item").ValueKind);
        Assert.Equal("washer-01|out_of_service|null|false|12:00:20.000000", (await database.RowsAsync(MachinesSql))[1]);
        Assert.Equal(["50.00|0|0"], await database.RowsAsync(MoneySql));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items"));
    }

    // REFUND-06: o catálogo mudou durante a execução; o estorno devolve o preço cobrado, não o atual.
    [Fact]
    public async Task B03_RefundUsesTheHistoricalPriceAfterTheCatalogChanged()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (_, wash) = await RunningWashAsync(database, client, clock);
        await database.ExecuteAsync("UPDATE services SET current_price = 25.00 WHERE code = 'wash'");
        clock.SetUtcNow(T0.AddSeconds(30));

        using var response = await client.PostAsync(Path(WasherId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["debit|null|18.90|31.10|12:00:00", $"refund|{wash}|18.90|50.00|12:00:30"], await database.RowsAsync(LedgerSql));
        Assert.Equal("50.00", await database.ScalarAsync<string>("SELECT balance::text FROM wallets"));
    }

    [Theory]
    [InlineData("available")]
    [InlineData("reserved")]
    public async Task B04_MachineThatIsNotRunningIsRefusedWith409WithoutMutation(string state)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        if (state == "reserved")
        {
            await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        }

        var before = await SnapshotAsync(database);
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(Path(WasherId), null);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(WasherId));
        Assert.Equal("A máquina não está em execução; não há execução para falhar e nada foi alterado.", problem.GetProperty("detail").GetString());
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains(before, row => row.StartsWith($"washer-01|{state}|", StringComparison.Ordinal));
        var openApi = await OpenApiContract.FetchAsync(client);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "409", "application/problem+json", "post"), problem.GetRawText());
    }

    [Fact]
    public async Task B05_RepeatedBreakdownIsRefusedWithoutASecondRefund()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (_, wash) = await RunningWashAsync(database, client, clock);
        clock.SetUtcNow(T0.AddSeconds(30));
        await AssertOkAsync(client.PostAsync(Path(WasherId), null));
        var after = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(35));

        using var repeat = await client.PostAsync(Path(WasherId), null);

        await ProblemAssert.HasProblemAsync(repeat, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(WasherId));
        Assert.Equal(after, await SnapshotAsync(database));
        Assert.Equal(["debit|null|18.90|31.10|12:00:00", $"refund|{wash}|18.90|50.00|12:00:30"], await database.RowsAsync(LedgerSql));
    }

    // SIM-05: complete e breakdown da mesma execução disputam o lock da wallet; em qualquer ordem só há um desfecho:
    // completed sem refund ou failed com refund único, e o perdedor recebe 409 sem efeito.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task B06_CompleteAndBreakdownOfTheSameRunHaveASingleOutcome(bool completeFirst)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using (var setup = BookingApi.Create(database, clock, Demo))
        {
            await RunningWashAsync(database, setup.CreateClient(), clock);
        }

        var wash = await database.ScalarAsync<Guid>("SELECT id FROM service_request_items");
        clock.SetUtcNow(T0.AddSeconds(30));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Demo);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);
        Task<HttpResponseMessage> Complete() => client.PostAsync($"/api/v1/simulation/machines/{WasherId}/complete", null);
        Task<HttpResponseMessage> Breakdown() => client.PostAsync(Path(WasherId), null);

        var firstTask = completeFirst ? Complete() : Breakdown();
        await WaitUntilWaitingOnLockAsync(database, ApiApp);
        var secondTask = completeFirst ? Breakdown() : Complete();
        await WaitUntilWaitingOnLockAsync(database, ApiApp, count: 2);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var first = await firstTask.WaitAsync(SignalTimeout);
        using var second = await secondTask.WaitAsync(SignalTimeout);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var rows = await database.RowsAsync(StateSql);
        var ledger = await database.RowsAsync(LedgerSql);
        if (completeFirst)
        {
            Assert.Contains($"item|{wash}|completed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000", rows);
            Assert.Equal("washer-01|available|null|false|12:00:30.000000", (await database.RowsAsync(MachinesSql))[1]);
            Assert.Equal(["debit|null|18.90|31.10|12:00:00"], ledger);
        }
        else
        {
            Assert.Contains($"item|{wash}|failed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:10.000000", rows);
            Assert.Equal("washer-01|out_of_service|null|false|12:00:30.000000", (await database.RowsAsync(MachinesSql))[1]);
            Assert.Equal(["debit|null|18.90|31.10|12:00:00", $"refund|{wash}|18.90|50.00|12:00:30"], ledger);
        }
    }

    // REFUND-02: duas confirmações simultâneas da mesma falha produzem um único refund e um único crédito.
    [Fact]
    public async Task B07_TwoConcurrentBreakdownsProduceASingleRefund()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using (var setup = BookingApi.Create(database, clock, Demo))
        {
            await RunningWashAsync(database, setup.CreateClient(), clock);
        }

        var wash = await database.ScalarAsync<Guid>("SELECT id FROM service_request_items");
        clock.SetUtcNow(T0.AddSeconds(30));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Demo);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);

        var firstTask = client.PostAsync(Path(WasherId), null);
        var secondTask = client.PostAsync(Path(WasherId), null);
        await WaitUntilWaitingOnLockAsync(database, ApiApp, count: 2);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var a = await firstTask.WaitAsync(SignalTimeout);
        using var b = await secondTask.WaitAsync(SignalTimeout);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], new[] { a.StatusCode, b.StatusCode }.Order());
        Assert.Equal(["debit|null|18.90|31.10|12:00:00", $"refund|{wash}|18.90|50.00|12:00:30"], await database.RowsAsync(LedgerSql));
        Assert.Equal(["50.00|2|1"], await database.RowsAsync(MoneySql));
    }

    // Falha no crédito, no lançamento ou no commit desfaz também o failed e o out_of_service: nada parcial.
    [Theory]
    [InlineData("CREATE TRIGGER fail_breakdown BEFORE INSERT ON wallet_transactions FOR EACH ROW EXECUTE FUNCTION fail_breakdown();")]
    [InlineData("CREATE TRIGGER fail_breakdown BEFORE UPDATE ON wallets FOR EACH ROW EXECUTE FUNCTION fail_breakdown();")]
    [InlineData("CREATE CONSTRAINT TRIGGER fail_breakdown AFTER INSERT ON wallet_transactions DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_breakdown();")]
    public async Task B08_FailureInTheCreditLedgerOrCommitRevertsEverything(string trigger)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (_, wash) = await RunningWashAsync(database, client, clock);
        await database.ExecuteAsync(
            $"""
            CREATE FUNCTION fail_breakdown() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada SELECT secreto'; END $$;
            {trigger}
            """);
        var before = await SnapshotAsync(database);
        clock.SetUtcNow(T0.AddSeconds(30));

        using var response = await client.PostAsync(Path(WasherId), null);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, Path(WasherId));
        Assert.DoesNotContain("secreto", problem.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Contains(before, row => row.StartsWith($"item|{wash}|in_progress|", StringComparison.Ordinal));
        Assert.Contains($"washer-01|running|{wash}|false|12:00:10.000000", before);
        Assert.Equal(["31.10|1|1"], await database.RowsAsync(MoneySql));
    }

    // FIN-12 com o endpoint real: a compra e a perda da última lavadora disputam o lock da wallet. Falha antes →
    // a compra vê a capacidade perdida e recusa o lote inteiro sem débito; compra antes → aceita, e os itens pagos
    // continuam pendentes depois da falha.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task B09_PurchaseAndLossOfTheLastMachineAreSerialized(bool breakdownFirst)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Demo);
        using var client = factory.CreateClient();
        await AssertOkAsync(client.PostAsync(OccupyMachineTests.Path(WasherId), null));
        clock.SetUtcNow(T0.AddSeconds(10));
        var (holder, transaction) = await HoldAsync(database, HolderApp, WalletLock);
        var intent = BookingIntent.Create(["wash", "dry"], null, 3980, 10).Intent!;
        Task<CreateBookingResult> Purchase() => Task.Run(() =>
            BookingApi.UseCase(new PostgresCreateBookingSessions(database.ContextFactory(PurchaseApp)), clock).ExecuteAsync(Guid.NewGuid(), intent, CancellationToken.None));
        Task<HttpResponseMessage>? breakdown = null;
        Task<CreateBookingResult>? purchase = null;

        if (breakdownFirst)
        {
            breakdown = client.PostAsync(Path(WasherId), null);
            await WaitUntilWaitingOnLockAsync(database, ApiApp);
            purchase = Purchase();
            await WaitUntilWaitingOnLockAsync(database, PurchaseApp);
        }
        else
        {
            purchase = Purchase();
            await WaitUntilWaitingOnLockAsync(database, PurchaseApp);
            breakdown = client.PostAsync(Path(WasherId), null);
            await WaitUntilWaitingOnLockAsync(database, ApiApp);
        }

        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var failed = await breakdown.WaitAsync(SignalTimeout);
        var result = await purchase.WaitAsync(SignalTimeout);

        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal("washer-01|out_of_service|null|false|12:00:10.000000", (await database.RowsAsync(MachinesSql))[1]);
        if (breakdownFirst)
        {
            Assert.Equal((CreateBookingOutcome.Rejected, BookingRejection.NoOperationalMachine), (result.Outcome, result.Rejection));
            Assert.Equal(["50.00|0|0"], await database.RowsAsync(MoneySql));
            Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM service_request_items"));
        }
        else
        {
            Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
            Assert.Equal(["10.20|1|1"], await database.RowsAsync(MoneySql));
            Assert.Equal(["dry|pending_activation", "wash|pending_activation"], await database.RowsAsync("SELECT service_code || '|' || status FROM service_request_items ORDER BY service_code"));
        }
    }

    // SIM-07: com a única lavadora fora de serviço, itens pagos na fila ou pendentes continuam consultáveis e
    // canceláveis com refund; uma compra nova que depende dela é recusada sem débito.
    [Fact]
    public async Task B10_PaidQueuedAndPendingItemsArePreservedAndCancellableWhileOutOfService()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        var (_, a) = await RunningWashAsync(database, client, clock);
        clock.SetUtcNow(T0.AddSeconds(15));
        var queued = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(16));
        var pending = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var b = queued.Items[0].ItemId;
        var c = pending.Items.Single(item => item.ServiceCode == "wash").ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));

        await AssertOkAsync(client.PostAsync(Path(WasherId), null));

        var rows = await database.RowsAsync(StateSql);
        Assert.Contains($"item|{b}|queued|2|12:00:15.000000|null|null|null|null", rows);
        Assert.Contains($"item|{c}|pending_activation|null|null|null|null|null|null", rows);
        var detail = await BookingApi.JsonAsync(await client.GetAsync($"/api/v1/bookings/{queued.BookingId}"));
        Assert.Equal(("queued", 1), (detail.GetProperty("items")[0].GetProperty("status").GetString(), detail.GetProperty("items")[0].GetProperty("queuePosition").GetInt32()));
        using var purchase = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings")
        {
            Content = new StringContent("""{"serviceCodes":["wash","dry"],"expectedTotalCents":3980}""", Encoding.UTF8, "application/json"),
        };
        purchase.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var refused = await client.SendAsync(purchase);
        await ProblemAssert.HasProblemAsync(refused, HttpStatusCode.Conflict, ApiProblems.NoOperationalMachine, "/api/v1/bookings");
        clock.SetUtcNow(T0.AddSeconds(31));
        using var cancelQueued = await client.PostAsync($"/api/v1/bookings/{queued.BookingId}/items/{b}/cancel", null);
        clock.SetUtcNow(T0.AddSeconds(32));
        using var cancelPending = await client.PostAsync($"/api/v1/bookings/{pending.BookingId}/items/{c}/cancel", null);

        foreach (var cancelled in new[] { cancelQueued, cancelPending })
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            Assert.Equal(1890L, (await BookingApi.JsonAsync(cancelled)).GetProperty("refundedAmountCents").GetInt64());
        }

        Assert.Equal(
            [
                "debit|null|18.90|81.10|12:00:00",
                "debit|null|18.90|62.20|12:00:15",
                "debit|null|39.80|22.40|12:00:16",
                $"refund|{a}|18.90|41.30|12:00:30",
                $"refund|{b}|18.90|60.20|12:00:31",
                $"refund|{c}|18.90|79.10|12:00:32",
            ],
            await database.RowsAsync(LedgerSql));
        Assert.Equal("washer-01|out_of_service|null|false|12:00:30.000000", (await database.RowsAsync(MachinesSql))[1]);
    }

    [Fact]
    public async Task B11_IdsEnvironmentContractAndRefundMetricMatchTheSpec()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Demo);
        using var client = factory.CreateClient();
        using var counter = new RefundCounter(factory.Services.GetRequiredService<IMeterFactory>());
        await RunningWashAsync(database, client, clock);
        await AssertOkAsync(client.PostAsync(OccupyMachineTests.Path(DryerId), null));
        await using var withoutDemo = Host(database, "Development", "false");
        using var plain = withoutDemo.CreateClient();
        var unknown = Guid.NewGuid().ToString();
        clock.SetUtcNow(T0.AddSeconds(30));
        var before = await SnapshotAsync(database);

        using var invalid = await client.PostAsync("/api/v1/simulation/machines/x/breakdown", null);
        using var missing = await client.PostAsync(Path(unknown), null);
        using var unmapped = await plain.PostAsync(Path(WasherId), null);
        var plainOpenApi = await OpenApiContract.FetchAsync(plain);

        await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, "/api/v1/simulation/machines/x/breakdown");
        var notFound = await ProblemAssert.HasProblemAsync(missing, HttpStatusCode.NotFound, ApiProblems.MachineNotFound, Path(unknown));
        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
        Assert.DoesNotContain("breakdown", plainOpenApi, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await SnapshotAsync(database));

        // Métrica: a falha real conta uma vez com reason=machine_failure; a sintética e a repetição não contam.
        await AssertOkAsync(client.PostAsync(Path(WasherId), null));
        await AssertOkAsync(client.PostAsync(Path(DryerId), null));
        using var repeat = await client.PostAsync(Path(WasherId), null);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal(["reason=machine_failure"], counter.Measurements);

        var openApi = await OpenApiContract.FetchAsync(client);
        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "POST", "BreakdownMachine")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Post];
        Assert.Equal(["Simulation"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "400", "404", "409", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(operation.RequestBody);
        var parameter = Assert.Single(operation.Parameters!);
        Assert.Equal(("machineId", "uuid", ParameterLocation.Path), (parameter.Name, parameter.Schema!.Format, parameter.In!.Value));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), JsonSchemaContract.Example(openApi, Route, "200", method: "post"));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "404", "application/problem+json", "post"), notFound.GetRawText());
    }

    private static string Path(string machineId) => $"/api/v1/simulation/machines/{machineId}/breakdown";

    /// <summary>Compra wash às 12:00:00 (oferta da lavadora) confirmada às 12:00:10: execução real in_progress.</summary>
    private static async Task<(Guid Booking, Guid Item)> RunningWashAsync(TestDatabase database, HttpClient client, FakeTimeProvider clock)
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
