using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cicclo.Api.Tests.Support;
using Cicclo.Api.Wallet;
using Cicclo.Api.Wallet.GetBalance;
using Cicclo.Api.Wallet.ReadCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Cicclo.Api.Tests.Integration.WalletCaching;

/// <summary>
/// Adaptador Redis real da projeção de saldo (CACHE-02/05/08, D07/D08), ainda não ligado ao GET. Cada cenário usa um
/// prefixo exclusivo no Redis do teste; o TTL é o relógio do servidor Redis, observado com polling limitado.
/// </summary>
[Collection(RedisCollection.Name)]
[Trait("Increment", "E08")]
[Trait("Task", "T1")]
public sealed class RedisWalletReadCacheTests(RedisFixture redis)
{
    private static readonly WalletBalance Projection = new(50.00m, "BRL", new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567));

    private static readonly string[] Outcomes = ["hit", "miss", "invalid", "ok", "error", "timeout"];

    private readonly string prefix = $"t{Guid.NewGuid():N}:";

    [Fact]
    public async Task C01_RoundtripStoresOnlyTheVersionedBalanceProjectionUnderTheIsolatedKey()
    {
        using var metrics = new MetricCapture();
        await using var cache = Cache(metrics.Meters);

        Assert.True(await cache.WriteAsync(Projection, CancellationToken.None));
        Assert.Equal(Projection, await cache.ReadAsync(CancellationToken.None));

        Assert.Equal([prefix + "wallet:demo:v1"], redis.Server.Keys(pattern: prefix + "*").Select(key => (string)key!));
        Assert.Equal(RedisType.String, await redis.Database.KeyTypeAsync(prefix + "wallet:demo:v1"));
        using var stored = JsonDocument.Parse((string)(await redis.Database.StringGetAsync(prefix + "wallet:demo:v1"))!);
        Assert.Equal(["balanceCents", "currency", "updatedAt"], stored.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(5000, stored.RootElement.GetProperty("balanceCents").GetInt64());
        Assert.Equal("BRL", stored.RootElement.GetProperty("currency").GetString());
        Assert.Equal("2026-10-21T12:00:00.1234567Z", stored.RootElement.GetProperty("updatedAt").GetString());
        Assert.Equal(["write|ok", "read|hit"], metrics.Measurements);
    }

    [Fact]
    public async Task C02_MissingEntryIsAMissWithoutCreatingAnyKey()
    {
        using var metrics = new MetricCapture();
        await using var cache = Cache(metrics.Meters);

        Assert.Null(await cache.ReadAsync(CancellationToken.None));

        Assert.Empty(redis.Server.Keys(pattern: prefix + "*"));
        Assert.Equal(["read|miss"], metrics.Measurements);
    }

    [Theory]
    [InlineData(null, 30_000)]
    [InlineData(7, 7_000)]
    public async Task C03_TheConfiguredTtlIsSetOnTheRedisServerAtWriteTime(int? configuredSeconds, int expectedMilliseconds)
    {
        await using var cache = configuredSeconds is { } seconds ? Cache(timeToLiveSeconds: seconds) : Cache();

        Assert.True(await cache.WriteAsync(Projection, CancellationToken.None));

        var ttl = await redis.Database.KeyTimeToLiveAsync(prefix + "wallet:demo:v1");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value.TotalMilliseconds, expectedMilliseconds - 2_000, expectedMilliseconds);
    }

    [Fact]
    public async Task C04_TheEntryReallyExpiresOnTheServerAfterAReducedTtl()
    {
        using var metrics = new MetricCapture();
        await using var cache = Cache(metrics.Meters, timeToLiveSeconds: 1);
        Assert.True(await cache.WriteAsync(Projection, CancellationToken.None));
        Assert.Equal(Projection, await cache.ReadAsync(CancellationToken.None));

        var expired = await PollAsync(async () => !await redis.Database.KeyExistsAsync(prefix + "wallet:demo:v1"), TimeSpan.FromSeconds(5));

        Assert.True(expired, "A chave com TTL de 1 s deveria expirar no servidor Redis em até 5 s.");
        Assert.Null(await cache.ReadAsync(CancellationToken.None));
        Assert.Equal(["write|ok", "read|hit", "read|miss"], metrics.Measurements);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"balanceCents\":-100,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}")]
    [InlineData("{\"balanceCents\":5000,\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\",\"queue\":[]}")]
    [InlineData("{\"balanceCents\":\"5000\",\"currency\":\"BRL\",\"updatedAt\":\"2026-10-21T12:00:00.0000000Z\"}")]
    public async Task C05_CorruptNegativeOrInvalidEntriesDegradeToAMissWithoutThrowing(string raw)
    {
        using var metrics = new MetricCapture();
        await using var cache = Cache(metrics.Meters);
        await redis.Database.StringSetAsync(prefix + "wallet:demo:v1", raw);

        Assert.Null(await cache.ReadAsync(CancellationToken.None));

        Assert.Equal(["read|invalid"], metrics.Measurements);
    }

    [Fact]
    public async Task C06_RedisDownIsAFiniteMissAndALostConnectionRecoversWithoutExceptions()
    {
        using var downMetrics = new MetricCapture();
        await using (var down = Cache(downMetrics.Meters, endpoint: $"127.0.0.1:{ClosedPort()}", timeoutMilliseconds: 300))
        {
            var elapsed = Stopwatch.StartNew();
            Assert.Null(await down.ReadAsync(CancellationToken.None));
            Assert.False(await down.WriteAsync(Projection, CancellationToken.None));
            Assert.False(await down.InvalidateAsync(CancellationToken.None));
            Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        }

        Assert.Equal(["read", "write", "invalidate"], downMetrics.Measurements.Select(entry => entry.Split('|')[0]));
        Assert.All(downMetrics.Measurements, entry => Assert.Contains(entry.Split('|')[1], new[] { "error", "timeout" }));

        await using var cache = Cache();
        Assert.True(await cache.WriteAsync(Projection, CancellationToken.None));
        var killed = (long)await redis.Server.ExecuteAsync("CLIENT", "KILL", "TYPE", "normal", "SKIPME", "yes");
        Assert.InRange(killed, 1, long.MaxValue);

        var recovered = await PollAsync(async () => await cache.ReadAsync(CancellationToken.None) == Projection, TimeSpan.FromSeconds(10));

        Assert.True(recovered, "O multiplexer deveria reconectar e voltar a servir a projeção.");
    }

    [Fact]
    public async Task C07_TimeoutsAreBoundedAndTheMultiplexerIsReusedAcrossCalls()
    {
        using var metrics = new MetricCapture();
        await using var cache = Cache(metrics.Meters, timeoutMilliseconds: 200);
        Assert.Null(await cache.ReadAsync(CancellationToken.None));
        var connectionsBefore = await redis.TotalConnectionsReceivedAsync();

        for (var round = 0; round < 10; round++)
        {
            Assert.True(await cache.WriteAsync(Projection, CancellationToken.None));
            Assert.Equal(Projection, await cache.ReadAsync(CancellationToken.None));
            Assert.True(await cache.InvalidateAsync(CancellationToken.None));
        }

        Assert.Equal(connectionsBefore, await redis.TotalConnectionsReceivedAsync());

        // O orçamento de 200 ms precisa valer mesmo quando o timeout do próprio cliente só é detectado no heartbeat (~1 s).
        await redis.Server.ExecuteAsync("CLIENT", "PAUSE", "2000", "ALL");
        var elapsed = Stopwatch.StartNew();
        Assert.Null(await cache.ReadAsync(CancellationToken.None));
        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromMilliseconds(700));
        Assert.Equal("read|timeout", metrics.Measurements[^1]);

        Assert.True(await PollAsync(async () => await cache.WriteAsync(Projection, CancellationToken.None), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task C08_InvalidationRemovesOnlyTheProjectionKeyAndMetricsCarryOnlyOperationAndOutcome()
    {
        using var metrics = new MetricCapture();
        var neighbour = $"t{Guid.NewGuid():N}:";
        await using var cache = Cache(metrics.Meters);
        Assert.True(await cache.WriteAsync(Projection, CancellationToken.None));
        string[] siblings = [prefix + "wallet:demo:v2", prefix + "wallet:other", neighbour + "wallet:demo:v1", "wallet:demo:v1:" + prefix];
        foreach (var sibling in siblings)
        {
            await redis.Database.StringSetAsync(sibling, "x");
        }

        Assert.True(await cache.InvalidateAsync(CancellationToken.None));
        Assert.Null(await cache.ReadAsync(CancellationToken.None));

        Assert.False(await redis.Database.KeyExistsAsync(prefix + "wallet:demo:v1"));
        Assert.All(siblings, sibling => Assert.True(redis.Database.KeyExists(sibling), sibling));
        Assert.Equal(["write|ok", "invalidate|ok", "read|miss"], metrics.Measurements);
        Assert.All(metrics.Tags, tags =>
        {
            Assert.Equal(["operation", "outcome"], tags.Select(tag => tag.Key));
            Assert.Contains(tags[0].Value, new[] { "read", "write", "invalidate" });
            Assert.Contains(tags[1].Value, Outcomes);
            Assert.DoesNotContain(tags, tag => tag.Value!.Contains(DemoWallet.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal(["cicclo_cache_operations_total"], metrics.Instruments.Distinct());
    }

    private RedisWalletReadCache Cache(IMeterFactory? meters = null, string? endpoint = null, int timeToLiveSeconds = 30, int timeoutMilliseconds = 500) =>
        new(
            Options.Create(new WalletCacheOptions
            {
                Enabled = true,
                Redis = endpoint ?? redis.ConnectionString,
                TimeToLiveSeconds = timeToLiveSeconds,
                OperationTimeoutMilliseconds = timeoutMilliseconds,
                KeyPrefix = prefix,
            }),
            meters ?? new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
            NullLogger<RedisWalletReadCache>.Instance);

    /// <summary>Porta TCP local sem listener: a conexão é recusada, simulando Redis fora do ar.</summary>
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Polling limitado sobre o relógio real do servidor Redis (não há como avançar o TTL pelo .NET).</summary>
    private static async Task<bool> PollAsync(Func<Task<bool>> condition, TimeSpan limit)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < limit)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return await condition();
    }

    /// <summary>Medições do contador de cache criadas por uma fábrica de meters exclusiva do cenário.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        private readonly MeterListener listener = new();
        private readonly List<(string Instrument, KeyValuePair<string, string?>[] Tags)> measurements = [];

        public MetricCapture()
        {
            Meters = services.GetRequiredService<IMeterFactory>();
            listener.InstrumentPublished = (instrument, subscription) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, Meters))
                {
                    subscription.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                lock (measurements)
                {
                    measurements.Add((instrument.Name, tags.ToArray().Select(tag => new KeyValuePair<string, string?>(tag.Key, tag.Value?.ToString())).ToArray()));
                }
            });
            listener.Start();
        }

        public IMeterFactory Meters { get; }

        public List<string> Measurements => [.. Snapshot().Select(entry => string.Join('|', entry.Tags.Select(tag => tag.Value)))];

        public List<KeyValuePair<string, string?>[]> Tags => [.. Snapshot().Select(entry => entry.Tags)];

        public List<string> Instruments => [.. Snapshot().Select(entry => entry.Instrument)];

        public void Dispose()
        {
            listener.Dispose();
            services.Dispose();
        }

        private List<(string Instrument, KeyValuePair<string, string?>[] Tags)> Snapshot()
        {
            lock (measurements)
            {
                return [.. measurements];
            }
        }
    }
}
