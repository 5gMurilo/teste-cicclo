using System.Globalization;
using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;
using ConfirmOfferUseCase = Cicclo.Api.Laundry.ConfirmOffer.ConfirmOffer;

namespace Cicclo.Api.Tests.Contracts.Bookings;

/// <summary>
/// POST /api/v1/bookings/{bookingId}/items/{itemId}/activate sobre PostgreSQL 18.1 real (QUEUE-02..07/13/14, HTTP-04/06..08,
/// OPS-04): a mesma política de fila da compra, sem débito, vínculo item↔booking, repetição sem ticket novo e reconciliação
/// do tipo antes de responder (G1).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E04")]
[Trait("Task", "T5")]
public sealed class ActivateItemTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/bookings/{bookingId}/items/{itemId}/activate";
    private const string SetupApp = "cicclo-e04-setup";
    private const string ApiApp = "cicclo-e04-api";
    private static readonly string[] ItemFields = ["itemId", "serviceCode", "unitPriceCents", "status", "machineId", "queuePosition", "expiresAt"];
    private static readonly Dictionary<string, string> Unlimited = new() { ["RateLimiting:ItemCommands:PermitLimit"] = "1000" };

    [Fact]
    public async Task V01_FreeMachineWithoutQueueOffersTheItemForSixtySecondsWithoutAnyDebit()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var wash = Item(booking, "wash");
        clock.Advance(TimeSpan.FromSeconds(7));
        await using var factory = BookingApi.Create(database, clock, Unlimited);

        using var response = await ActivateAsync(factory.CreateClient(), booking.BookingId, wash);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var item = await BookingApi.JsonAsync(response);
        Assert.Equal(ItemFields, item.EnumerateObject().Select(property => property.Name));
        Assert.Equal((wash, "wash", 1890L, "to_be_confirmed"), (item.GetProperty("itemId").GetGuid(), item.GetProperty("serviceCode").GetString(), item.GetProperty("unitPriceCents").GetInt64(), item.GetProperty("status").GetString()));
        Assert.Equal(Guid.Parse(WasherId), item.GetProperty("machineId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("queuePosition").ValueKind);
        Assert.Equal("2026-10-21T12:01:07Z", item.GetProperty("expiresAt").GetString());
        Assert.Equal(
            [$"item|{wash}|to_be_confirmed|1|12:00:07.000000|{WasherId}|12:00:07.000000|12:01:07.000000|null", $"machine|washer-01|reserved|{wash}|12:00:07.000000", "wallet|10.20|1"],
            (await database.RowsAsync(StateSql)).Where(row => row.Contains(wash.ToString(), StringComparison.Ordinal) || row.StartsWith("wallet|", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task V02_BusyMachineQueuesTheItemWithANewTicketAndDerivedPosition()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running' WHERE id = '{WasherId}'");
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "wash"]);
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();

        var first = await BookingApi.JsonAsync(await ActivateAsync(client, booking.BookingId, booking.Items[0].ItemId));
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = await BookingApi.JsonAsync(await ActivateAsync(client, booking.BookingId, booking.Items[1].ItemId));

        Assert.Equal(("queued", 1), (first.GetProperty("status").GetString(), first.GetProperty("queuePosition").GetInt32()));
        Assert.Equal(("queued", 2), (second.GetProperty("status").GetString(), second.GetProperty("queuePosition").GetInt32()));
        Assert.All(new[] { first, second }, item =>
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("machineId").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("expiresAt").ValueKind);
        });
        Assert.Equal(
            [$"{booking.Items[0].ItemId}|1|12:00:00", $"{booking.Items[1].ItemId}|2|12:00:02"],
            await database.RowsAsync("SELECT id || '|' || queue_ticket || '|' || to_char(queued_at AT TIME ZONE 'UTC', 'HH24:MI:SS') FROM service_request_items ORDER BY queue_ticket"));
        Assert.Equal("12.20|1", (await database.RowsAsync("SELECT (SELECT balance::text FROM wallets) || '|' || (SELECT count(*) FROM wallet_transactions)")).Single());
    }

    [Fact]
    public async Task V03_ActivationDoesNotOvertakeAnOlderQueuedItemWhenTheMachineIsFree()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running' WHERE id = '{WasherId}'; UPDATE wallets SET balance = 100.00;");
        var clock = Clock();
        var older = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        // A lavadora fica livre sem promoção; o item novo disputa a máquina com o item mais antigo da fila.
        await database.ExecuteAsync($"UPDATE machines SET state = 'available' WHERE id = '{WasherId}'");
        await using var factory = BookingApi.Create(database, clock, Unlimited);

        var item = await BookingApi.JsonAsync(await ActivateAsync(factory.CreateClient(), booking.BookingId, Item(booking, "wash")));

        Assert.Equal(("queued", 1), (item.GetProperty("status").GetString(), item.GetProperty("queuePosition").GetInt32()));
        Assert.Equal($"to_be_confirmed|{WasherId}", await database.ScalarAsync<string>($"SELECT status || '|' || machine_id FROM service_request_items WHERE id = '{older.Items[0].ItemId}'"));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("to_be_confirmed")]
    [InlineData("in_progress")]
    public async Task V04_RepeatedActivationReturns200WithTheCurrentStateWithoutTicketStartOrCharge(string state)
    {
        var database = await ReadyAsync(postgres, state == "queued" ? $"UPDATE machines SET state = 'running' WHERE id = '{WasherId}'" : null);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        if (state == "in_progress")
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await new ConfirmOfferUseCase(Sessions(database, SetupApp), Coordinator(clock), Metrics(), clock, NullLogger<ConfirmOfferUseCase>.Instance)
                .ExecuteAsync(booking.BookingId, wash, CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromSeconds(state == "in_progress" ? 120 : 10));
        var before = await database.RowsAsync(StateSql);
        Assert.Contains(before, row => row.StartsWith($"item|{wash}|{state}|1|", StringComparison.Ordinal));
        await using var factory = BookingApi.Create(database, clock, Unlimited);

        var item = await BookingApi.JsonAsync(await ActivateAsync(factory.CreateClient(), booking.BookingId, wash));

        Assert.Equal(state, item.GetProperty("status").GetString());
        Assert.Equal(state == "queued" ? JsonValueKind.Number : JsonValueKind.Null, item.GetProperty("queuePosition").ValueKind);
        Assert.Equal(state == "to_be_confirmed" ? "2026-10-21T12:01:00Z" : null, item.GetProperty("expiresAt").GetString());
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq"));
    }

    // G1 (decisão de contrato): repetir activate sobre uma oferta já vencida reconcilia a fila do tipo antes de responder,
    // como confirm e o worker. O item volta ao fim da fila por expiração (QUEUE-10) e, sozinho, recebe nova oferta.
    [Fact]
    public async Task V05_RepeatedActivationOverAnExpiredOfferReconcilesTheQueueBeforeAnswering()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var wash = booking.Items[0].ItemId;
        clock.SetUtcNow(T0.AddSeconds(61));
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();
        var measurements = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, subscription) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>())
                    && instrument.Meter.Name == Cicclo.Api.Laundry.LaundryMetrics.MeterName)
                {
                    subscription.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{instrument.Name}|{value}|{string.Join(',', tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}"))}")));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{instrument.Name}|{value}|{string.Join(',', tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}"))}")));
        listener.Start();

        var item = await BookingApi.JsonAsync(await ActivateAsync(client, booking.BookingId, wash));

        Assert.Equal(("to_be_confirmed", "2026-10-21T12:02:01Z"), (item.GetProperty("status").GetString(), item.GetProperty("expiresAt").GetString()));
        Assert.Equal(
            $"item|{wash}|to_be_confirmed|2|12:01:01.000000|{WasherId}|12:01:01.000000|12:02:01.000000|null",
            (await database.RowsAsync(StateSql)).Single(row => row.StartsWith("item|", StringComparison.Ordinal)));
        Assert.Equal("31.10|1", (await database.RowsAsync("SELECT (SELECT balance::text FROM wallets) || '|' || (SELECT count(*) FROM wallet_transactions)")).Single());
        Assert.Equal(["cicclo_offers_expired_total|1|machine_type=washer", "cicclo_queue_wait_seconds|0|machine_type=washer"], measurements.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task V06_ItemOfAnotherBookingUnknownIdsAndInvalidUuidsAreRejectedWithoutMutation()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var other = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var before = await database.RowsAsync(StateSql);
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        using var foreignItem = await ActivateAsync(client, booking.BookingId, Item(other, "wash"));
        using var missingItem = await ActivateAsync(client, booking.BookingId, unknown);
        using var missingBooking = await ActivateAsync(client, unknown, Item(booking, "wash"));
        using var invalidItem = await client.PostAsync($"/api/v1/bookings/{booking.BookingId}/items/nao-uuid/activate", null);
        using var invalidBooking = await client.PostAsync($"/api/v1/bookings/123/items/{Item(booking, "wash")}/activate", null);

        await ProblemAssert.HasProblemAsync(foreignItem, HttpStatusCode.NotFound, ApiProblems.ItemNotFound, $"/api/v1/bookings/{booking.BookingId}/items/{Item(other, "wash")}/activate");
        await ProblemAssert.HasProblemAsync(missingItem, HttpStatusCode.NotFound, ApiProblems.ItemNotFound, $"/api/v1/bookings/{booking.BookingId}/items/{unknown}/activate");
        await ProblemAssert.HasProblemAsync(missingBooking, HttpStatusCode.NotFound, ApiProblems.BookingNotFound, $"/api/v1/bookings/{unknown}/items/{Item(booking, "wash")}/activate");
        await ProblemAssert.HasProblemAsync(invalidItem, HttpStatusCode.BadRequest, ApiProblems.ValidationError, $"/api/v1/bookings/{booking.BookingId}/items/nao-uuid/activate");
        await ProblemAssert.HasProblemAsync(invalidBooking, HttpStatusCode.BadRequest, ApiProblems.ValidationError, $"/api/v1/bookings/123/items/{Item(booking, "wash")}/activate");
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task V07_ForeignWalletBookingAndUnavailableDatabaseAreRejectedWithoutMutation()
    {
        var database = await ReadyAsync(postgres);
        const string otherWallet = "00000000-0000-4000-8000-000000000002";
        var foreignBooking = Guid.NewGuid();
        var foreignItem = Guid.NewGuid();
        await database.ExecuteAsync(
            $"""
            INSERT INTO wallets (id, currency, balance, created_at, updated_at) VALUES ('{otherWallet}', 'BRL', 99.99, now(), now());
            INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
            VALUES ('{foreignBooking}', '{otherWallet}', gen_random_uuid(), repeat('a', 64), 18.90, now());
            INSERT INTO service_request_items (id, request_id, service_code, unit_price, status) VALUES ('{foreignItem}', '{foreignBooking}', 'wash', 18.90, 'pending_activation');
            """);
        var before = await database.RowsAsync(StateSql);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        await using var down = BookingApi.Create(new TestDatabase(ApiFactory.UnreachableDatabase), Clock(), Unlimited);

        using var foreign = await ActivateAsync(factory.CreateClient(), foreignBooking, foreignItem);
        using var unavailable = await ActivateAsync(down.CreateClient(), foreignBooking, foreignItem);

        await ProblemAssert.HasProblemAsync(foreign, HttpStatusCode.NotFound, ApiProblems.BookingNotFound, $"/api/v1/bookings/{foreignBooking}/items/{foreignItem}/activate");
        await ProblemAssert.HasProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, $"/api/v1/bookings/{foreignBooking}/items/{foreignItem}/activate");
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task V08_OperationSchemaExampleAndFifteenPerMinuteItemCommandLimitMatchTheContract()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        await using var factory = BookingApi.Create(database, clock, new Dictionary<string, string> { ["RateLimiting:ItemCommands:PermitLimit"] = "2" });
        using var client = factory.CreateClient();
        var openApi = await OpenApiContract.FetchAsync(client);
        using var ok = await ActivateAsync(client, booking.BookingId, Item(booking, "wash"));
        using var missing = await ActivateAsync(client, booking.BookingId, Guid.NewGuid());
        using var limited = await ActivateAsync(client, booking.BookingId, Item(booking, "dry"));
        using var read = await client.GetAsync($"/api/v1/bookings/{booking.BookingId}");

        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "POST", "ActivateBookingItem")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Post];
        Assert.Equal(["Bookings"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "400", "404", "409", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(operation.RequestBody);
        Assert.Equal(["bookingId", "itemId"], operation.Parameters!.Select(parameter => parameter.Name));
        Assert.All(operation.Parameters!, parameter =>
        {
            Assert.Equal(ParameterLocation.Path, parameter.In);
            Assert.Equal("uuid", parameter.Schema!.Format);
            Assert.False(string.IsNullOrWhiteSpace(parameter.Description));
        });
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), await ok.Content.ReadAsStringAsync());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200", method: "post"), JsonSchemaContract.Example(openApi, Route, "200", method: "post"));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "404", "application/problem+json", "post"), await missing.Content.ReadAsStringAsync());
        var problem = await ProblemAssert.HasProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, $"/api/v1/bookings/{booking.BookingId}/items/{Item(booking, "dry")}/activate");
        Assert.InRange(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture), 1, 60);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "429", "application/problem+json", "post"), problem.GetRawText());
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("pending_activation", await database.ScalarAsync<string>($"SELECT status FROM service_request_items WHERE id = '{Item(booking, "dry")}'"));
        await using var defaults = BookingApi.Create(database, clock);
        Assert.Equal(15, defaults.Services.GetRequiredService<IOptionsMonitor<RateLimitPolicyOptions>>().Get(ApiRateLimits.ItemCommands).PermitLimit);
    }

    // Ordem de locks comum: a ativação espera a wallet (mutex das escritas) antes de tocar máquinas ou itens, e o instante
    // da decisão é lido depois dos locks.
    [Fact]
    public async Task V09_ActivationWaitsForTheWalletBeforeLockingMachinesAndDecidesWithTheClockAfterTheLocks()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var wash = Item(booking, "wash");
        await using var factory = BookingApi.Create(new TestDatabase(Named(database, ApiApp)), clock, Unlimited);
        using var client = factory.CreateClient();
        var (holder, transaction) = await HoldAsync(database, "cicclo-e04-holder", $"SELECT 1 FROM wallets WHERE id = '{DemoData.WalletId}' FOR UPDATE");

        var activation = ActivateAsync(client, booking.BookingId, wash);
        await WaitUntilWaitingOnLockAsync(database, ApiApp);

        Assert.True(await CanLockNowAsync(database, $"SELECT 1 FROM machines WHERE id = '{WasherId}' FOR UPDATE"));
        Assert.True(await CanLockNowAsync(database, $"SELECT 1 FROM service_request_items WHERE id = '{wash}' FOR UPDATE"));
        clock.SetUtcNow(T0.AddSeconds(30));
        await transaction.RollbackAsync();
        await holder.DisposeAsync();
        using var response = await activation.WaitAsync(SignalTimeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2026-10-21T12:01:30Z", (await BookingApi.JsonAsync(response)).GetProperty("expiresAt").GetString());
    }

    private static Guid Item(Cicclo.Api.Bookings.CreateBooking.BookingSnapshot booking, string code) =>
        booking.Items.First(item => item.ServiceCode == code).ItemId;

    private static Task<HttpResponseMessage> ActivateAsync(HttpClient client, Guid bookingId, Guid itemId) =>
        client.PostAsync($"/api/v1/bookings/{bookingId}/items/{itemId}/activate", null);
}
