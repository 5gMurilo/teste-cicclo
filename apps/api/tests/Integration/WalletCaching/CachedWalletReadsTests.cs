using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cicclo.Api.Bookings.CreateBooking;
using Cicclo.Api.Tests.Support;
using Cicclo.Api.Wallet.GetBalance;
using Cicclo.Api.Wallet.ReadCaching;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;
using static Cicclo.Api.Tests.Integration.Laundry.LaundryScenario;

namespace Cicclo.Api.Tests.Integration.WalletCaching;

/// <summary>
/// GET wallet com cache-aside eventual e invalidação pós-commit (CACHE-01..08) sobre PostgreSQL 18.1 e Redis reais,
/// ambos exclusivos (o Redis é desta classe; cada cenário usa um prefixo próprio, sem FLUSHALL/FLUSHDB). O banco é a
/// prova financeira: asserções de estado imediato leem o PostgreSQL ou o resultado do comando, nunca um GET cacheado.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Increment", "E08")]
[Trait("Task", "T2")]
public sealed class CachedWalletReadsTests(PostgresFixture postgres, RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string WalletPath = "/api/v1/wallet";
    private const string Wash = """{"serviceCodes":["wash"],"expectedTotalCents":1890}""";
    private const string LedgerSql = "SELECT kind || '|' || amount || '|' || balance_after FROM wallet_transactions ORDER BY created_at, kind";

    private static readonly Dictionary<string, string> Demo = new()
    {
        ["Laundry:DemoMode"] = "true",
        ["Laundry:QueueWorkerIntervalSeconds"] = "3600",
        ["RateLimiting:Reads:PermitLimit"] = "1000",
        ["RateLimiting:BookingCreate:PermitLimit"] = "1000",
        ["RateLimiting:BookingReads:PermitLimit"] = "1000",
        ["RateLimiting:ItemCommands:PermitLimit"] = "1000",
        ["RateLimiting:ItemCancel:PermitLimit"] = "1000",
        ["RateLimiting:IssueReports:PermitLimit"] = "1000",
    };

    private readonly string prefix = $"t{Guid.NewGuid():N}:";

    private string Key => prefix + RedisWalletReadCache.Key;

    [Fact]
    public async Task K01_CacheOffReadsPostgresEveryTimeWithoutAnyRedisConnection()
    {
        var database = await ReadyAsync(postgres);
        var connections = await redis.TotalConnectionsReceivedAsync();
        await using var factory = BookingApi.Create(database, Clock(), Settings(enabled: false));
        using var client = factory.CreateClient();

        Assert.Null(factory.Services.GetService<IWalletReadCache>());
        Assert.Equal(5000L, await BalanceAsync(client));
        await database.ExecuteAsync("UPDATE wallets SET balance = 31.10");
        Assert.Equal(3110L, await BalanceAsync(client));
        using var created = await BuyAsync(client, Guid.NewGuid(), Wash);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(1220L, (await BookingApi.JsonAsync(created)).GetProperty("remainingBalanceCents").GetInt64());
        Assert.Equal(1220L, await BalanceAsync(client));

        Assert.Equal(connections, await redis.TotalConnectionsReceivedAsync());
        Assert.Empty(redis.Server.Keys(pattern: prefix + "*"));
    }

    [Fact]
    public async Task K02_MissReadsPostgresAndStoresTheProjectionWithTheServerTtl()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Settings());
        using var metrics = new CacheMetrics(factory.Services.GetRequiredService<IMeterFactory>());
        using var client = factory.CreateClient();

        Assert.Equal(5000L, await BalanceAsync(client));

        using (var stored = JsonDocument.Parse((string)(await redis.Database.StringGetAsync(Key))!))
        {
            Assert.Equal(["balanceCents", "currency", "updatedAt"], stored.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(5000L, stored.RootElement.GetProperty("balanceCents").GetInt64());
        }

        var ttl = await redis.Database.KeyTimeToLiveAsync(Key);
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(28), TimeSpan.FromSeconds(30));
        Assert.Equal(5000L, await BalanceAsync(client));
        Assert.Equal(["read|miss", "write|ok", "read|hit"], metrics.Measurements);
    }

    [Fact]
    public async Task K03_HitServesTheProjectionButNeverDecidesThePurchaseBalance()
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Settings());
        using var client = factory.CreateClient();
        Assert.Equal(5000L, await BalanceAsync(client));

        // Cache diz 5000, PostgreSQL diz 10.00: o GET é eventual, mas a compra decide pelo banco e recusa.
        await database.ExecuteAsync("UPDATE wallets SET balance = 10.00");
        Assert.Equal(5000L, await BalanceAsync(client));
        using (var refused = await BuyAsync(client, Guid.NewGuid(), Wash))
        {
            await ProblemAssert.HasProblemAsync(refused, HttpStatusCode.Conflict, "WALLET_INSUFFICIENT_BALANCE", "/api/v1/bookings");
        }

        Assert.Empty(await database.RowsAsync(LedgerSql));
        Assert.True(await redis.Database.KeyExistsAsync(Key), "recusa revertida não invalida como se o saldo tivesse mudado");
        Assert.Equal(5000L, await BalanceAsync(client));

        // Cache diz 0, PostgreSQL diz 50.00: a compra é aceita e o saldo restante vem do banco.
        await database.ExecuteAsync("UPDATE wallets SET balance = 50.00");
        await redis.Database.StringSetAsync(Key, """{"balanceCents":0,"currency":"BRL","updatedAt":"2026-10-21T12:00:00.0000000Z"}""", TimeSpan.FromSeconds(30));
        Assert.Equal(0L, await BalanceAsync(client));
        using var created = await BuyAsync(client, Guid.NewGuid(), Wash);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(3110L, (await BookingApi.JsonAsync(created)).GetProperty("remainingBalanceCents").GetInt64());
        Assert.Equal(["debit|18.90|31.10"], await database.RowsAsync(LedgerSql));
        Assert.Equal(3110L, await BalanceAsync(client));
    }

    [Fact]
    public async Task K04_PurchaseInvalidatesOnceAfterTheCommitAndReplayDoesNot()
    {
        var database = await ReadyAsync(postgres);
        var probe = new ProbeCache(database);
        await using var factory = Host(database, probe);
        using var client = factory.CreateClient();
        Assert.Equal(5000L, await BalanceAsync(client));
        var key = Guid.NewGuid();

        using (var created = await BuyAsync(client, key, Wash))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        Assert.Equal([31.10m], probe.TakeInvalidations());
        Assert.False(await redis.Database.KeyExistsAsync(Key));
        Assert.Equal(3110L, await BalanceAsync(client));

        using (var replay = await BuyAsync(client, key, Wash))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(3110L, (await BookingApi.JsonAsync(replay)).GetProperty("remainingBalanceCents").GetInt64());
        }

        Assert.Empty(probe.TakeInvalidations());
        Assert.True(await redis.Database.KeyExistsAsync(Key));
        Assert.Equal(["debit|18.90|31.10"], await database.RowsAsync(LedgerSql));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("breakdown")]
    [InlineData("incident")]
    public async Task K05_RefundWritesInvalidateOnceAfterTheCommitAndRepetitionsDoNot(string write)
    {
        var database = await ReadyAsync(postgres);
        var probe = new ProbeCache(database);
        await using var factory = Host(database, probe);
        using var client = factory.CreateClient();
        var (booking, item) = await BuyWashAsync(client);
        string? command = null;
        if (write == "cancel")
        {
            command = $"/api/v1/bookings/{booking}/items/{item}/cancel";
        }
        else
        {
            await AssertStatusAsync(client.PostAsync($"/api/v1/bookings/{booking}/items/{item}/confirm", null), HttpStatusCode.OK);
            command = $"/api/v1/simulation/machines/{WasherId}/breakdown";
            if (write == "incident")
            {
                using var reported = await AssertStatusAsync(client.PostAsync($"/api/v1/bookings/{booking}/items/{item}/report-issue", null), HttpStatusCode.Accepted);
                command = $"/api/v1/simulation/incidents/{(await BookingApi.JsonAsync(reported)).GetProperty("incidentId").GetGuid()}/confirm";
            }
        }

        Assert.Equal(3110L, await BalanceAsync(client));
        probe.TakeInvalidations();

        using (await AssertStatusAsync(client.PostAsync(command, null), HttpStatusCode.OK))
        {
            Assert.Equal([50.00m], probe.TakeInvalidations());
        }

        Assert.False(await redis.Database.KeyExistsAsync(Key));
        Assert.Equal(5000L, await BalanceAsync(client));

        using (await AssertStatusAsync(client.PostAsync(command, null), write == "breakdown" ? HttpStatusCode.Conflict : HttpStatusCode.OK))
        {
            Assert.Empty(probe.TakeInvalidations());
        }

        Assert.True(await redis.Database.KeyExistsAsync(Key));
        Assert.Equal(["debit|18.90|31.10", "refund|18.90|50.00"], await database.RowsAsync(LedgerSql));
    }

    [Fact]
    public async Task K06_RejectedOrMoneylessWritesNeverInvalidate()
    {
        var database = await ReadyAsync(postgres);
        var probe = new ProbeCache(database);
        await using var factory = Host(database, probe);
        using var client = factory.CreateClient();
        Assert.Equal(5000L, await BalanceAsync(client));

        // Falha da ocupação sintética (sem item, sem refund), preço divergente e saldo insuficiente: nada muda na wallet.
        (await AssertStatusAsync(client.PostAsync($"/api/v1/simulation/machines/{DryerId}/occupy", null), HttpStatusCode.OK)).Dispose();
        (await AssertStatusAsync(client.PostAsync($"/api/v1/simulation/machines/{DryerId}/breakdown", null), HttpStatusCode.OK)).Dispose();
        (await AssertStatusAsync(BuyAsync(client, Guid.NewGuid(), """{"serviceCodes":["wash"],"expectedTotalCents":1000}"""), HttpStatusCode.Conflict)).Dispose();
        await database.ExecuteAsync("UPDATE wallets SET balance = 10.00");
        (await AssertStatusAsync(BuyAsync(client, Guid.NewGuid(), Wash), HttpStatusCode.Conflict)).Dispose();

        Assert.Empty(probe.TakeInvalidations());
        Assert.True(await redis.Database.KeyExistsAsync(Key));
        Assert.Empty(await database.RowsAsync(LedgerSql));
    }

    [Fact]
    public async Task K07_RedisDownKeepsHealthReadsAndMoneyOnPostgresAndLogsDegradationWithoutSecrets()
    {
        var database = await ReadyAsync(postgres);
        const string Secret = "nao-vazar-redis-segredo";
        var logs = new CapturingLoggerProvider();
        var endpoint = $"127.0.0.1:{ClosedPort()}";
        var settings = Settings(redisEndpoint: $"{endpoint},password={Secret}");
        await using var factory = BookingApi.Create(database, Clock(), settings).WithWebHostBuilder(host => host.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var metrics = new CacheMetrics(factory.Services.GetRequiredService<IMeterFactory>());
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var health = await client.GetAsync(path);
            Assert.Equal((HttpStatusCode.OK, "Healthy"), (health.StatusCode, await health.Content.ReadAsStringAsync()));
        }

        Assert.Equal(5000L, await BalanceAsync(client));
        var (booking, item) = await BuyWashAsync(client);
        Assert.Equal(3110L, await BalanceAsync(client));
        using (var cancelled = await AssertStatusAsync(client.PostAsync($"/api/v1/bookings/{booking}/items/{item}/cancel", null), HttpStatusCode.OK))
        {
            Assert.Equal(5000L, (await BookingApi.JsonAsync(cancelled)).GetProperty("remainingBalanceCents").GetInt64());
        }

        Assert.Equal(5000L, await BalanceAsync(client));
        Assert.Equal(["debit|18.90|31.10", "refund|18.90|50.00"], await database.RowsAsync(LedgerSql));
        Assert.Equal(["read", "write", "invalidate"], metrics.Measurements.Select(entry => entry.Split('|')[0]).Distinct());
        Assert.All(metrics.Measurements, entry => Assert.Contains(entry.Split('|')[1], new[] { "error", "timeout" }));
        var degraded = logs.Entries.Where(entry => entry.EventId.Id == 800).ToList();
        Assert.NotEmpty(degraded);
        Assert.All(degraded, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(Secret, StringComparison.Ordinal) || entry.Message.Contains(endpoint, StringComparison.Ordinal));
    }

    [Fact]
    public async Task K08_FailingInvalidationNeitherChangesTheConfirmedResultNorRepeatsTheDebit()
    {
        var database = await ReadyAsync(postgres);
        var probe = new ProbeCache(database) { FailInvalidation = true };
        var logs = new CapturingLoggerProvider();
        await using var factory = Host(database, probe).WithWebHostBuilder(host => host.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient();
        Assert.Equal(5000L, await BalanceAsync(client));

        using var created = await AssertStatusAsync(BuyAsync(client, Guid.NewGuid(), Wash), HttpStatusCode.Created);

        Assert.Equal(3110L, (await BookingApi.JsonAsync(created)).GetProperty("remainingBalanceCents").GetInt64());
        Assert.Equal([31.10m], probe.TakeInvalidations());
        Assert.Equal(["debit|18.90|31.10"], await database.RowsAsync(LedgerSql));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM service_requests"));
        var failure = Assert.Single(logs.Entries, entry => entry.EventId.Id == 801);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Contains(nameof(InvalidOperationException), failure.Message, StringComparison.Ordinal);

        // Sem invalidação, o GET continua eventual: a projeção antiga só sai pelo TTL.
        Assert.Equal(5000L, await BalanceAsync(client));
    }

    [Fact]
    public async Task K09_LateRepopulationServesTheOldBalanceUntilTheTtlCountedFromTheLateWrite()
    {
        var database = await ReadyAsync(postgres);
        var probe = new ProbeCache(database);
        await using var factory = Host(database, probe, timeToLiveSeconds: 2);
        using var client = factory.CreateClient();
        var gate = probe.HoldNextWrite();

        // Leitor: miss, lê 5000 do PostgreSQL e para antes de gravar.
        var reading = BalanceAsync(client);
        await probe.WriteReached.WaitAsync(SignalTimeout);

        // Escritor: compra confirmada e invalidada depois do commit, com a chave ainda vazia.
        using (await AssertStatusAsync(BuyAsync(client, Guid.NewGuid(), Wash), HttpStatusCode.Created))
        {
            Assert.Equal([31.10m], probe.TakeInvalidations());
        }

        Assert.False(await redis.Database.KeyExistsAsync(Key));

        // A gravação tardia repõe o saldo antigo: o GET seguinte é antigo, sem read-your-writes.
        gate.SetResult();
        Assert.Equal(5000L, await reading);
        var ttl = await redis.Database.KeyTimeToLiveAsync(Key);
        Assert.InRange(ttl!.Value, TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(2));
        Assert.Equal(5000L, await BalanceAsync(client));
        Assert.Equal(31.10m, await database.ScalarAsync<decimal>("SELECT balance FROM wallets"));

        var elapsed = Stopwatch.StartNew();
        var balances = new List<long>();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(6) && (balances.Count == 0 || balances[^1] != 3110L))
        {
            balances.Add(await BalanceAsync(client));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(3110L, balances[^1]);
        Assert.All(balances, balance => Assert.Contains(balance, new[] { 5000L, 3110L }));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(500), $"a projeção antiga expirou cedo demais ({elapsed.Elapsed})");
    }

    [Fact]
    public async Task K10_DatabaseDownKeepsReadinessAndCommandsAt503WhileAValidHitIsAnEventualSnapshot()
    {
        var database = await ReadyAsync(postgres);
        await using (var up = BookingApi.Create(database, Clock(), Settings()))
        {
            using var client = up.CreateClient();
            Assert.Equal(5000L, await BalanceAsync(client));
        }

        await using var down = ApiFactory.Create(ApiFactory.UnreachableDatabase, Settings());
        using var api = down.CreateClient();

        Assert.Equal(5000L, await BalanceAsync(api));
        using (var ready = await api.GetAsync("/health/ready"))
        {
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), (ready.StatusCode, await ready.Content.ReadAsStringAsync()));
        }

        using (var purchase = await BuyAsync(api, Guid.NewGuid(), Wash))
        {
            await ProblemAssert.HasProblemAsync(purchase, HttpStatusCode.ServiceUnavailable, "DATABASE_UNAVAILABLE", "/api/v1/bookings");
        }

        await redis.Database.KeyDeleteAsync(Key);
        using var miss = await api.GetAsync(WalletPath);
        await ProblemAssert.HasProblemAsync(miss, HttpStatusCode.ServiceUnavailable, "DATABASE_UNAVAILABLE", WalletPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task K11_TwentyReplaysAndThreeWashesEndWithTheSameMoneyWithCacheOnOrOff(bool cacheOn)
    {
        var database = await ReadyAsync(postgres);
        await using var factory = BookingApi.Create(database, Clock(), Settings(enabled: cacheOn));
        using var client = factory.CreateClient();
        Assert.Equal(5000L, await BalanceAsync(client));
        var key = Guid.NewGuid();

        var replays = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SummaryAsync(BuyAsync(client, key, Wash))));
        Assert.Equal(1, replays.Count(entry => entry.StartsWith("201|", StringComparison.Ordinal)));
        Assert.Equal(19, replays.Count(entry => entry.StartsWith("200|", StringComparison.Ordinal)));
        Assert.Single(replays.Select(entry => entry[4..]).Distinct());
        Assert.Equal(3110L, await BalanceAsync(client));

        var washes = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => SummaryAsync(BuyAsync(client, Guid.NewGuid(), Wash))));
        Assert.Equal(["201", "409", "409"], washes.Select(entry => entry[..3]).Order(StringComparer.Ordinal));

        Assert.Equal(1220L, await BalanceAsync(client));
        Assert.Equal(["debit|18.90|31.10", "debit|18.90|12.20"], await database.RowsAsync(LedgerSql));
        Assert.Equal(["12.20|2|2"], await database.RowsAsync(
            "SELECT (SELECT balance FROM wallets) || '|' || (SELECT count(*) FROM service_requests) || '|' || (SELECT count(*) FROM service_request_items)"));
        Assert.Equal(cacheOn, await redis.Database.KeyExistsAsync(Key));
    }

    private Dictionary<string, string> Settings(bool enabled = true, string? redisEndpoint = null, int timeToLiveSeconds = 30) =>
        new(Demo)
        {
            ["Caching:Enabled"] = enabled ? "true" : "false",
            ["Caching:Redis"] = redisEndpoint ?? redis.ConnectionString,
            ["Caching:KeyPrefix"] = prefix,
            ["Caching:TimeToLiveSeconds"] = timeToLiveSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>Host com cache ligado em que a porta registrada é a sonda em volta do adaptador Redis real.</summary>
    private WebApplicationFactory<Program> Host(TestDatabase database, ProbeCache probe, int timeToLiveSeconds = 30) =>
        BookingApi.Create(database, Clock(), Settings(timeToLiveSeconds: timeToLiveSeconds)).WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton<RedisWalletReadCache>();
            services.AddSingleton<IWalletReadCache>(provider => probe.Wrap(provider.GetRequiredService<RedisWalletReadCache>()));
        }));

    private static async Task<long> BalanceAsync(HttpClient client)
    {
        using var response = await AssertStatusAsync(client.GetAsync(WalletPath), HttpStatusCode.OK);
        return (await BookingApi.JsonAsync(response)).GetProperty("balanceCents").GetInt64();
    }

    private static async Task<HttpResponseMessage> BuyAsync(HttpClient client, Guid key, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(CreateBookingEndpoint.IdempotencyKeyHeader, key.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<(Guid Booking, Guid Item)> BuyWashAsync(HttpClient client)
    {
        using var response = await AssertStatusAsync(BuyAsync(client, Guid.NewGuid(), Wash), HttpStatusCode.Created);
        var booking = await BookingApi.JsonAsync(response);
        return (booking.GetProperty("bookingId").GetGuid(), booking.GetProperty("items")[0].GetProperty("itemId").GetGuid());
    }

    /// <summary>status|bookingId|remainingBalanceCents (ou o código do problema).</summary>
    private static async Task<string> SummaryAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        var body = await BookingApi.JsonAsync(response);
        var detail = body.TryGetProperty("bookingId", out var id)
            ? $"{id.GetGuid()}|{body.GetProperty("remainingBalanceCents").GetInt64()}"
            : body.GetProperty("errorCode").GetString();
        return $"{(int)response.StatusCode}|{detail}";
    }

    private static async Task<HttpResponseMessage> AssertStatusAsync(Task<HttpResponseMessage> call, HttpStatusCode status)
    {
        var response = await call;
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response;
    }

    /// <summary>Porta TCP local sem listener: a conexão é recusada, simulando Redis fora do ar.</summary>
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Sonda da porta em volta do adaptador real: na invalidação, registra o saldo já confirmado no PostgreSQL (lido
    /// por outra conexão, sem lock), o que distingue invalidar depois do commit de invalidar antes dele; pode segurar a
    /// próxima gravação (corrida de repopulação) ou falhar a invalidação.
    /// </summary>
    private sealed class ProbeCache(TestDatabase database) : IWalletReadCache
    {
        private readonly ConcurrentQueue<decimal> invalidations = new();
        private readonly TaskCompletionSource writeReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IWalletReadCache? inner;
        private TaskCompletionSource? heldWrite;

        public bool FailInvalidation { get; init; }

        public Task WriteReached => writeReached.Task;

        public IWalletReadCache Wrap(IWalletReadCache cache)
        {
            inner = cache;
            return this;
        }

        public TaskCompletionSource HoldNextWrite() => heldWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<decimal> TakeInvalidations()
        {
            var taken = new List<decimal>();
            while (invalidations.TryDequeue(out var balance))
            {
                taken.Add(balance);
            }

            return taken;
        }

        public Task<WalletBalance?> ReadAsync(CancellationToken cancellationToken) => Inner.ReadAsync(cancellationToken);

        public async Task<bool> WriteAsync(WalletBalance balance, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref heldWrite, null) is { } held)
            {
                writeReached.TrySetResult();
                await held.Task.WaitAsync(SignalTimeout, cancellationToken);
            }

            return await Inner.WriteAsync(balance, cancellationToken);
        }

        public async Task<bool> InvalidateAsync(CancellationToken cancellationToken)
        {
            invalidations.Enqueue(await database.ScalarAsync<decimal>("SELECT balance FROM wallets"));
            return FailInvalidation
                ? throw new InvalidOperationException("falha simulada da invalidação")
                : await Inner.InvalidateAsync(cancellationToken);
        }

        private IWalletReadCache Inner => inner ?? throw new InvalidOperationException("Sonda sem adaptador.");
    }

    /// <summary>operation|outcome de cicclo_cache_operations_total medidos pela fábrica de meters do host do cenário.</summary>
    private sealed class CacheMetrics : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentQueue<string> measurements = new();

        public CacheMetrics(IMeterFactory meters)
        {
            listener.InstrumentPublished = (instrument, subscription) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meters) && instrument.Name == "cicclo_cache_operations_total")
                {
                    subscription.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
                measurements.Enqueue(string.Join('|', tags.ToArray().Select(tag => tag.Value?.ToString()))));
            listener.Start();
        }

        public List<string> Measurements => [.. measurements];

        public void Dispose() => listener.Dispose();
    }
}
