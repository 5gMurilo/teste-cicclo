using System.Globalization;
using System.Net;
using System.Text.Json;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Tests.Support;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Contracts.Bookings;

/// <summary>
/// GET /api/v1/bookings sobre PostgreSQL 18.1 real (HTTP-05/06/07/08, SEC-01, OPS-04): wrapper items/page/pageSize/
/// hasNextPage, padrão 1/20, limites de A08 e ordem total createdAt desc, id desc, com a mesma projeção do detalhe.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E04")]
[Trait("Task", "T3")]
public sealed class ListBookingsTests(PostgresFixture postgres)
{
    private const string Path = "/api/v1/bookings";
    private const string ForeignBooking = "00000000-0000-4000-8000-0000000000f1";

    [Fact]
    public async Task L01_EmptyCollectionReturnsAnEmptyFirstPageWithTheDefaults()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock());

        using var response = await factory.CreateClient().GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await BookingApi.JsonAsync(response);
        Assert.Equal(["items", "page", "pageSize", "hasNextPage"], body.EnumerateObject().Select(property => property.Name));
        Assert.Equal((0, 1, 20, false), (body.GetProperty("items").GetArrayLength(), body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("hasNextPage").GetBoolean()));
    }

    [Fact]
    public async Task L02_DefaultsReturnTwentyNewestFirstAndTheNextPageTheRest()
    {
        var database = await ReadyAsync(postgres);
        var ids = await InsertBookingsAsync(database, Enumerable.Range(0, 25).Select(index => T0.AddMinutes(index)).ToArray());
        await using var factory = BookingApi.Create(database, Clock());
        using var client = factory.CreateClient();

        var first = await ListAsync(client, string.Empty);
        var second = await ListAsync(client, "?page=2");

        Assert.Equal((1, 20, true), (first.GetProperty("page").GetInt32(), first.GetProperty("pageSize").GetInt32(), first.GetProperty("hasNextPage").GetBoolean()));
        Assert.Equal((2, 20, false), (second.GetProperty("page").GetInt32(), second.GetProperty("pageSize").GetInt32(), second.GetProperty("hasNextPage").GetBoolean()));
        Assert.Equal(ids.AsEnumerable().Reverse(), Ids(first).Concat(Ids(second)));
        Assert.Equal("2026-10-21T12:24:00Z", first.GetProperty("items")[0].GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task L03_TiedInstantsAreOrderedByIdDescendingAndPagesNeitherRepeatNorSkip()
    {
        var database = await ReadyAsync(postgres);
        // Três compras no mesmo instante, gravadas fora da ordem de id, entre duas mais antigas e uma mais nova.
        var tie = T0.AddMinutes(5);
        var ids = new[]
        {
            Guid.Parse("00000000-0000-4000-8000-0000000000b2"), Guid.Parse("00000000-0000-4000-8000-0000000000b9"), Guid.Parse("00000000-0000-4000-8000-0000000000b5"),
            Guid.Parse("00000000-0000-4000-8000-0000000000a1"), Guid.Parse("00000000-0000-4000-8000-0000000000a2"), Guid.Parse("00000000-0000-4000-8000-0000000000c1"),
        };
        var instants = new[] { tie, tie, tie, T0, T0.AddMinutes(1), T0.AddMinutes(9) };
        for (var index = 0; index < ids.Length; index++)
        {
            await InsertBookingAsync(database, ids[index], instants[index]);
        }

        await using var factory = BookingApi.Create(database, Clock(), new Dictionary<string, string> { ["RateLimiting:BookingReads:PermitLimit"] = "100" });
        using var client = factory.CreateClient();

        var pages = new List<JsonElement>();
        for (var page = 1; page <= 3; page++)
        {
            pages.Add(await ListAsync(client, $"?page={page}&pageSize=2"));
        }

        var repeated = await ListAsync(client, "?page=2&pageSize=2");

        Assert.Equal(["c1", "b9", "b5", "b2", "a2", "a1"], pages.SelectMany(Ids).Select(id => id.ToString()[^2..]));
        Assert.Equal([true, true, false], pages.Select(page => page.GetProperty("hasNextPage").GetBoolean()));
        Assert.Equal(Ids(pages[1]), Ids(repeated));
    }

    [Fact]
    public async Task L04_PageSizeBoundariesOneAndOneHundredAndAnExactlyFullLastPage()
    {
        var database = await ReadyAsync(postgres);
        var ids = await InsertBookingsAsync(database, Enumerable.Range(0, 3).Select(index => T0.AddSeconds(index)).ToArray());
        await using var factory = BookingApi.Create(database, Clock(), new Dictionary<string, string> { ["RateLimiting:BookingReads:PermitLimit"] = "100" });
        using var client = factory.CreateClient();

        var one = await ListAsync(client, "?pageSize=1");
        var lastSingle = await ListAsync(client, "?page=3&pageSize=1");
        var hundred = await ListAsync(client, "?pageSize=100");
        var exact = await ListAsync(client, "?pageSize=3");
        var beyond = await ListAsync(client, "?page=2&pageSize=3");

        Assert.Equal([ids[2]], Ids(one));
        Assert.Equal((1, true), (one.GetProperty("pageSize").GetInt32(), one.GetProperty("hasNextPage").GetBoolean()));
        Assert.Equal([ids[0]], Ids(lastSingle));
        Assert.False(lastSingle.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal((3, 100, false), (hundred.GetProperty("items").GetArrayLength(), hundred.GetProperty("pageSize").GetInt32(), hundred.GetProperty("hasNextPage").GetBoolean()));
        Assert.Equal((3, false), (exact.GetProperty("items").GetArrayLength(), exact.GetProperty("hasNextPage").GetBoolean()));
        Assert.Equal((0, 2, false), (beyond.GetProperty("items").GetArrayLength(), beyond.GetProperty("page").GetInt32(), beyond.GetProperty("hasNextPage").GetBoolean()));
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?page=-1", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?page=0&pageSize=101", "page,pageSize")]
    [InlineData("?page=abc", null)]
    [InlineData("?pageSize=1.5", null)]
    [InlineData("?page=2147483648", null)]
    public async Task L05_InvalidQueryIs400ValidationErrorWithoutOverflow(string query, string? fields)
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock());

        using var response = await factory.CreateClient().GetAsync(Path + query);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.BadRequest, ApiProblems.ValidationError, Path);
        if (fields is null)
        {
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("errors").ValueKind);
        }
        else
        {
            Assert.Equal(fields.Split(','), problem.GetProperty("errors").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task L06_LastIntPageIsAnEmptyPageNotAnOverflow()
    {
        var database = await ReadyAsync(postgres);
        await InsertBookingsAsync(database, [T0]);
        await using var factory = BookingApi.Create(database, Clock());

        var body = await ListAsync(factory.CreateClient(), "?page=2147483647&pageSize=100");

        Assert.Equal((0, 2147483647, 100, false), (body.GetProperty("items").GetArrayLength(), body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("hasNextPage").GetBoolean()));
    }

    [Fact]
    public async Task L07_ListUsesTheDetailProjectionOnlyForTheDemoWalletAndWritesNothing()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 100.00");
        var clock = Clock();
        var offered = await BookingApi.PurchaseAsync(database, clock, ["wash"]);
        clock.Advance(TimeSpan.FromSeconds(1));
        var batch = await BookingApi.PurchaseAsync(database, clock, ["wash", "dry"]);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ActivateAsync(Sessions(database, "cicclo-e04-setup"), Coordinator(clock), batch.Items.Single(item => item.ServiceCode == "wash").ItemId);
        const string otherWallet = "00000000-0000-4000-8000-000000000002";
        await database.ExecuteAsync(
            $"""
            INSERT INTO wallets (id, currency, balance, created_at, updated_at) VALUES ('{otherWallet}', 'BRL', 99.99, now(), now());
            INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
            VALUES ('{ForeignBooking}', '{otherWallet}', gen_random_uuid(), repeat('a', 64), 18.90, '2030-01-01T00:00:00Z');
            INSERT INTO service_request_items (id, request_id, service_code, unit_price, status) VALUES (gen_random_uuid(), '{ForeignBooking}', 'wash', 18.90, 'pending_activation');
            """);
        const string versions = "SELECT string_agg(xmin::text, ',' ORDER BY id) FROM (SELECT id::text, xmin FROM wallets UNION ALL SELECT id::text, xmin FROM machines UNION ALL SELECT id::text, xmin FROM service_request_items) v";
        var before = await database.ScalarAsync<string>(versions);
        await using var factory = BookingApi.Create(database, clock, new Dictionary<string, string> { ["RateLimiting:BookingReads:PermitLimit"] = "100" });
        using var client = factory.CreateClient();

        var list = await ListAsync(client, $"?walletId={otherWallet}");

        Assert.Equal([batch.BookingId, offered.BookingId], Ids(list));
        foreach (var booking in list.GetProperty("items").EnumerateArray())
        {
            using var detail = await client.GetAsync($"{Path}/{booking.GetProperty("bookingId").GetGuid()}");
            Assert.Equal((await BookingApi.JsonAsync(detail)).GetRawText(), booking.GetRawText());
        }

        Assert.Equal(1, list.GetProperty("items")[0].GetProperty("items").EnumerateArray().Single(item => item.GetProperty("serviceCode").GetString() == "wash").GetProperty("queuePosition").GetInt32());
        Assert.Equal(before, await database.ScalarAsync<string>(versions));
    }

    [Fact]
    public async Task L08_SchemaExampleProblemsSharedReadLimitAndUnavailableDatabaseMatchTheContract()
    {
        var database = await ReadyAsync(postgres);
        var booking = await BookingApi.PurchaseAsync(database, Clock(), ["wash", "dry"]);
        await using var factory = BookingApi.Create(database, Clock(), new Dictionary<string, string> { ["RateLimiting:BookingReads:PermitLimit"] = "3" });
        using var client = factory.CreateClient();
        var openApi = await OpenApiContract.FetchAsync(client);
        using var ok = await client.GetAsync(Path);
        using var invalid = await client.GetAsync($"{Path}?pageSize=0");
        using var detail = await client.GetAsync($"{Path}/{booking.BookingId}");
        using var limited = await client.GetAsync(Path);
        await using var down = BookingApi.Create(new TestDatabase(ApiFactory.UnreachableDatabase), Clock());
        using var unavailable = await down.CreateClient().GetAsync(Path);

        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Path, "GET", "ListBookings")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Path].Operations![HttpMethod.Get];
        Assert.Equal(["200", "400", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["page", "pageSize"], operation.Parameters!.Select(parameter => parameter.Name));
        Assert.All(operation.Parameters!, parameter => Assert.False(parameter.Required));
        Assert.All(operation.Parameters!, parameter => Assert.False(string.IsNullOrWhiteSpace(parameter.Description)));
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), await ok.Content.ReadAsStringAsync());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), JsonSchemaContract.Example(openApi, Path, "200"));
        JsonSchemaContract.AssertInvalid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), """{"items":[],"page":0,"pageSize":20,"hasNextPage":false}""");
        JsonSchemaContract.AssertInvalid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), """{"items":[],"page":1,"pageSize":101,"hasNextPage":false}""");
        JsonSchemaContract.AssertInvalid(openApi, JsonSchemaContract.ResponsePointer(Path, "200"), """{"items":[],"page":1,"pageSize":20}""");
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "400", "application/problem+json"), await invalid.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        // Listagem e detalhe compartilham a política de leituras de contratações (30/min por padrão; 3 neste host).
        var limitedBody = await ProblemAssert.HasProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, Path);
        Assert.InRange(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture), 1, 60);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "429", "application/problem+json"), limitedBody.GetRawText());
        var unavailableBody = await ProblemAssert.HasProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path);
        Assert.DoesNotContain("nao-vazar-down", unavailableBody.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(Path + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BookingApi.JsonAsync(response);
    }

    private static List<Guid> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(booking => booking.GetProperty("bookingId").GetGuid())];

    private static async Task<Guid[]> InsertBookingsAsync(TestDatabase database, DateTimeOffset[] instants)
    {
        var ids = new Guid[instants.Length];
        for (var index = 0; index < instants.Length; index++)
        {
            ids[index] = Guid.NewGuid();
            await InsertBookingAsync(database, ids[index], instants[index]);
        }

        return ids;
    }

    /// <summary>Compra da wallet demo com um item pendente e instante explícito (a listagem não depende do débito).</summary>
    private static Task InsertBookingAsync(TestDatabase database, Guid id, DateTimeOffset createdAt) =>
        database.ExecuteAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                INSERT INTO service_requests (id, wallet_id, idempotency_key, request_fingerprint, total_amount, created_at)
                VALUES ('{id}', '{DemoData.WalletId}', gen_random_uuid(), repeat('f', 64), 18.90, '{createdAt:O}');
                INSERT INTO service_request_items (id, request_id, service_code, unit_price, status) VALUES (gen_random_uuid(), '{id}', 'wash', 18.90, 'pending_activation');
                """));
}
