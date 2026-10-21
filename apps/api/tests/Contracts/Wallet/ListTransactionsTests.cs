using System.Globalization;
using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Contracts.Wallet;

/// <summary>
/// GET /api/v1/wallet/transactions sobre PostgreSQL 18.1 real (FIN-11, HTTP-05/06/07/08, SEC-01, OPS-04): lançamentos da
/// wallet demo com valor positivo, saldo posterior histórico, referências documentadas e a paginação de A08.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E04")]
[Trait("Task", "T4")]
public sealed class ListTransactionsTests(PostgresFixture postgres)
{
    private const string Path = "/api/v1/wallet/transactions";
    private static readonly string[] Fields = ["transactionId", "bookingId", "itemId", "kind", "amountCents", "balanceAfterCents", "createdAt"];

    [Fact]
    public async Task T01_WalletWithoutOperationsReturnsAnEmptyFirstPage()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock());

        var body = await ListAsync(factory.CreateClient(), string.Empty);

        Assert.Equal(["items", "page", "pageSize", "hasNextPage"], body.EnumerateObject().Select(property => property.Name));
        Assert.Equal((0, 1, 20, false), (body.GetProperty("items").GetArrayLength(), body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("hasNextPage").GetBoolean()));
    }

    [Fact]
    public async Task T02_WashPurchaseIsOnePositiveDebitOf1890WithBalanceAfter3110()
    {
        var database = await ReadyAsync(postgres);
        var booking = await BookingApi.PurchaseAsync(database, Clock(), ["wash"]);
        await using var factory = BookingApi.Create(database, Clock());

        var entry = Assert.Single((await ListAsync(factory.CreateClient(), string.Empty)).GetProperty("items").EnumerateArray());

        Assert.Equal(Fields, entry.EnumerateObject().Select(property => property.Name));
        Assert.Equal(await database.ScalarAsync<Guid>("SELECT id FROM wallet_transactions"), entry.GetProperty("transactionId").GetGuid());
        Assert.Equal(booking.BookingId, entry.GetProperty("bookingId").GetGuid());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("itemId").ValueKind);
        Assert.Equal("debit", entry.GetProperty("kind").GetString());
        Assert.Equal((1890L, 3110L), (entry.GetProperty("amountCents").GetInt64(), entry.GetProperty("balanceAfterCents").GetInt64()));
        Assert.Equal("2026-10-21T12:00:00Z", entry.GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task T03_SeveralEntriesKeepTheirHistoricalBalanceAfterEvenWhenTheBalanceChangesLater()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        var wash = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.Advance(TimeSpan.FromSeconds(30));
        var dry = await BookingApi.PurchaseAsync(database, clock, ["dry"]);
        await database.ExecuteAsync("UPDATE wallets SET balance = 0.00");
        await using var factory = BookingApi.Create(database, clock);

        var items = (await ListAsync(factory.CreateClient(), string.Empty)).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(
            [$"{dry.BookingId}|debit|2090|1020|2026-10-21T12:00:30Z", $"{wash.BookingId}|debit|1890|3110|2026-10-21T12:00:00Z"],
            items.Select(Describe));
    }

    [Fact]
    public async Task T04_TiedEntriesAreOrderedByIdDescendingAndPagesAreStable()
    {
        var database = await ReadyAsync(postgres);
        var tie = T0.AddMinutes(1);
        var entries = new (string Id, DateTimeOffset At)[]
        {
            ("00000000-0000-4000-8000-0000000000e2", tie), ("00000000-0000-4000-8000-0000000000e9", tie), ("00000000-0000-4000-8000-0000000000e5", tie),
            ("00000000-0000-4000-8000-0000000000d1", T0), ("00000000-0000-4000-8000-0000000000f1", T0.AddMinutes(2)),
        };
        foreach (var (id, at) in entries)
        {
            await InsertDebitAsync(database, Guid.Parse(id), at);
        }

        await using var factory = BookingApi.Create(database, Clock(), new Dictionary<string, string> { ["RateLimiting:BookingReads:PermitLimit"] = "100" });
        using var client = factory.CreateClient();

        var pages = new List<JsonElement>();
        for (var page = 1; page <= 3; page++)
        {
            pages.Add(await ListAsync(client, $"?page={page}&pageSize=2"));
        }

        var again = await ListAsync(client, "?page=2&pageSize=2");
        var exact = await ListAsync(client, "?pageSize=5");

        Assert.Equal(["f1", "e9", "e5", "e2", "d1"], pages.SelectMany(Ids).Select(id => id[^2..]));
        Assert.Equal([true, true, false], pages.Select(page => page.GetProperty("hasNextPage").GetBoolean()));
        Assert.Equal(Ids(pages[1]), Ids(again));
        Assert.False(exact.GetProperty("hasNextPage").GetBoolean());
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?page=x", null)]
    public async Task T05_InvalidQueryIs400AndUnavailableDatabaseIs503WithoutLeaks(string query, string? field)
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock());
        await using var down = BookingApi.Create(new TestDatabase(ApiFactory.UnreachableDatabase), Clock());

        using var invalid = await factory.CreateClient().GetAsync(Path + query);
        using var unavailable = await down.CreateClient().GetAsync(Path);

        var problem = await ProblemAssert.HasProblemAsync(invalid, HttpStatusCode.BadRequest, ApiProblems.ValidationError, Path);
        if (field is null)
        {
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("errors").ValueKind);
        }
        else
        {
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(property => property.Name));
        }

        var down503 = await ProblemAssert.HasProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path);
        foreach (var leak in new[] { "nao-vazar-down", "wallet_transactions", "SELECT", "Npgsql" })
        {
            Assert.DoesNotContain(leak, down503.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task T06_SchemaTypesNullsSharedLimitDemoWalletOnlyAndNoFinancialEffect()
    {
        var database = await ReadyAsync(postgres);
        var booking = await BookingApi.PurchaseAsync(database, Clock(), ["wash", "dry"]);
        const string otherWallet = "00000000-0000-4000-8000-000000000002";
        await database.ExecuteAsync(
            $"""
            INSERT INTO wallets (id, currency, balance, created_at, updated_at) VALUES ('{otherWallet}', 'BRL', 80.00, now(), now());
            INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
            VALUES ('00000000-0000-4000-8000-0000000000aa', '{otherWallet}', gen_random_uuid(), repeat('a', 64), 18.90, '2030-01-01T00:00:00Z');
            INSERT INTO wallet_transactions (id, wallet_id, request_id, request_item_id, kind, amount, balance_after, created_at)
            VALUES (gen_random_uuid(), '{otherWallet}', '00000000-0000-4000-8000-0000000000aa', NULL, 'debit', 18.90, 61.10, '2030-01-01T00:00:00Z');
            """);
        const string state = "SELECT string_agg(t || ':' || x, ',' ORDER BY t) FROM (SELECT id::text AS t, xmin::text || balance::text AS x FROM wallets UNION ALL SELECT id::text, xmin::text FROM wallet_transactions) v";
        var before = await database.ScalarAsync<string>(state);
        await using var factory = BookingApi.Create(database, Clock(), new Dictionary<string, string> { ["RateLimiting:BookingReads:PermitLimit"] = "3" });
        using var client = factory.CreateClient();
        var openApi = await OpenApiContract.FetchAsync(client);
        using var ok = await client.GetAsync($"{Path}?walletId={otherWallet}");
        using var bookings = await client.GetAsync("/api/v1/bookings");
        using var invalid = await client.GetAsync($"{Path}?page=0");
        using var limited = await client.GetAsync(Path);
        using var wallet = await client.GetAsync("/api/v1/wallet");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var page = await BookingApi.JsonAsync(ok);
        Assert.Equal([$"{booking.BookingId}|debit|3980|1020|2026-10-21T12:00:00Z"], page.GetProperty("items").EnumerateArray().Select(Describe));
        Assert.Equal(HttpStatusCode.OK, bookings.StatusCode);
        // Histórico e leituras de contratações compartilham a política de 30/min (3 neste host); wallet/catálogo é outra.
        var limitedBody = await ProblemAssert.HasProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, Path);
        Assert.InRange(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture), 1, 60);
        Assert.Equal(HttpStatusCode.OK, wallet.StatusCode);
        Assert.Equal(before, await database.ScalarAsync<string>(state));

        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Path, "GET", "ListWalletTransactions")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Path].Operations![HttpMethod.Get];
        Assert.Equal(["Wallet"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "400", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["page", "pageSize"], operation.Parameters!.Select(parameter => parameter.Name));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), page.GetRawText());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), JsonSchemaContract.Example(openApi, Path, "200"));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "400", "application/problem+json"), await invalid.Content.ReadAsStringAsync());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "429", "application/problem+json"), limitedBody.GetRawText());
        const string valid = """{"transactionId":"01a2b3c4-0000-7000-8000-000000000001","bookingId":"fb0cb8f8-ced3-4ad7-9ee5-e7df973abc19","itemId":null,"kind":"debit","amountCents":1890,"balanceAfterCents":3110,"createdAt":"2026-10-21T12:00:00Z"}""";
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), Page(valid));
        foreach (var broken in new[]
        {
            valid.Replace("\"amountCents\":1890", "\"amountCents\":-1890", StringComparison.Ordinal),
            valid.Replace("\"amountCents\":1890", "\"amountCents\":0", StringComparison.Ordinal),
            valid.Replace("\"balanceAfterCents\":3110", "\"balanceAfterCents\":-1", StringComparison.Ordinal),
            valid.Replace("\"kind\":\"debit\"", "\"kind\":\"credit\"", StringComparison.Ordinal),
            valid.Replace("\"itemId\":null,", string.Empty, StringComparison.Ordinal),
            valid.Replace("\"amountCents\":1890", "\"amountCents\":18.9", StringComparison.Ordinal),
        })
        {
            JsonSchemaContract.AssertInvalid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), Page(broken));
        }
    }

    private static string Page(string item) => $$"""{"items":[{{item}}],"page":1,"pageSize":20,"hasNextPage":false}""";

    private static async Task<JsonElement> ListAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(Path + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return await BookingApi.JsonAsync(response);
    }

    private static List<string> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(entry => entry.GetProperty("transactionId").GetString()!)];

    private static string Describe(JsonElement entry) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.GetProperty("bookingId").GetGuid()}|{entry.GetProperty("kind").GetString()}|{entry.GetProperty("amountCents").GetInt64()}|{entry.GetProperty("balanceAfterCents").GetInt64()}|{entry.GetProperty("createdAt").GetString()}");

    /// <summary>Compra da wallet demo com débito de 18.90 gravado por SQL no instante e id indicados.</summary>
    private static Task InsertDebitAsync(TestDatabase database, Guid transactionId, DateTimeOffset at)
    {
        var bookingId = Guid.NewGuid();
        return database.ExecuteAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
                VALUES ('{bookingId}', '{DemoData.WalletId}', gen_random_uuid(), repeat('f', 64), 18.90, '{at:O}');
                INSERT INTO wallet_transactions (id, wallet_id, request_id, request_item_id, kind, amount, balance_after, created_at)
                VALUES ('{transactionId}', '{DemoData.WalletId}', '{bookingId}', NULL, 'debit', 18.90, 31.10, '{at:O}');
                """));
    }
}
