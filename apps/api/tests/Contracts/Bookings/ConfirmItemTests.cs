using System.Globalization;
using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;
using ReconcileOffersUseCase = Cicclo.Api.Laundry.ReconcileOffers.ReconcileOffers;

namespace Cicclo.Api.Tests.Contracts.Bookings;

/// <summary>
/// POST /api/v1/bookings/{bookingId}/items/{itemId}/confirm sobre PostgreSQL 18.1 real com FakeTimeProvider
/// (QUEUE-08..14, HTTP-04/06..08, OPS-04): prazo estrito now &lt; expiresAt, 410 só depois do commit da reentrada,
/// um vencedor entre confirmar e expirar, repetição em execução sem reinício e recusas sem mutação.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E04")]
[Trait("Task", "T6")]
public sealed class ConfirmItemTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/bookings/{bookingId}/items/{itemId}/confirm";
    private const string ApiApp = "cicclo-e04-api";
    private const string ReconcileApp = "cicclo-e04-reconcile";
    private static readonly Dictionary<string, string> Unlimited = new() { ["RateLimiting:ItemCommands:PermitLimit"] = "1000" };

    [Theory]
    [InlineData(59_000_000L)]
    [InlineData(59_999_999L)]
    public async Task C01_OfferConfirmedBeforeTheDeadlineStartsTheItemAndRunsTheMachineWithoutDebit(long elapsedMicroseconds)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddTicks(elapsedMicroseconds * 10));
        await using var factory = BookingApi.Create(database, clock, Unlimited);

        using var response = await ConfirmAsync(factory.CreateClient(), booking.BookingId, wash);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await BookingApi.JsonAsync(response);
        Assert.Equal((wash, "in_progress"), (item.GetProperty("itemId").GetGuid(), item.GetProperty("status").GetString()));
        Assert.Equal(Guid.Parse(WasherId), item.GetProperty("machineId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("expiresAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("queuePosition").ValueKind);
        var started = LaundryScenarioMicros(T0.AddTicks(elapsedMicroseconds * 10));
        Assert.Equal(
            [$"item|{wash}|in_progress|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|{started}", $"machine|washer-01|running|{wash}|{started}", "wallet|31.10|1"],
            (await database.RowsAsync(StateSql)).Where(row => !row.StartsWith("machine|dryer-01|", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    public async Task C02_AtOrAfterTheDeadlineIs410AfterTheRequeueIsCommittedEvenWithoutTheWorker(int seconds)
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        var first = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(1));
        var second = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var a = first.Items[0].ItemId;
        var b = second.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(seconds));
        // O worker do host nunca disparou: o relógio controlado não avança pelos intervalos.
        Assert.Contains($"item|{a}|to_be_confirmed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|null", await database.RowsAsync(StateSql));
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();

        using var response = await ConfirmAsync(client, first.BookingId, a);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Gone, ApiProblems.MachineOfferExpired, Path(first.BookingId, a));
        Assert.Equal("A oferta venceu antes da confirmação. O item voltou ao fim da fila e está na posição 1.", problem.GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("errors").ValueKind);
        var at = $"12:01:{seconds - 60:00}.000000";
        var rows = await database.RowsAsync(StateSql);
        Assert.Contains($"item|{a}|queued|3|{at}|null|null|null|null", rows);
        Assert.Contains($"item|{b}|to_be_confirmed|2|12:00:01.000000|{WasherId}|{at}|12:02:{seconds - 60:00}.000000|null", rows);
        Assert.Contains($"machine|washer-01|reserved|{b}|{at}", rows);
        Assert.Contains("wallet|62.20|2", rows);
        var detail = await BookingApi.JsonAsync(await client.GetAsync($"/api/v1/bookings/{first.BookingId}"));
        Assert.Equal(("queued", 1), (detail.GetProperty("items")[0].GetProperty("status").GetString(), detail.GetProperty("items")[0].GetProperty("queuePosition").GetInt32()));
    }

    // G2 (decisão de contrato): o 410 informa a situação atual depois da reentrada; sozinho na fila, o item já recebeu
    // nova oferta de 60 s na mesma transação.
    [Fact]
    public async Task C03_ExpiredOfferAloneInTheQueueIs410WithTheNewOfferInTheDetail()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(61).AddTicks(1_234_560));
        await using var factory = BookingApi.Create(database, clock, Unlimited);

        using var response = await ConfirmAsync(factory.CreateClient(), booking.BookingId, wash);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Gone, ApiProblems.MachineOfferExpired, Path(booking.BookingId, wash));
        Assert.Equal(
            $"A oferta venceu antes da confirmação. Sem ninguém à frente, o item recebeu nova oferta da máquina {WasherId} até 2026-10-21T12:02:01.123456Z.",
            problem.GetProperty("detail").GetString());
        Assert.Contains(
            $"item|{wash}|to_be_confirmed|2|12:01:01.123456|{WasherId}|12:01:01.123456|12:02:01.123456|null",
            await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task C04_ConcurrentConfirmAndWorkerReconciliationAfterTheDeadlineHaveASingleWinner()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(60));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Unlimited);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, "cicclo-e04-holder", $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE");

        // A confirmação entra primeiro na fila do lock da wallet; o worker espera atrás dela.
        var confirm = ConfirmAsync(client, booking.BookingId, wash);
        await WaitUntilWaitingOnLockAsync(database, ApiApp);
        var worker = Task.Run(() => new ReconcileOffersUseCase(Sessions(database, ReconcileApp), Coordinator(clock), Metrics(), NullLogger<ReconcileOffersUseCase>.Instance)
            .ExecuteAsync(CancellationToken.None));
        await WaitUntilWaitingOnLockAsync(database, ReconcileApp);
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var response = await confirm.WaitAsync(SignalTimeout);
        var reconciled = await worker.WaitAsync(SignalTimeout);

        await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Gone, ApiProblems.MachineOfferExpired, Path(booking.BookingId, wash));
        Assert.Equal(0, reconciled.Sum(change => change.Expired.Count));
        Assert.Contains($"item|{wash}|to_be_confirmed|2|12:01:00.000000|{WasherId}|12:01:00.000000|12:02:00.000000|null", await database.RowsAsync(StateSql));
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
    }

    [Fact]
    public async Task C05_WorkerWinningTheRaceBeforeTheDeadlineIsImpossibleSoTheConfirmationStartsOnce()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(59));
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();

        var reconciled = await new ReconcileOffersUseCase(Sessions(database, ReconcileApp), Coordinator(clock), Metrics(), NullLogger<ReconcileOffersUseCase>.Instance)
            .ExecuteAsync(CancellationToken.None);
        using var confirmed = await ConfirmAsync(client, booking.BookingId, wash);
        clock.SetUtcNow(T0.AddSeconds(61));
        var after = await new ReconcileOffersUseCase(Sessions(database, ReconcileApp), Coordinator(clock), Metrics(), NullLogger<ReconcileOffersUseCase>.Instance)
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal((0, 0), (reconciled.Sum(change => change.Expired.Count), after.Sum(change => change.Expired.Count)));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Contains($"item|{wash}|in_progress|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|12:00:59.000000", await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task C06_RepeatedConfirmationOfAStartedItemReturns200WithoutRestartingIt()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();
        using (var first = await ConfirmAsync(client, booking.BookingId, wash))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var before = await database.RowsAsync(StateSql);
        clock.SetUtcNow(T0.AddMinutes(10));

        using var repeat = await ConfirmAsync(client, booking.BookingId, wash);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var item = await BookingApi.JsonAsync(repeat);
        Assert.Equal(("in_progress", Guid.Parse(WasherId)), (item.GetProperty("status").GetString(), item.GetProperty("machineId").GetGuid()));
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains(before, row => row.EndsWith("|12:00:30.000000", StringComparison.Ordinal) && row.StartsWith($"item|{wash}|in_progress|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task C07_InvalidIdsForeignItemsAndItemsWithoutAPendingOfferAreRejectedWithoutMutation()
    {
        var database = await ReadyAsync(postgres, $"UPDATE wallets SET balance = 100.00; UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{DryerId}';");
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var other = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var dry = booking.Items.Single(item => item.ServiceCode == "dry").ItemId;
        var wash = booking.Items.Single(item => item.ServiceCode == "wash").ItemId;
        await ActivateAsync(Sessions(database, "cicclo-e04-setup"), Coordinator(clock), dry);
        var before = await database.RowsAsync(StateSql);
        Assert.Contains(before, row => row.StartsWith($"item|{dry}|queued|", StringComparison.Ordinal));
        Assert.Contains(before, row => row.StartsWith($"item|{wash}|pending_activation|", StringComparison.Ordinal));
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        using var pending = await ConfirmAsync(client, booking.BookingId, wash);
        using var queued = await ConfirmAsync(client, booking.BookingId, dry);
        using var foreign = await ConfirmAsync(client, booking.BookingId, other.Items[0].ItemId);
        using var missingItem = await ConfirmAsync(client, booking.BookingId, unknown);
        using var missingBooking = await ConfirmAsync(client, unknown, wash);
        using var invalid = await client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/x/confirm", null);

        await ProblemAssert.HasProblemAsync(pending, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(booking.BookingId, wash));
        await ProblemAssert.HasProblemAsync(queued, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(booking.BookingId, dry));
        await ProblemAssert.HasProblemAsync(foreign, HttpStatusCode.NotFound, ApiProblems.ItemNotFound, Path(booking.BookingId, other.Items[0].ItemId));
        await ProblemAssert.HasProblemAsync(missingItem, HttpStatusCode.NotFound, ApiProblems.ItemNotFound, Path(booking.BookingId, unknown));
        await ProblemAssert.HasProblemAsync(missingBooking, HttpStatusCode.NotFound, ApiProblems.BookingNotFound, Path(unknown, wash));
        await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, $"/api/v1/bookings/{booking.BookingId}/items/x/confirm");
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains($"item|{other.Items[0].ItemId}|to_be_confirmed|", string.Join('\n', before), StringComparison.Ordinal);
    }

    [Fact]
    public async Task C08_OperationProblemsSharedFifteenPerMinuteLimitAndUnavailableDatabaseMatchTheContract()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var expired = await BookingApi.PurchaseAsync(database, clock, ["dry"]);
        var wash = booking.Items.Single(item => item.ServiceCode == "wash").ItemId;
        // O relógio só anda antes de o host subir: o worker do host não dispara e não reoferta a secadora vencida.
        clock.SetUtcNow(T0.AddSeconds(90));
        await using var factory = BookingApi.Create(database, clock, new Dictionary<string, string> { ["RateLimiting:ItemCommands:PermitLimit"] = "4" });
        using var client = factory.CreateClient();
        var openApi = await OpenApiContract.FetchAsync(client);
        using var activated = await client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{wash}/activate", null);
        using var ok = await ConfirmAsync(client, booking.BookingId, wash);
        using var conflict = await ConfirmAsync(client, booking.BookingId, booking.Items.Single(item => item.ServiceCode == "dry").ItemId);
        using var gone = await ConfirmAsync(client, expired.BookingId, expired.Items[0].ItemId);
        using var limited = await ConfirmAsync(client, booking.BookingId, wash);
        await using var down = BookingApi.Create(new TestDatabase(ApiFactory.UnreachableDatabase), clock, Unlimited);
        using var unavailable = await ConfirmAsync(down.CreateClient(), booking.BookingId, wash);

        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "POST", "ConfirmBookingItem")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Post];
        Assert.Equal(["Bookings"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "400", "404", "409", "410", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(operation.RequestBody);
        Assert.Equal(["bookingId", "itemId"], operation.Parameters!.Select(parameter => parameter.Name));
        Assert.All(operation.Parameters!, parameter => Assert.Equal(("uuid", ParameterLocation.Path), (parameter.Schema!.Format, parameter.In!.Value)));
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var started = await BookingApi.JsonAsync(ok);
        Assert.Equal((wash, "in_progress"), (started.GetProperty("itemId").GetGuid(), started.GetProperty("status").GetString()));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), started.GetRawText());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), JsonSchemaContract.Example(openApi, Route, "200", method: "post"));
        foreach (var (status, response) in new[] { ("409", conflict), ("410", gone) })
        {
            Assert.Equal(status, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, status, "application/problem+json", "post"), await response.Content.ReadAsStringAsync());
        }

        // activate e confirm compartilham a política de comandos de item (15/min por padrão; 4 neste host).
        var problem = await ProblemAssert.HasProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, Path(booking.BookingId, wash));
        Assert.InRange(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture), 1, 60);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "429", "application/problem+json", "post"), problem.GetRawText());
        await ProblemAssert.HasProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path(booking.BookingId, wash));
    }

    // Outra ordem da corrida: o worker vence e devolve a oferta vencida à fila. Sem oferta corrente, a confirmação
    // posterior é transição inválida (409, sem mutação); a máquina segue com o próximo, que confirma normalmente.
    [Fact]
    public async Task C09_WorkerExpiringTheOfferFirstLeavesNothingToConfirmSoTheLateConfirmationIs409WithoutMutation()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        var first = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(1));
        var second = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var a = first.Items[0].ItemId;
        var b = second.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(60));
        var reconciled = await new ReconcileOffersUseCase(Sessions(database, ReconcileApp), Coordinator(clock), Metrics(), NullLogger<ReconcileOffersUseCase>.Instance)
            .ExecuteAsync(CancellationToken.None);
        var before = await database.RowsAsync(StateSql);
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();

        using var late = await ConfirmAsync(client, first.BookingId, a);

        Assert.Equal(1, reconciled.Sum(change => change.Expired.Count));
        await ProblemAssert.HasProblemAsync(late, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(first.BookingId, a));
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains($"item|{a}|queued|3|12:01:00.000000|null|null|null|null", before);
        using var next = await ConfirmAsync(client, second.BookingId, b);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Contains($"item|{b}|in_progress|2|12:00:01.000000|{WasherId}|12:01:00.000000|12:02:00.000000|12:01:00.000000", await database.RowsAsync(StateSql));
    }

    // Instante da decisão depois dos locks (D02) no caminho HTTP: o request chega aos 59 s, espera o lock da wallet e o
    // relógio passa a 61 s; a decisão usa 61 s, então responde 410 e a reentrada fica commitada.
    [Fact]
    public async Task C10_ConfirmationWaitingOnTheLockWhileTheDeadlinePassesIs410()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(59));
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Unlimited);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, "cicclo-e04-holder", $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE");

        var confirm = ConfirmAsync(client, booking.BookingId, wash);
        await WaitUntilWaitingOnLockAsync(database, ApiApp);
        clock.SetUtcNow(T0.AddSeconds(61));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var response = await confirm.WaitAsync(SignalTimeout);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Gone, ApiProblems.MachineOfferExpired, Path(booking.BookingId, wash));
        Assert.Equal(
            $"A oferta venceu antes da confirmação. Sem ninguém à frente, o item recebeu nova oferta da máquina {WasherId} até 2026-10-21T12:02:01Z.",
            problem.GetProperty("detail").GetString());
        Assert.Contains($"item|{wash}|to_be_confirmed|2|12:01:01.000000|{WasherId}|12:01:01.000000|12:02:01.000000|null", await database.RowsAsync(StateSql));
    }

    private static string Path(Guid bookingId, Guid itemId) => $"/api/v1/bookings/{bookingId}/items/{itemId}/confirm";

    private static string LaundryScenarioMicros(DateTimeOffset instant) => Micros(instant);

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Guid bookingId, Guid itemId) =>
        client.PostAsync(Path(bookingId, itemId), null);
}
