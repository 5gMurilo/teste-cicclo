using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Cicclo.Api.Bookings;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Platform.Http;
using Cicclo.Api.Platform.Persistence;
using Cicclo.Api.Tests.Integration.Bookings;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Contracts.Bookings;

/// <summary>
/// POST /api/v1/bookings pelo host real e PostgreSQL 18.1 (FIN-01..09/11, IDEM-01..07, HTTP-01/02/06..09,
/// QUEUE-01..03, OPS-03..06): cada cenário confere resposta e banco. Valores esperados vêm da spec e do handoff 6.4.
/// O limite de compras só é reduzido nos cenários próprios de 429; os demais o elevam para não interferir.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E04")]
[Trait("Task", "T2")]
public sealed class CreateBookingEndpointTests(PostgresFixture postgres)
{
    private const string Path = "/api/v1/bookings";
    private const string Detail = "/api/v1/bookings/{bookingId}";

    private const string CountsSql =
        "SELECT (SELECT count(*) FROM service_requests) || '|' || (SELECT count(*) FROM service_request_items) || '|' || (SELECT count(*) FROM wallet_transactions) || '|' || (SELECT balance::text FROM wallets)";

    private static readonly Dictionary<string, string> Unlimited = new() { ["RateLimiting:BookingCreate:PermitLimit"] = "1000", ["RateLimiting:BookingReads:PermitLimit"] = "1000" };

    [Theory]
    [InlineData("""{"serviceCodes":["wash"],"expectedTotalCents":1890}""", 1890L, 3110L, "31.10", "wash|1890|to_be_confirmed|washer")]
    [InlineData("""{"serviceCodes":["dry"],"expectedTotalCents":2090}""", 2090L, 2910L, "29.10", "dry|2090|to_be_confirmed|dryer")]
    [InlineData("""{"serviceCodes":["wash","dry"],"expectedTotalCents":3980}""", 3980L, 1020L, "10.20", "dry|2090|pending_activation|null;wash|1890|pending_activation|null")]
    [InlineData("""{"serviceCodes":["wash","wash"],"expectedTotalCents":3780}""", 3780L, 1220L, "12.20", "wash|1890|pending_activation|null;wash|1890|pending_activation|null")]
    [InlineData("""{"serviceCodes":["dry","wash"],"preferredFirstServiceCode":"wash","expectedTotalCents":3980}""", 3980L, 1020L, "10.20", "dry|2090|pending_activation|null;wash|1890|to_be_confirmed|washer")]
    public async Task H01_FirstConfirmationReturns201WithLocationChargeBalanceAndInitialActivation(
        string body, long charged, long remaining, string balance, string items)
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        using var client = factory.CreateClient();

        using var response = await PostAsync(client, Guid.NewGuid(), body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var created = await BookingApi.JsonAsync(response);
        var bookingId = created.GetProperty("bookingId").GetGuid();
        Assert.Equal($"/api/v1/bookings/{bookingId}", response.Headers.Location!.OriginalString);
        Assert.Equal((charged, remaining, "BRL"), (created.GetProperty("chargedAmountCents").GetInt64(), created.GetProperty("remainingBalanceCents").GetInt64(), created.GetProperty("currency").GetString()));
        Assert.Equal("2026-10-21T12:00:00Z", created.GetProperty("createdAt").GetString());
        Assert.Equal(items, Items(created));
        Assert.All(created.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("status").GetString() == "to_be_confirmed"), item =>
            Assert.Equal("2026-10-21T12:01:00Z", item.GetProperty("expiresAt").GetString()));
        Assert.Equal($"1|{items.Split(';').Length}|1|{balance}", await database.ScalarAsync<string>(CountsSql));
        Assert.Equal(
            $"{bookingId}|debit|{(charged / 100m).ToString("0.00", CultureInfo.InvariantCulture)}|{balance}",
            (await database.RowsAsync("SELECT request_id, kind, amount::text, balance_after::text FROM wallet_transactions")).Single());

        using var detail = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(created.GetRawText(), (await BookingApi.JsonAsync(detail)).GetRawText());
    }

    [Fact]
    public async Task H02_ReplayReturns200WithTheSameBookingHistoricalChargeCurrentStateAndBalanceWithoutDebit()
    {
        var database = await ReadyAsync(postgres);
        var clock = Clock();
        await using var factory = BookingApi.Create(database, clock, Unlimited);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        var created = await BookingApi.JsonAsync(await PostAsync(client, key, """{"serviceCodes":["wash"],"expectedTotalCents":1890}"""));
        // Depois da compra: preço, capacidade e saldo mudam; o replay recupera a compra antes de qualquer regra dinâmica.
        await database.ExecuteAsync("UPDATE services SET current_price = 25.00 WHERE code = 'wash'");
        using (var other = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":["dry"],"expectedTotalCents":2090}"""))
        {
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        }

        await database.ExecuteAsync("UPDATE wallets SET balance = 0.00; UPDATE services SET is_active = false WHERE code = 'wash'");

        using var replay = await PostAsync(client, key, """{"expectedTotalCents":1890,"serviceCodes":["wash"],"preferredFirstServiceCode":null}""");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Null(replay.Headers.Location);
        var body = await BookingApi.JsonAsync(replay);
        Assert.Equal(created.GetProperty("bookingId").GetGuid(), body.GetProperty("bookingId").GetGuid());
        Assert.Equal((1890L, 0L), (body.GetProperty("chargedAmountCents").GetInt64(), body.GetProperty("remainingBalanceCents").GetInt64()));
        Assert.Equal(Items(created), Items(body));
        Assert.Equal("2|2|2|0.00", await database.ScalarAsync<string>(CountsSql));
    }

    [Theory]
    [InlineData("""{"serviceCodes":["wash","dry"],"expectedTotalCents":3980}""", """{"expectedTotalCents":3980,"serviceCodes":["dry","wash"]}""", HttpStatusCode.OK)]
    [InlineData("""{"serviceCodes":["wash","dry"],"expectedTotalCents":3980}""", """{"serviceCodes":["dry","wash"],"preferredFirstServiceCode":null,"expectedTotalCents":3980}""", HttpStatusCode.OK)]
    [InlineData("""{"serviceCodes":["wash","dry"],"expectedTotalCents":3980}""", """{"serviceCodes":["wash","dry"],"preferredFirstServiceCode":"wash","expectedTotalCents":3980}""", HttpStatusCode.Conflict)]
    [InlineData("""{"serviceCodes":["wash","wash"],"expectedTotalCents":3780}""", """{"serviceCodes":["wash"],"expectedTotalCents":1890}""", HttpStatusCode.Conflict)]
    [InlineData("""{"serviceCodes":["wash"],"expectedTotalCents":1890}""", """{"serviceCodes":["dry"],"expectedTotalCents":2090}""", HttpStatusCode.Conflict)]
    public async Task H03_SameKeyRecoversEquivalentIntentsAndConflictsOtherwiseWithoutMutation(string first, string second, HttpStatusCode expected)
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        var created = await BookingApi.JsonAsync(await PostAsync(client, key, first));
        var before = await database.RowsAsync(StateSql);

        using var response = await PostAsync(client, key, second);

        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(created.GetProperty("bookingId").GetGuid(), (await BookingApi.JsonAsync(response)).GetProperty("bookingId").GetGuid());
        }
        else
        {
            await ProblemAssert.HasProblemAsync(response, HttpStatusCode.Conflict, ApiProblems.IdempotencyConflict, Path);
        }

        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM wallet_transactions"));
    }

    [Fact]
    public async Task H04_InsufficientBalanceIs409WithoutMutationAndTheKeyStaysUsable()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 10.00");
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        var before = await database.RowsAsync(StateSql);

        using var rejected = await PostAsync(client, key, """{"serviceCodes":["wash"],"expectedTotalCents":1890}""");

        await ProblemAssert.HasProblemAsync(rejected, HttpStatusCode.Conflict, ApiProblems.WalletInsufficientBalance, Path);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        await database.ExecuteAsync("UPDATE wallets SET balance = 18.90");
        using var accepted = await PostAsync(client, key, """{"serviceCodes":["wash"],"expectedTotalCents":1890}""");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(0L, (await BookingApi.JsonAsync(accepted)).GetProperty("remainingBalanceCents").GetInt64());
    }

    [Fact]
    public async Task H05_DivergentExpectedTotalIs409PriceChangedWithTheServerTotalAndNoMutation()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        var before = await database.RowsAsync(StateSql);

        using var rejected = await PostAsync(client, key, """{"serviceCodes":["wash","dry"],"expectedTotalCents":3900}""");

        var problem = await ProblemAssert.HasProblemAsync(rejected, HttpStatusCode.Conflict, ApiProblems.PriceChanged, Path);
        Assert.Contains("3980", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        using var accepted = await PostAsync(client, key, """{"serviceCodes":["wash","dry"],"expectedTotalCents":3980}""");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Theory]
    [InlineData("", """{"serviceCodes":["wash","iron"],"expectedTotalCents":3780}""", HttpStatusCode.NotFound, ApiProblems.ServiceNotFound)]
    [InlineData("UPDATE services SET is_active = false WHERE code = 'dry'", """{"serviceCodes":["wash","dry"],"expectedTotalCents":3980}""", HttpStatusCode.Conflict, ApiProblems.ServiceDisabled)]
    [InlineData("UPDATE machines SET state = 'out_of_service' WHERE machine_type = 'dryer'", """{"serviceCodes":["dry","wash"],"expectedTotalCents":3980}""", HttpStatusCode.Conflict, ApiProblems.NoOperationalMachine)]
    public async Task H06_InvalidInactiveOrUnservedServiceRejectsTheWholeBatchWithoutMutation(string setup, string body, HttpStatusCode status, string errorCode)
    {
        var database = await ReadyAsync(postgres, setup.Length > 0 ? setup : null);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        var before = await database.RowsAsync(StateSql);

        using var response = await PostAsync(factory.CreateClient(), Guid.NewGuid(), body);

        await ProblemAssert.HasProblemAsync(response, status, errorCode, Path);
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Theory]
    [InlineData(null, """{"serviceCodes":["wash"],"expectedTotalCents":1890}""", "Idempotency-Key")]
    [InlineData("nao-e-uuid", """{"serviceCodes":["wash"],"expectedTotalCents":1890}""", null)]
    [InlineData("k", "null", "body")]
    [InlineData("k", "{", "body")]
    [InlineData("k", """{"serviceCodes":["wash"],"expectedTotalCents":"1890"}""", "expectedTotalCents")]
    [InlineData("k", """{"serviceCodes":["wash"],"expectedTotalCents":1890.5}""", "expectedTotalCents")]
    [InlineData("k", """{"serviceCodes":"wash","expectedTotalCents":1890}""", "serviceCodes")]
    [InlineData("k", """{"serviceCodes":["wash"],"expectedTotalCents":1890,"walletId":"00000000-0000-4000-8000-000000000001"}""", "walletId")]
    [InlineData("k", """{"serviceCodes":["wash"],"expectedTotalCents":1890,"unitPriceCents":1}""", "unitPriceCents")]
    [InlineData("k", """{"serviceCodes":["wash"]}""", "expectedTotalCents")]
    [InlineData("k", """{"serviceCodes":["wash"],"expectedTotalCents":0}""", "expectedTotalCents")]
    [InlineData("k", """{"serviceCodes":["wash"],"expectedTotalCents":-1890}""", "expectedTotalCents")]
    [InlineData("k", """{"serviceCodes":["wash",null],"expectedTotalCents":1890}""", "serviceCodes")]
    [InlineData("k", """{"serviceCodes":[null],"expectedTotalCents":1}""", "serviceCodes")]
    public async Task H07_MalformedHeaderOrBodyIs400ValidationErrorWithoutMutation(string? key, string body, string? field)
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        var before = await database.RowsAsync(StateSql);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(CreateBookingEndpoint.IdempotencyKeyHeader, key == "k" ? Guid.NewGuid().ToString() : key);
        }

        using var response = await factory.CreateClient().SendAsync(request);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.BadRequest, ApiProblems.ValidationError, Path);
        if (field is null)
        {
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("errors").ValueKind);
        }
        else
        {
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(property => property.Name));
        }

        foreach (var leak in new[] { "System.", "Nullable", "Int64", "CreateBookingRequest", " at " })
        {
            Assert.DoesNotContain(leak, problem.GetRawText(), StringComparison.Ordinal);
        }

        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Theory]
    [InlineData("""{"expectedTotalCents":1890}""", "serviceCodes")]
    [InlineData("""{"serviceCodes":null,"expectedTotalCents":1890}""", "serviceCodes")]
    [InlineData("""{"serviceCodes":[],"expectedTotalCents":1890}""", "serviceCodes")]
    [InlineData("""{"serviceCodes":["wash","wash","wash","wash","wash","wash","wash","wash","wash","wash","wash"],"expectedTotalCents":20790}""", "serviceCodes")]
    [InlineData("""{"serviceCodes":["wash","dry"],"preferredFirstServiceCode":"iron","expectedTotalCents":3980}""", "preferredFirstServiceCode")]
    [InlineData("""{"serviceCodes":["wash"],"preferredFirstServiceCode":"WASH","expectedTotalCents":1890}""", "preferredFirstServiceCode")]
    public async Task H08_StructurallyInvalidSelectionIs400InvalidServiceSelectionWithoutMutation(string body, string field)
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        var before = await database.RowsAsync(StateSql);

        using var response = await PostAsync(factory.CreateClient(), Guid.NewGuid(), body);

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.BadRequest, ApiProblems.InvalidServiceSelection, Path);
        Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(property => property.Name));
        Assert.Equal(before, await database.RowsAsync(StateSql));
    }

    [Fact]
    public async Task H09_TenItemsAreAcceptedWhileOversizedBodyIs413AndNonJsonMediaIs415()
    {
        var database = await ReadyAsync(postgres, "UPDATE wallets SET balance = 200.00");
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        using var client = factory.CreateClient();

        using var ten = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":["wash","wash","wash","wash","wash","dry","dry","dry","dry","dry"],"expectedTotalCents":19900}""");
        var huge = $$"""{"serviceCodes":["wash"],"expectedTotalCents":1890,"preferredFirstServiceCode":"{{new string('x', 16 * 1024)}}"}""";
        using var tooLarge = await PostAsync(client, Guid.NewGuid(), huge);
        using var plain = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":["wash"],"expectedTotalCents":1890}""", "text/plain");
        using var noBody = new HttpRequestMessage(HttpMethod.Post, Path);
        noBody.Headers.Add(CreateBookingEndpoint.IdempotencyKeyHeader, Guid.NewGuid().ToString());
        using var withoutContentType = await client.SendAsync(noBody);

        Assert.Equal(HttpStatusCode.Created, ten.StatusCode);
        Assert.Equal(10, (await BookingApi.JsonAsync(ten)).GetProperty("items").GetArrayLength());
        await ProblemAssert.HasProblemAsync(tooLarge, HttpStatusCode.RequestEntityTooLarge, ApiProblems.PayloadTooLarge, Path);
        await ProblemAssert.HasProblemAsync(plain, HttpStatusCode.UnsupportedMediaType, ApiProblems.UnsupportedMediaType, Path);
        var empty = await ProblemAssert.HasProblemAsync(withoutContentType, HttpStatusCode.BadRequest, ApiProblems.ValidationError, Path);
        Assert.Equal(["body"], empty.GetProperty("errors").EnumerateObject().Select(property => property.Name));
        Assert.Equal("1|10|1|1.00", await database.ScalarAsync<string>(CountsSql));
    }

    [Fact]
    public async Task H10_TwentyParallelCallsWithTheSameKeyCreateOneBookingWithOne201AndNineteen200()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostAsync(client, key, """{"serviceCodes":["wash"],"expectedTotalCents":1890}""")));

        try
        {
            Assert.Equal((1, 19), (responses.Count(response => response.StatusCode == HttpStatusCode.Created), responses.Count(response => response.StatusCode == HttpStatusCode.OK)));
            var ids = new HashSet<Guid>();
            foreach (var response in responses)
            {
                ids.Add((await BookingApi.JsonAsync(response)).GetProperty("bookingId").GetGuid());
            }

            Assert.Single(ids);
            Assert.Equal("1|1|1|31.10", await database.ScalarAsync<string>(CountsSql));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task H11_ThreeParallelWashPurchasesWithDistinctKeysConfirmExactlyTwo()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PostAsync(client, Guid.NewGuid(), """{"serviceCodes":["wash"],"expectedTotalCents":1890}""")));

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order());
        await ProblemAssert.HasProblemAsync(responses.Single(response => response.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, ApiProblems.WalletInsufficientBalance, Path);
        Assert.Equal("2|2|2|12.20", await database.ScalarAsync<string>(CountsSql));
        Assert.Equal(["queued", "to_be_confirmed"], await database.RowsAsync("SELECT status FROM service_request_items ORDER BY status"));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Theory]
    [InlineData("machines", "BEFORE UPDATE", "WHEN (NEW.state = 'reserved')")]
    [InlineData("wallet_transactions", "BEFORE INSERT", "")]
    public async Task H12_FailureWhileSavingTheLedgerOrAssigningTheMachineIsASanitized500AndRevertsEverything(string table, string timing, string when)
    {
        var database = await ReadyAsync(postgres);
        await database.ExecuteAsync(
            $"""
            CREATE FUNCTION fail_purchase() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada SELECT secreto'; END $$;
            CREATE TRIGGER fail_purchase {timing} ON {table} FOR EACH ROW {when} EXECUTE FUNCTION fail_purchase();
            """);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        var before = await database.RowsAsync(StateSql);

        using var response = await PostAsync(factory.CreateClient(), Guid.NewGuid(), """{"serviceCodes":["wash"],"expectedTotalCents":1890}""");

        var problem = await ProblemAssert.HasProblemAsync(response, HttpStatusCode.InternalServerError, ApiProblems.InternalError, Path);
        Assert.DoesNotContain("secreto", problem.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, await database.RowsAsync(StateSql));
        Assert.Equal("0|0|0|50.00", await database.ScalarAsync<string>(CountsSql));
    }

    [Fact]
    public async Task H13_ResponseLostAfterCommitIsRecoveredByRepeatingTheSameKeyWithoutChargingAgain()
    {
        var database = await ReadyAsync(postgres);
        using var lost = new CancellationTokenSource();
        var commits = 0;
        await using var factory = BookingApi.Create(database, Clock(), Unlimited).WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddScoped<ICreateBookingSessions>(provider => new ScriptedBookingSessions(new PostgresCreateBookingSessions(provider.GetRequiredService<IDbContextFactory<CiccloDbContext>>()))
            {
                OnCommit = async (_, commit) =>
                {
                    await commit();
                    if (Interlocked.Increment(ref commits) == 1)
                    {
                        // A compra já confirmou; o cliente desiste antes de receber a resposta.
                        await lost.CancelAsync();
                    }
                },
            })));
        using var client = factory.CreateClient();
        var key = Guid.NewGuid();
        const string body = """{"serviceCodes":["wash"],"expectedTotalCents":1890}""";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostAsync(client, key, body, cancellationToken: lost.Token));
        Assert.Equal("1|1|1|31.10", await database.ScalarAsync<string>(CountsSql));

        using var retry = await PostAsync(client, key, body);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await database.ScalarAsync<Guid>("SELECT id FROM service_requests"), (await BookingApi.JsonAsync(retry)).GetProperty("bookingId").GetGuid());
        Assert.Equal("1|1|1|31.10", await database.ScalarAsync<string>(CountsSql));
    }

    [Fact]
    public async Task H14_PurchaseLimitIsFivePerMinuteByDefaultAndRejectsWith429RetryAfterWithoutMutation()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), new Dictionary<string, string> { ["RateLimiting:BookingCreate:PermitLimit"] = "2" });
        using var client = factory.CreateClient();

        using var first = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":["wash"],"expectedTotalCents":1890}""");
        using var invalid = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":[],"expectedTotalCents":1890}""");
        using var limited = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":["dry"],"expectedTotalCents":2090}""");
        using var read = await client.GetAsync(first.Headers.Location);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.BadRequest), (first.StatusCode, invalid.StatusCode));
        var problem = await ProblemAssert.HasProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimitExceeded, Path);
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("errors").ValueKind);
        Assert.InRange(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture), 1, 60);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("1|1|1|31.10", await database.ScalarAsync<string>(CountsSql));
        await using var defaults = BookingApi.Create(database, Clock());
        Assert.Equal(5, defaults.Services.GetRequiredService<IOptionsMonitor<RateLimitPolicyOptions>>().Get(ApiRateLimits.BookingCreate).PermitLimit);
    }

    [Fact]
    public async Task H15_PurchaseProducesACorrelatedBusinessSpanLogAndLowCardinalityAcceptedRejectedCounter()
    {
        var database = await ReadyAsync(postgres);
        var logs = new CapturingLoggerProvider();
        await using var factory = BookingApi.Create(database, Clock(), Unlimited).WithWebHostBuilder(host => host.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient();
        using var spans = new SpanCapture();
        using var counter = new CounterCapture(factory.Services.GetRequiredService<IMeterFactory>());
        var key = Guid.NewGuid();
        var traceId = ActivityTraceId.CreateRandom();

        using var created = await PostAsync(client, key, """{"serviceCodes":["wash"],"expectedTotalCents":1890}""", traceparent: $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        using var replay = await PostAsync(client, key, """{"serviceCodes":["wash"],"expectedTotalCents":1890}""");
        using var conflict = await PostAsync(client, key, """{"serviceCodes":["dry"],"expectedTotalCents":2090}""");
        using var invalid = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":["wash"],"expectedTotalCents":0}""");

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.BadRequest],
            new[] { created, replay, conflict, invalid }.Select(response => response.StatusCode));
        var bookingId = (await BookingApi.JsonAsync(created)).GetProperty("bookingId").GetGuid();
        var business = Assert.Single(spans.Stopped, span => span.OperationName == "booking.create" && span.TraceId == traceId);
        var http = Assert.Single(spans.Stopped, span => span.Source.Name == "Microsoft.AspNetCore" && span.TraceId == traceId);
        Assert.Equal(http.SpanId, business.ParentSpanId);
        Assert.Equal("Created", business.GetTagItem("booking.outcome"));
        Assert.Equal(bookingId.ToString(), business.GetTagItem("booking.id"));
        var log = Assert.Single(logs.Entries, entry => entry.Category == typeof(CreateBooking).FullName && entry.EventId.Id == 100);
        Assert.Equal(traceId.ToHexString(), log.Scopes["TraceId"]);
        Assert.Equal(business.SpanId.ToHexString(), log.Scopes["SpanId"]);
        Assert.Contains(bookingId.ToString(), log.Message, StringComparison.Ordinal);
        Assert.Equal(["outcome=accepted", "outcome=accepted", "outcome=rejected", "outcome=rejected"], counter.Measurements.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task H16_UnreachableOtlpCollectorDoesNotPreventOrRollBackThePurchase()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), new Dictionary<string, string>(Unlimited) { ["OpenTelemetry:OtlpEndpoint"] = "http://127.0.0.1:1/" });
        using var client = factory.CreateClient();

        using var response = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":["wash","dry"],"expectedTotalCents":3980}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("1|2|1|10.20", await database.ScalarAsync<string>(CountsSql));
        using var detail = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task H17_UnavailableDatabaseOrMissingDemoWalletIs503WithoutClaimingSuccess()
    {
        var database = await ReadyAsync(postgres);
        await database.ExecuteAsync("DELETE FROM wallets");
        await using var missingWallet = BookingApi.Create(database, Clock(), Unlimited);
        await using var down = BookingApi.Create(new TestDatabase(ApiFactory.UnreachableDatabase), Clock(), Unlimited);

        using var noWallet = await PostAsync(missingWallet.CreateClient(), Guid.NewGuid(), """{"serviceCodes":["wash"],"expectedTotalCents":1890}""");
        using var noDatabase = await PostAsync(down.CreateClient(), Guid.NewGuid(), """{"serviceCodes":["wash"],"expectedTotalCents":1890}""");

        await ProblemAssert.HasProblemAsync(noWallet, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path);
        var problem = await ProblemAssert.HasProblemAsync(noDatabase, HttpStatusCode.ServiceUnavailable, ApiProblems.DatabaseUnavailable, Path);
        Assert.DoesNotContain("nao-vazar-down", problem.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("0|0|0", (await database.RowsAsync(
            "SELECT (SELECT count(*) FROM service_requests), (SELECT count(*) FROM service_request_items), (SELECT count(*) FROM wallet_transactions)")).Single());
    }

    [Fact]
    public async Task H18_OperationRequestResponsesHeadersAndExamplesMatchTheGeneratedDocument()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Unlimited);
        using var client = factory.CreateClient();
        var openApi = await OpenApiContract.FetchAsync(client);
        const string body = """{"serviceCodes":["wash","dry"],"preferredFirstServiceCode":"wash","expectedTotalCents":3980}""";
        var key = Guid.NewGuid();
        using var created = await PostAsync(client, key, body);
        using var replay = await PostAsync(client, key, body);
        using var conflict = await PostAsync(client, key, """{"serviceCodes":["dry"],"expectedTotalCents":2090}""");
        using var invalid = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":[],"expectedTotalCents":1890}""");
        using var unknown = await PostAsync(client, Guid.NewGuid(), """{"serviceCodes":["iron"],"expectedTotalCents":1890}""");
        using var plain = await PostAsync(client, Guid.NewGuid(), body, "text/plain");

        Assert.Empty(OpenApiContract.Problems(openApi, new RequiredOperation(Path, "POST", "CreateBooking"), new RequiredOperation(Detail, "GET", "GetBooking")));
        var operation = OpenApiContract.Read(openApi).Document!.Paths[Path].Operations![HttpMethod.Post];
        Assert.Equal(["Bookings"], operation.Tags!.Select(tag => tag.Name));
        Assert.Equal(["200", "201", "400", "404", "409", "413", "415", "429", "500", "503"], operation.Responses!.Keys.Order(StringComparer.Ordinal));
        var header = Assert.Single(operation.Parameters!);
        Assert.Equal((CreateBookingEndpoint.IdempotencyKeyHeader, ParameterLocation.Header, true), (header.Name, header.In!.Value, header.Required));
        Assert.Equal("uuid", header.Schema!.Format);
        Assert.True(operation.Responses["201"].Headers!["Location"].Required);
        Assert.True(operation.Responses["429"].Headers!["Retry-After"].Required);
        using (var raw = JsonDocument.Parse(openApi))
        {
            var request = raw.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CreateBookingRequest");
            Assert.Equal(["expectedTotalCents", "serviceCodes"], request.GetProperty("required").EnumerateArray().Select(name => name.GetString()).Order(StringComparer.Ordinal));
            Assert.False(request.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(["expectedTotalCents", "preferredFirstServiceCode", "serviceCodes"], request.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.All(request.GetProperty("properties").EnumerateObject(), property => Assert.False(string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString())));
            Assert.Equal((1, 10), (request.GetProperty("properties").GetProperty("serviceCodes").GetProperty("minItems").GetInt32(), request.GetProperty("properties").GetProperty("serviceCodes").GetProperty("maxItems").GetInt32()));
        }

        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.RequestPointer(Path), """{"serviceCodes":["wash"],"expectedTotalCents":1890}""");
        JsonSchemaContract.AssertInvalid(openApi, JsonSchemaContract.RequestPointer(Path), """{"serviceCodes":["wash"],"expectedTotalCents":1890,"walletId":"x"}""");
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.RequestPointer(Path), body);
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "201", method: "post"), await created.Content.ReadAsStringAsync());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "200", method: "post"), await replay.Content.ReadAsStringAsync());
        JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, "201", method: "post"), JsonSchemaContract.Example(openApi, Path, "201", method: "post"));
        foreach (var (status, response) in new[] { ("409", conflict), ("400", invalid), ("404", unknown), ("415", plain) })
        {
            Assert.Equal(status, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            JsonSchemaContract.AssertValid(openApi, JsonSchemaContract.ResponsePointer(Path, status, "application/problem+json", "post"), await response.Content.ReadAsStringAsync());
        }
    }

    // Corpo chunked acima do limite no Kestrel e Content-Type não JSON fazem o binding responder 413/415 sem exceção e
    // sem corpo; a borda HTTP completa com o Problem Details padronizado.
    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge, ApiProblems.PayloadTooLarge)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType, ApiProblems.UnsupportedMediaType)]
    public async Task H19_BareTransportRejectionsFromBindingBecomeStandardProblems(int status, string errorCode)
    {
        await using var host = await IsolatedHttpHost.StartAsync(app => app.MapPost("/bare", () => Results.StatusCode(status)));

        using var response = await host.CreateClient().PostAsync("/bare", new StringContent("{}", Encoding.UTF8, "application/json"));

        await ProblemAssert.HasProblemAsync(response, (HttpStatusCode)status, errorCode, "/bare");
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, Guid key, string body, string mediaType = "application/json", string? traceparent = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        request.Headers.Add(CreateBookingEndpoint.IdempotencyKeyHeader, key.ToString());
        if (traceparent is not null)
        {
            request.Headers.Add("traceparent", traceparent);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static string Items(JsonElement booking) =>
        string.Join(';', booking.GetProperty("items").EnumerateArray().Select(item =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{item.GetProperty("serviceCode").GetString()}|{item.GetProperty("unitPriceCents").GetInt64()}|{item.GetProperty("status").GetString()}|{Machine(item)}")));

    private static string Machine(JsonElement item) =>
        item.GetProperty("machineId").ValueKind == JsonValueKind.Null ? "null"
        : item.GetProperty("machineId").GetGuid() == Guid.Parse(WasherId) ? "washer"
        : item.GetProperty("machineId").GetGuid() == Guid.Parse(DryerId) ? "dryer"
        : "other";

    /// <summary>Spans encerrados das fontes HTTP e de negócio.</summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener listener;
        private readonly ConcurrentQueue<Activity> stopped = new();

        public SpanCapture()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is "Microsoft.AspNetCore" or BookingMetrics.MeterName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(listener);
        }

        public IReadOnlyList<Activity> Stopped => [.. stopped];

        public void Dispose() => listener.Dispose();
    }

    /// <summary>Medições de cicclo_bookings_total do host, com as dimensões recebidas.</summary>
    private sealed class CounterCapture : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentQueue<string> measurements = new();

        public CounterCapture(IMeterFactory meters)
        {
            listener.InstrumentPublished = (instrument, subscription) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meters) && instrument.Name == "cicclo_bookings_total")
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
