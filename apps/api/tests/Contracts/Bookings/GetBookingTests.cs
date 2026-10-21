using System.Globalization;
using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Integration.Laundry;
using Cicclo.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Npgsql;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;
using ConfirmOfferUseCase = Cicclo.Api.Laundry.ConfirmOffer.ConfirmOffer;

namespace Cicclo.Api.Tests.Contracts.Bookings;

/// <summary>
/// GET /api/v1/bookings/{bookingId} sobre PostgreSQL 18.1 real (HTTP-03/04/06/07/08, SEC-01): valores esperados vêm
/// da spec e do handoff (preço histórico, saldo atual, machineId/queuePosition/expiresAt conforme o estado e null
/// explícito fora dele). Compras e ofertas são criadas pelo núcleo interno, nunca por POST.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E04")]
[Trait("Task", "T1")]
public sealed class GetBookingTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/bookings/{bookingId}";
    private const string SetupApp = "cicclo-e04-setup";
    private const string WriterApp = "cicclo-e04-writer";

    private static readonly string[] BookingFields = ["bookingId", "currency", "chargedAmountCents", "remainingBalanceCents", "createdAt", "items"];
    private static readonly string[] ItemFields = ["itemId", "serviceCode", "unitPriceCents", "status", "machineId", "queuePosition", "expiresAt"];

    [Fact]
    public async Task G01_PersistedPurchaseReturnsHistoricalChargeCurrentBalanceAndExplicitNulls()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        await using var factory = BookingApi.Create(database, clock);

        using var response = await factory.CreateClient().GetAsync($"/api/v1/bookings/{booking.BookingId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal(BookingFields, body.EnumerateObject().Select(property => property.Name));
        Assert.Equal(booking.BookingId, body.GetProperty("bookingId").GetGuid());
        Assert.Equal("BRL", body.GetProperty("currency").GetString());
        Assert.Equal(3980L, body.GetProperty("chargedAmountCents").GetInt64());
        Assert.Equal(1020L, body.GetProperty("remainingBalanceCents").GetInt64());
        Assert.Equal("2026-10-21T12:00:00Z", body.GetProperty("createdAt").GetString());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(booking.Items.Select(item => item.ItemId), items.Select(item => item.GetProperty("itemId").GetGuid()));
        Assert.Equal(["dry|2090|pending_activation", "wash|1890|pending_activation"], items.Select(Describe));
        Assert.All(items, item =>
        {
            Assert.Equal(ItemFields, item.EnumerateObject().Select(property => property.Name));
            Assert.Equal(JsonValueKind.Null, item.GetProperty("machineId").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("queuePosition").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("expiresAt").ValueKind);
        });
    }

    [Fact]
    public async Task G02_LaterPriceChangeKeepsTheHistoricalChargeWhileTheBalanceIsCurrent()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        await database.ExecuteAsync("UPDATE services SET current_price = 25.00 WHERE code = 'wash'");
        await BookingApi.PurchaseAsync(database, clock, ["dry"]);
        await using var factory = BookingApi.Create(database, clock);

        var body = await GetAsync(factory.CreateClient(), booking.BookingId);

        Assert.Equal(1890L, body.GetProperty("chargedAmountCents").GetInt64());
        Assert.Equal(1890L, Assert.Single(body.GetProperty("items").EnumerateArray()).GetProperty("unitPriceCents").GetInt64());
        // 5000 - 1890 - 2090: o saldo é o atual da wallet, não o do instante da compra.
        Assert.Equal(1020L, body.GetProperty("remainingBalanceCents").GetInt64());
    }

    [Fact]
    public async Task G03_QueuedItemsShowThePositionDerivedPerMachineTypeWithoutMachineOrDeadline()
    {
        var database = await ReadyAsync(postgres, $"UPDATE machines SET state = 'running' WHERE id = '{DryerId}'; UPDATE wallets SET balance = 100.00;");
        var clock = Clock();
        var sessions = Sessions(database, SetupApp);
        var earlier = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "wash", "dry"]);
        foreach (var item in new[] { earlier.Items[0].ItemId }.Concat(booking.Items.Select(item => item.ItemId)))
        {
            await ActivateAsync(sessions, Coordinator(clock), item);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        await using var factory = BookingApi.Create(database, clock);

        var body = await GetAsync(factory.CreateClient(), booking.BookingId);

        // A lavadora livre foi ofertada ao item de outra compra (ticket 1), que não conta na fila; as lavagens deste
        // booking (tickets 3 e 4) ficam em 1 e 2. A secagem (ticket 2) tem a fila própria das secadoras.
        Assert.Equal(["dry|2090|queued|1", "wash|1890|queued|1", "wash|1890|queued|2"], body.GetProperty("items").EnumerateArray().Select(DescribeWithPosition));
        Assert.All(body.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("machineId").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("expiresAt").ValueKind);
        });
        Assert.Equal(5870L, body.GetProperty("chargedAmountCents").GetInt64());
        Assert.Equal(10000L - 1890L - 5870L, body.GetProperty("remainingBalanceCents").GetInt64());
    }

    [Fact]
    public async Task G04_OfferedItemShowsMachineAndDeadlineAndStartedItemKeepsOnlyTheMachine()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var dry = booking.Items.Single(item => item.ServiceCode == "dry").ItemId;
        var wash = booking.Items.Single(item => item.ServiceCode == "wash").ItemId;
        var sessions = Sessions(database, SetupApp);
        await ActivateAsync(sessions, Coordinator(clock), wash);
        await ActivateAsync(sessions, Coordinator(clock), dry);
        clock.Advance(TimeSpan.FromSeconds(10));
        var started = await new ConfirmOfferUseCase(sessions, Coordinator(clock), Metrics(), clock, NullLogger<ConfirmOfferUseCase>.Instance)
            .ExecuteAsync(booking.BookingId, dry, CancellationToken.None);
        Assert.Equal(Cicclo.Api.Laundry.ConfirmOffer.ConfirmOfferOutcome.Started, started.Outcome);
        await using var factory = BookingApi.Create(database, clock);

        var items = (await GetAsync(factory.CreateClient(), booking.BookingId)).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal("in_progress", items[0].GetProperty("status").GetString());
        Assert.Equal(Guid.Parse(DryerId), items[0].GetProperty("machineId").GetGuid());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("expiresAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("queuePosition").ValueKind);
        Assert.Equal("to_be_confirmed", items[1].GetProperty("status").GetString());
        Assert.Equal(Guid.Parse(WasherId), items[1].GetProperty("machineId").GetGuid());
        Assert.Equal("2026-10-21T12:01:00Z", items[1].GetProperty("expiresAt").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("queuePosition").ValueKind);
    }

    [Fact]
    public async Task G05_InvalidUuidIs400AndUnknownOrForeignBookingIs404WithoutLeaks()
    {
        var database = await ReadyAsync(postgres);
        var foreign = Guid.NewGuid();
        const string otherWallet = "00000000-0000-4000-8000-000000000002";
        await database.ExecuteAsync(
            $"""
            INSERT INTO wallets (id, currency, balance, created_at, updated_at) VALUES ('{otherWallet}', 'BRL', 99.99, now(), now());
            INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
            VALUES ('{foreign}', '{otherWallet}', gen_random_uuid(), repeat('a', 64), 18.90, now());
            INSERT INTO service_request_items (id, request_id, service_code, unit_price, status) VALUES (gen_random_uuid(), '{foreign}', 'wash', 18.90, 'pending_activation');
            """);
        await using var factory = BookingApi.Create(database, Clock());
        using var client = factory.CreateClient();

        using var invalid = await client.GetAsync("/api/v1/bookings/nao-e-uuid");
        var invalidBody = await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, "/api/v1/bookings/nao-e-uuid");
        foreach (var leak in new[] { "Guid", "Failed to bind", "System." })
        {
            Assert.DoesNotContain(leak, invalidBody.GetRawText(), StringComparison.Ordinal);
        }

        var unknown = Guid.NewGuid();
        using var missing = await client.GetAsync($"/api/v1/bookings/{unknown}");
        var missingBody = await ProblemAssert.HasProblemAsync(missing, HttpStatusCode.NotFound, ApiProblems.BookingNotFound, $"/api/v1/bookings/{unknown}");
        Assert.Equal(JsonValueKind.Null, missingBody.GetProperty("errors").ValueKind);

        using var other = await client.GetAsync($"/api/v1/bookings/{foreign}?walletId={otherWallet}");
        await ProblemAssert.HasProblemAsync(other, HttpStatusCode.NotFound, ApiProblems.BookingNotFound, $"/api/v1/bookings/{foreign}");
    }

    [Fact]
    public async Task G06_ReadingAnExpiredOfferNeitherReconcilesNorWritesAnything()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), booking.Items[0].ItemId);
        const string versions = "SELECT string_agg(t || ':' || x, ',' ORDER BY t) FROM (SELECT 'w' AS t, xmin::text AS x FROM wallets UNION ALL SELECT 'm' || id, xmin::text FROM machines UNION ALL SELECT 'i' || id, xmin::text FROM service_request_items) v";
        var before = await SnapshotAsync(database, versions);
        clock.SetUtcNow(T0.AddMinutes(5));
        await using var factory = BookingApi.Create(database, clock);

        var item = Assert.Single((await GetAsync(factory.CreateClient(), booking.BookingId)).GetProperty("items").EnumerateArray());

        Assert.Equal("to_be_confirmed", item.GetProperty("status").GetString());
        Assert.Equal("2026-10-21T12:01:00Z", item.GetProperty("expiresAt").GetString());
        Assert.Equal(before, await SnapshotAsync(database, versions));
    }

    [Fact]
    public async Task G07_ReadSeesOneCommittedSnapshotWithoutWaitingForWritersAndIgnoresAccountSelectors()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        var wash = booking.Items.Single(item => item.ServiceCode == "wash").ItemId;
        await using var factory = BookingApi.Create(database, clock);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wallet-Id", "00000000-0000-4000-8000-000000000002");

        // Escritor fora do protocolo com saldo e item alterados, ainda sem commit: a leitura não espera nem mistura.
        await using (var writer = new NpgsqlConnection(Named(database, WriterApp)))
        {
            await writer.OpenAsync();
            await using var transaction = await writer.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand(
                $"UPDATE wallets SET balance = 0.01; UPDATE service_request_items SET status = 'queued', queue_ticket = nextval('queue_ticket_seq'), queued_at = now() WHERE id = '{wash}'",
                writer,
                transaction))
            {
                await command.ExecuteNonQueryAsync();
            }

            var during = await GetAsync(client, booking.BookingId, "?walletId=00000000-0000-4000-8000-000000000002&userId=x").WaitAsync(SignalTimeout);
            Assert.Equal((1020L, "dry|2090|pending_activation;wash|1890|pending_activation"), Summary(during));

            await transaction.CommitAsync();
        }

        var after = await GetAsync(client, booking.BookingId);
        Assert.Equal((1L, "dry|2090|pending_activation;wash|1890|queued"), Summary(after));
    }

    [Fact]
    public async Task G08_BodiesExampleProblemsParameterAndReadLimitMatchTheGeneratedDocument()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var booking = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        await ActivateAsync(Sessions(database, SetupApp), Coordinator(clock), booking.Items.Single(item => item.ServiceCode == "wash").ItemId);
        await using var factory = BookingApi.Create(database, clock, new Dictionary<string, string> { ["RateLimiting:BookingReads:PermitLimit"] = "3" });
        using var client = factory.CreateClient();
        var openApi = await OpenApiContract.FetchAsync(client);
        using var ok = await client.GetAsync($"/api/v1/bookings/{booking.BookingId}");
        using var invalid = await client.GetAsync("/api/v1/bookings/123");
        using var missing = await client.GetAsync($"/api/v1/bookings/{Guid.NewGuid()}");
        using var limited = await client.GetAsync($"/api/v1/bookings/{booking.BookingId}");
        using var wallet = await client.GetAsync("/api/v1/wallet");
        await using var downFactory = BookingApi.Create(new TestDatabase(ApiFactory.UnreachableDatabase), clock);
        using var unavailable = await downFactory.CreateClient().GetAsync($"/api/v1/bookings/{booking.BookingId}");

        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Route, "GET", "GetBooking")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Route].Operations![HttpMethod.Get];
        Assert.Equal(["Bookings"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "400", "404", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.True(operation.Responses["429"].Headers!["Retry-After"].Required);
        var parameter = Assert.Single(operation.Parameters!);
        Assert.Equal("bookingId", parameter.Name);
        Assert.Equal(ParameterLocation.Path, parameter.In);
        Assert.True(parameter.Required);
        Assert.Equal("uuid", parameter.Schema!.Format);
        Assert.Equal(JsonSchemaType.String, parameter.Schema.Type);
        Assert.False(string.IsNullOrWhiteSpace(parameter.Description));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200"), await ok.Content.ReadAsStringAsync());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "200"), JsonSchemaContract.Example(openApi, Route, "200"));
        await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, "/api/v1/bookings/123");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var limitedBody = await ProblemAssert.HasProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, $"/api/v1/bookings/{booking.BookingId}");
        Assert.InRange(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture), 1, 60);
        // A política de leituras de contratações é separada da de wallet/catálogo.
        Assert.Equal(HttpStatusCode.OK, wallet.StatusCode);
        await ProblemAssert.HasProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, $"/api/v1/bookings/{booking.BookingId}");
        foreach (var (status, response) in new[] { ("400", invalid), ("404", missing), ("503", unavailable) })
        {
            JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, status, "application/problem+json"), await response.Content.ReadAsStringAsync());
        }

        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Route, "429", "application/problem+json"), limitedBody.GetRawText());
        var limits = factory.Services.GetRequiredService<IOptionsMonitor<RateLimitPolicyOptions>>();
        await using var defaults = BookingApi.Create(database, clock);
        var defaultLimits = defaults.Services.GetRequiredService<IOptionsMonitor<RateLimitPolicyOptions>>();
        Assert.Equal((3, 30, 60), (limits.Get(ApiRateLimits.BookingReads).PermitLimit, defaultLimits.Get(ApiRateLimits.BookingReads).PermitLimit, defaultLimits.Get(ApiRateLimits.Reads).PermitLimit));
    }

    [Theory]
    [InlineData("""{"bookingId":"fb0cb8f8-ced3-4ad7-9ee5-e7df973abc19","currency":"BRL","chargedAmountCents":1890,"remainingBalanceCents":3110,"createdAt":"2026-10-21T12:00:00Z"}""")]
    [InlineData("""{"bookingId":"fb0cb8f8-ced3-4ad7-9ee5-e7df973abc19","currency":"BRL","chargedAmountCents":1890,"remainingBalanceCents":-1,"createdAt":"2026-10-21T12:00:00Z","items":[]}""")]
    [InlineData("""{"bookingId":"fb0cb8f8-ced3-4ad7-9ee5-e7df973abc19","currency":"BRL","chargedAmountCents":1890,"remainingBalanceCents":3110,"createdAt":"2026-10-21T12:00:00Z","items":[{"itemId":"ee7cefb9-9150-464f-bc77-90c795d6947d","serviceCode":"wash","unitPriceCents":1890,"status":"cancelled","machineId":null,"queuePosition":null,"expiresAt":null}]}""")]
    [InlineData("""{"bookingId":"fb0cb8f8-ced3-4ad7-9ee5-e7df973abc19","currency":"BRL","chargedAmountCents":1890,"remainingBalanceCents":3110,"createdAt":"2026-10-21T12:00:00Z","items":[{"itemId":"ee7cefb9-9150-464f-bc77-90c795d6947d","serviceCode":"wash","unitPriceCents":1890,"status":"queued","machineId":null,"queuePosition":0,"expiresAt":null}]}""")]
    [InlineData("""{"bookingId":"fb0cb8f8-ced3-4ad7-9ee5-e7df973abc19","currency":"BRL","chargedAmountCents":1890,"remainingBalanceCents":3110,"createdAt":"2026-10-21T12:00:00Z","items":[{"itemId":"ee7cefb9-9150-464f-bc77-90c795d6947d","serviceCode":"wash","unitPriceCents":1890,"status":"queued","machineId":null,"expiresAt":null}]}""")]
    [InlineData("""{"bookingId":"x","currency":"BRL","chargedAmountCents":1890,"remainingBalanceCents":3110,"createdAt":"2026-10-21T12:00:00Z","items":[]}""")]
    public async Task G09_GeneratedSchemaRejectsBodiesThatBreakTheBookingContract(string body)
    {
        await using var factory = ApiFactory.Create(connectionString: null);
        var openApi = await OpenApiContract.FetchAsync(factory.CreateClient());

        JsonSchemaContract.AssertInvalid(openApi, JsonSchemaContract.ResponsePointer(Route, "200"), body);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid bookingId, string query = "")
    {
        using var response = await client.GetAsync($"/api/v1/bookings/{bookingId}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BookingApi.JsonAsync(response);
    }

    /// <summary>Estado de itens/máquinas/wallet, versões de linha (xmin) e sequência de tickets, numa lista comparável.</summary>
    private static async Task<List<string>> SnapshotAsync(TestDatabase database, string versions) =>
        [
            .. await database.RowsAsync(StateSql),
            await database.ScalarAsync<string>(versions),
            (await database.ScalarAsync<long>("SELECT last_value FROM queue_ticket_seq")).ToString(CultureInfo.InvariantCulture),
        ];

    private static string Describe(JsonElement item) =>
        $"{item.GetProperty("serviceCode").GetString()}|{item.GetProperty("unitPriceCents").GetInt64()}|{item.GetProperty("status").GetString()}";

    private static string DescribeWithPosition(JsonElement item) =>
        $"{Describe(item)}|{item.GetProperty("queuePosition").GetInt32()}";

    private static (long Balance, string Items) Summary(JsonElement body) =>
        (body.GetProperty("remainingBalanceCents").GetInt64(), string.Join(';', body.GetProperty("items").EnumerateArray().Select(Describe)));
}
