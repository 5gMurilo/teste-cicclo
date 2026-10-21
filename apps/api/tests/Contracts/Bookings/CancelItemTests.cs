using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Contracts.Bookings;

/// <summary>
/// POST /api/v1/bookings/{bookingId}/items/{itemId}/cancel sobre PostgreSQL 18.1 real com FakeTimeProvider
/// (REFUND-01/03/04/06, HTTP-04/06..08, OPS-04/11, QUEUE-14): cancela antes de iniciar com refund do preço histórico,
/// libera e promove, repete sem crédito novo e recusa itens iniciados sem mutação. HTTP e banco conferidos juntos.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E05")]
[Trait("Task", "T1")]
public sealed class CancelItemTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/bookings/{bookingId}/items/{itemId}/cancel";
    private static readonly Dictionary<string, string> Unlimited = new()
    {
        ["RateLimiting:ItemCancel:PermitLimit"] = "1000",
        ["RateLimiting:ItemCommands:PermitLimit"] = "1000",
    };

    /// <summary>Lançamentos em ordem de criação: kind|item|valor|saldo depois.</summary>
    internal const string LedgerSql =
        "SELECT kind || '|' || coalesce(request_item_id::text, 'null') || '|' || amount || '|' || balance_after FROM wallet_transactions ORDER BY created_at, kind, balance_after DESC";

    [Fact]
    public async Task X01_PendingItemIsCancelledWithItsHistoricalPriceRefundedOnce()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var wash = booking.Items.Single(item => item.ServiceCode == "wash").ItemId;
        var machines = await MachinesAsync(database);
        clock.SetUtcNow(T0.AddSeconds(5));
        await using var factory = BookingApi.Create(database, clock, Unlimited);

        using var response = await CancelAsync(factory.CreateClient(), booking.BookingId, wash);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BookingApi.JsonAsync(response);
        AssertCancelledItem(body, wash, "wash", 1890);
        Assert.Equal((1890L, 2910L), (body.GetProperty("refundedAmountCents").GetInt64(), body.GetProperty("remainingBalanceCents").GetInt64()));
        Assert.Contains($"item|{wash}|cancelled|null|null|null|null|null|null", await database.RowsAsync(StateSql));
        Assert.Equal("12:00:05.000000", await database.ScalarAsync<string>($"SELECT to_char(cancelled_at AT TIME ZONE 'UTC', 'HH24:MI:SS.US') FROM service_request_items WHERE id = '{wash}'"));
        Assert.Equal(["debit|null|39.80|10.20", $"refund|{wash}|18.90|29.10"], await database.RowsAsync(LedgerSql));
        Assert.Equal(machines, await MachinesAsync(database));
    }

    [Fact]
    public async Task X02_QueuedItemIsCancelledWithoutTouchingTheOfferAheadOfIt()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var first = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(1));
        var second = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var a = first.Items[0].ItemId;
        var b = second.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(10));
        await using var factory = BookingApi.Create(database, clock, Unlimited);

        using var response = await CancelAsync(factory.CreateClient(), second.BookingId, b);

        var body = await BookingApi.JsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCancelledItem(body, b, "wash", 1890);
        Assert.Equal((1890L, 3110L), (body.GetProperty("refundedAmountCents").GetInt64(), body.GetProperty("remainingBalanceCents").GetInt64()));
        var rows = await database.RowsAsync(StateSql);
        Assert.Contains($"item|{a}|to_be_confirmed|1|12:00:00.000000|{WasherId}|12:00:00.000000|12:01:00.000000|null", rows);
        Assert.Contains($"item|{b}|cancelled|2|12:00:01.000000|null|null|null|null", rows);
        Assert.Contains($"machine|washer-01|reserved|{a}|12:00:00.000000", rows);
        Assert.Contains("wallet|31.10|3", rows);
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
    }

    [Fact]
    public async Task X03_CancellingTheOfferedItemReleasesTheMachineAndOffersItToTheNextInLine()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var first = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.SetUtcNow(T0.AddSeconds(1));
        var second = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var a = first.Items[0].ItemId;
        var b = second.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(20));
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();

        using var response = await CancelAsync(client, first.BookingId, a);

        var body = await BookingApi.JsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCancelledItem(body, a, "wash", 1890);
        Assert.Equal((1890L, 3110L), (body.GetProperty("refundedAmountCents").GetInt64(), body.GetProperty("remainingBalanceCents").GetInt64()));
        var rows = await database.RowsAsync(StateSql);
        Assert.Contains($"item|{a}|cancelled|1|12:00:00.000000|null|null|null|null", rows);
        Assert.Contains($"item|{b}|to_be_confirmed|2|12:00:01.000000|{WasherId}|12:00:20.000000|12:01:20.000000|null", rows);
        Assert.Contains($"machine|washer-01|reserved|{b}|12:00:20.000000", rows);
        Assert.Equal(["debit|null|18.90|31.10", "debit|null|18.90|12.20", $"refund|{a}|18.90|31.10"], await database.RowsAsync(LedgerSql));
        var next = await BookingApi.JsonAsync(await client.GetAsync($"/api/v1/bookings/{second.BookingId}"));
        Assert.Equal(("to_be_confirmed", "2026-10-21T12:01:20Z"), (next.GetProperty("items")[0].GetProperty("status").GetString(), next.GetProperty("items")[0].GetProperty("expiresAt").GetString()));
    }

    // REFUND-06, Independent Test da spec: wash de 1890 cancelado depois de o preço ir a 25.00 devolve 1890 e a
    // carteira volta a 5000.
    [Fact]
    public async Task X04_CatalogPriceChangedAfterThePurchaseStillRefundsTheHistoricalPriceAndRestoresTheWallet()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        await database.ExecuteAsync("UPDATE services SET current_price = 25.00 WHERE code = 'wash'");
        clock.SetUtcNow(T0.AddSeconds(5));
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();

        using var response = await CancelAsync(client, booking.BookingId, wash);

        var body = await BookingApi.JsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((1890L, 5000L), (body.GetProperty("refundedAmountCents").GetInt64(), body.GetProperty("remainingBalanceCents").GetInt64()));
        Assert.Equal(1890L, body.GetProperty("item").GetProperty("unitPriceCents").GetInt64());
        Assert.Equal(["debit|null|18.90|31.10", $"refund|{wash}|18.90|50.00"], await database.RowsAsync(LedgerSql));
        Assert.Equal("50.00", await database.ScalarAsync<string>("SELECT balance::text FROM wallets"));
        var ledger = await BookingApi.JsonAsync(await client.GetAsync("/api/v1/wallet/transactions"));
        var refund = ledger.GetProperty("items")[0];
        Assert.Equal(
            ("refund", wash, 1890L, 5000L, booking.BookingId),
            (refund.GetProperty("kind").GetString(), refund.GetProperty("itemId").GetGuid(), refund.GetProperty("amountCents").GetInt64(),
                refund.GetProperty("balanceAfterCents").GetInt64(), refund.GetProperty("bookingId").GetGuid()));
    }

    [Fact]
    public async Task X05_RepeatingTheCancellationReturnsTheHistoricalRefundAndTheCurrentBalanceWithoutCreditingAgain()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();
        using (var first = await CancelAsync(client, booking.BookingId, wash))
        {
            Assert.Equal(5000L, (await BookingApi.JsonAsync(first)).GetProperty("remainingBalanceCents").GetInt64());
        }

        await BookingApi.PurchaseAsync(database, clock, ["dry"]);
        await database.ExecuteAsync("UPDATE services SET current_price = 25.00 WHERE code = 'wash'");
        var before = await database.RowsAsync(StateSql);
        var ledger = await database.RowsAsync(LedgerSql);

        using var repeat = await CancelAsync(client, booking.BookingId, wash);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var body = await BookingApi.JsonAsync(repeat);
        AssertCancelledItem(body, wash, "wash", 1890);
        Assert.Equal((1890L, 2910L), (body.GetProperty("refundedAmountCents").GetInt64(), body.GetProperty("remainingBalanceCents").GetInt64()));
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal(ledger, await database.RowsAsync(LedgerSql));
        Assert.Single(ledger, row => row.StartsWith("refund|", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task X06_StartedOrFinishedItemsAreRefusedWith409WithoutRefund(string status)
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();
        using (var confirmed = await client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{wash}/confirm", null))
        {
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        }

        if (status != "in_progress")
        {
            // Desfecho preparado como fixture consistente (o comando de simulação que o produz não é desta etapa).
            await database.ExecuteAsync(
                $"""
                UPDATE service_request_items SET status = '{status}' WHERE id = '{wash}';
                UPDATE machines SET state = 'available', active_item_id = NULL WHERE id = '{WasherId}';
                """);
        }

        var before = await database.RowsAsync(StateSql);

        using var response = await CancelAsync(client, booking.BookingId, wash);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(booking.BookingId, wash));
        Assert.Equal("O item já iniciou ou terminou; o cancelamento só vale antes do início e nada foi alterado.", problem.GetProperty("detail").GetString());
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains(before, row => row.StartsWith($"item|{wash}|{status}|", StringComparison.Ordinal));
        Assert.Equal(["debit|null|18.90|31.10"], await database.RowsAsync(LedgerSql));
    }

    [Fact]
    public async Task X07_InvalidIdsAndItemsOfAnotherBookingAreRejectedWithoutMutation()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var other = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items.Single(item => item.ServiceCode == "wash").ItemId;
        var before = await database.RowsAsync(StateSql);
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        using var foreign = await CancelAsync(client, booking.BookingId, other.Items[0].ItemId);
        using var missingItem = await CancelAsync(client, booking.BookingId, unknown);
        using var missingBooking = await CancelAsync(client, unknown, wash);
        using var invalid = await client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/x/cancel", null);

        await ProblemAssert.HasProblemAsync(foreign, HttpStatusCode.NotFound, ApiProblems.ItemNotFound, Path(booking.BookingId, other.Items[0].ItemId));
        await ProblemAssert.HasProblemAsync(missingItem, HttpStatusCode.NotFound, ApiProblems.ItemNotFound, Path(booking.BookingId, unknown));
        await ProblemAssert.HasProblemAsync(missingBooking, HttpStatusCode.NotFound, ApiProblems.BookingNotFound, Path(unknown, wash));
        await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, $"/api/v1/bookings/{booking.BookingId}/items/x/cancel");
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal(["debit|null|39.80|60.20", "debit|null|18.90|41.30"], await database.RowsAsync(LedgerSql));
    }

    [Fact]
    public async Task X08_ContractTenPerMinuteLimitUnavailableDatabaseAndRefundCounterMatchTheSpec()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var started = await BookingApi.PurchaseAsync(database, clock, ["dry"]);
        var wash = booking.Items[0].ItemId;
        var dry = started.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(30));
        await using var factory = BookingApi.Create(database, clock, new Dictionary<string, string> { ["RateLimiting:ItemCancel:PermitLimit"] = "3" });
        using var client = factory.CreateClient();
        using var counter = new RefundCounter(factory.Services.GetRequiredService<IMeterFactory>());
        var openApi = await OpenApiContract.FetchAsync(client);
        using (var confirmed = await client.PostAsync($"/api/v1/bookings/{started.BookingId}/items/{dry}/confirm", null))
        {
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        }

        using var ok = await CancelAsync(client, booking.BookingId, wash);
        using var replay = await CancelAsync(client, booking.BookingId, wash);
        using var conflict = await CancelAsync(client, started.BookingId, dry);
        using var limited = await CancelAsync(client, booking.BookingId, wash);
        await using var down = BookingApi.Create(new TestDatabase(ApiFactory.UnreachableDatabase), clock, Unlimited);
        using var unavailable = await CancelAsync(down.CreateClient(), booking.BookingId, wash);
        await using var defaults = BookingApi.Create(database, clock);

        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "POST", "CancelBookingItem")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Post];
        Assert.Equal(["Bookings"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "400", "404", "409", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(operation.RequestBody);
        Assert.Equal(["bookingId", "itemId"], operation.Parameters!.Select(parameter => parameter.Name));
        Assert.All(operation.Parameters!, parameter => Assert.Equal(("uuid", ParameterLocation.Path), (parameter.Schema!.Format, parameter.In!.Value)));
        foreach (var success in new[] { ok, replay })
        {
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            var body = await BookingApi.JsonAsync(success);
            Assert.Equal((1890L, 7910L), (body.GetProperty("refundedAmountCents").GetInt64(), body.GetProperty("remainingBalanceCents").GetInt64()));
            JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), body.GetRawText());
        }

        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), JsonSchemaContract.Example(openApi, Route, "200", method: "post"));
        var refused = await ProblemAssert.HasProblemAsync(conflict, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, Path(started.BookingId, dry));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "409", "application/problem+json", "post"), refused.GetRawText());
        var problem = await ProblemAssert.HasProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, Path(booking.BookingId, wash));
        Assert.InRange(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture), 1, 60);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "429", "application/problem+json", "post"), problem.GetRawText());
        await ProblemAssert.HasProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path(booking.BookingId, wash));
        Assert.Equal(10, defaults.Services.GetRequiredService<IOptionsMonitor<RateLimitPolicyOptions>>().Get(ApiRateLimits.ItemCancel).PermitLimit);
        // Um crédito confirmado conta uma vez; a repetição e a recusa não contam.
        Assert.Equal(["reason=cancelled"], counter.Measurements);
        Assert.Single(await database.RowsAsync(LedgerSql), row => row.StartsWith("refund|", StringComparison.Ordinal));
    }

    // D1 (dívida E04): cancelled existe agora, então activate e confirm sobre ele são transições inválidas (QUEUE-14).
    [Fact]
    public async Task X09_ActivatingOrConfirmingACancelledItemIs409WithoutMutation()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var wash = booking.Items.Single(item => item.ServiceCode == "wash").ItemId;
        var dry = booking.Items.Single(item => item.ServiceCode == "dry").ItemId;
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();
        using (var cancelled = await CancelAsync(client, booking.BookingId, wash))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }

        // A secadora em fila prova que a recusa não reconcilia nem grava nada.
        await database.ExecuteAsync($"UPDATE machines SET state = 'running', is_demo_occupied = true WHERE id = '{DryerId}'");
        await ActivateAsync(Sessions(database, "cicclo-e05-setup"), Coordinator(clock), dry);
        var before = await database.RowsAsync(StateSql);
        var openApi = await OpenApiContract.FetchAsync(client);

        using var activate = await client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{wash}/activate", null);
        using var confirm = await client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/{wash}/confirm", null);

        var problem = await ProblemAssert.HasProblemAsync(
            activate, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, $"/api/v1/bookings/{booking.BookingId}/items/{wash}/activate");
        Assert.Equal("O item está cancelado ou já terminou; não pode ser ativado e nada foi alterado.", problem.GetProperty("detail").GetString());
        JsonSchemaContract.AssertValid(
            openApi, JsonSchemaContract.ResponsePointer("/api/v1/bookings/{bookingId}/items/{itemId}/activate", "409", "application/problem+json", "post"), problem.GetRawText());
        await ProblemAssert.HasProblemAsync(confirm, HttpStatusCode.Conflict, ApiProblems.InvalidStateTransition, $"/api/v1/bookings/{booking.BookingId}/items/{wash}/confirm");
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Contains(before, row => row.StartsWith($"item|{dry}|queued|1|", StringComparison.Ordinal));
    }

    internal static string Path(Guid bookingId, Guid itemId) => $"/api/v1/bookings/{bookingId}/items/{itemId}/cancel";

    internal static Task<HttpResponseMessage> CancelAsync(HttpClient client, Guid bookingId, Guid itemId) =>
        client.PostAsync(Path(bookingId, itemId), null);

    private static void AssertCancelledItem(JsonElement body, Guid itemId, string serviceCode, long unitPriceCents)
    {
        var item = body.GetProperty("item");
        Assert.Equal(
            (itemId, serviceCode, unitPriceCents, "cancelled"),
            (item.GetProperty("itemId").GetGuid(), item.GetProperty("serviceCode").GetString(), item.GetProperty("unitPriceCents").GetInt64(), item.GetProperty("status").GetString()));
        Assert.All(
            new[] { "machineId", "queuePosition", "expiresAt" },
            name => Assert.Equal(JsonValueKind.Null, item.GetProperty(name).ValueKind));
    }

    private static Task<IReadOnlyList<string>> MachinesAsync(TestDatabase database) =>
        database.RowsAsync("SELECT name || '|' || state || '|' || coalesce(active_item_id::text, 'null') || '|' || updated_at FROM machines ORDER BY id");

    /// <summary>Medições de cicclo_wallet_refunds_total do host, com as dimensões recebidas.</summary>
    private sealed class RefundCounter : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentQueue<string> measurements = new();

        public RefundCounter(IMeterFactory meters)
        {
            listener.InstrumentPublished = (instrument, subscription) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meters) && instrument.Name == "cicclo_wallet_refunds_total")
                {
                    subscription.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
                measurements.Enqueue(string.Join(',', tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}")) + (value == 1 ? string.Empty : $"x{value}")));
            listener.Start();
        }

        public IReadOnlyList<string> Measurements => [.. measurements];

        public void Dispose() => listener.Dispose();
    }
}
