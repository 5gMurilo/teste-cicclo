using System.Net;
using System.Net.Sockets;
using Cicclo.Api.Tests.Integration.Operation;
using Cicclo.Api.Tests.Support;
using Cicclo.Api.Wallet.ReadCaching;
using Npgsql;
using Xunit;

namespace Cicclo.Api.Tests.Integration.WalletCaching;

/// <summary>
/// Reset da demo pelo artefato publicado com o cache opcional ligado (CACHE-04/05): a chave da projeção sai somente
/// depois do commit do reset, só ela, e Redis fora do ar não impede nem reverte o reset. PostgreSQL e Redis exclusivos
/// desta classe; prefixo de chave por cenário, sem FLUSHALL/FLUSHDB.
/// </summary>
[Trait("Increment", "E08")]
[Trait("Task", "T2")]
public sealed class CachedWalletResetTests(ResetDemoDatabaseFixture postgres, PublishedApiFixture api, RedisFixture redis)
    : IClassFixture<ResetDemoDatabaseFixture>, IClassFixture<PublishedApiFixture>, IClassFixture<RedisFixture>
{
    private const string Completed = "Reset demo concluído: dados iniciais restaurados pelo seed.sql.";
    private const string Stale = """{"balanceCents":1020,"currency":"BRL","updatedAt":"2026-10-21T12:00:00.0000000Z"}""";

    private readonly string prefix = $"t{Guid.NewGuid():N}:";

    private string Key => prefix + RedisWalletReadCache.Key;

    [Fact]
    public async Task K12_AuthorizedResetRemovesOnlyTheProjectionKeyAndARefusedResetKeepsIt()
    {
        var database = await DirtyDatabaseAsync();
        await redis.Database.StringSetAsync(Key, Stale, TimeSpan.FromMinutes(5));
        await redis.Database.StringSetAsync(prefix + "wallet:demo:v2", "outra", TimeSpan.FromMinutes(5));
        var caching = Caching(redis.ConnectionString);

        var refused = await api.ResetAsync(database.ConnectionString, confirmation: null, extra: caching);

        Assert.Equal(2, refused.ExitCode);
        Assert.Equal(Stale, (string?)await redis.Database.StringGetAsync(Key));

        var reset = await api.ResetAsync(database.ConnectionString, Target(database), extra: caching);

        Assert.True(reset.ExitCode == 0, reset.Output);
        Assert.Contains(Completed, reset.Output, StringComparison.Ordinal);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
        Assert.False(await redis.Database.KeyExistsAsync(Key));
        Assert.Equal("outra", (string?)await redis.Database.StringGetAsync(prefix + "wallet:demo:v2"));
    }

    [Fact]
    public async Task K13_RedisDownDoesNotBlockOrRevertTheResetAndLeaksNoSecret()
    {
        var database = await DirtyDatabaseAsync();
        const string Secret = "nao-vazar-redis-reset";
        var endpoint = $"127.0.0.1:{ClosedPort()}";

        var reset = await api.ResetAsync(database.ConnectionString, Target(database), extra: Caching($"{endpoint},password={Secret}"));

        Assert.True(reset.ExitCode == 0, reset.Output);
        Assert.Contains("Cache da wallet degradado: invalidate terminou em", reset.Output, StringComparison.Ordinal);
        Assert.Contains(Completed, reset.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, reset.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, reset.Output, StringComparison.Ordinal);
        Assert.Equal(DemoData.InitialSnapshot, await DemoData.SnapshotAsync(database));
    }

    private Dictionary<string, string?> Caching(string endpoint) => new()
    {
        ["Caching__Enabled"] = "true",
        ["Caching__Redis"] = endpoint,
        ["Caching__KeyPrefix"] = prefix,
    };

    /// <summary>Banco `cicclo` novo com uma compra real wash+dry (saldo 10.20).</summary>
    private async Task<TestDatabase> DirtyDatabaseAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        await BookingApi.PurchaseAsync(database, TimeProvider.System, ["wash", "dry"]);
        Assert.Equal(10.20m, await database.ScalarAsync<decimal>("SELECT balance FROM wallets"));
        return database;
    }

    private static string Target(TestDatabase database)
    {
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        return $"{builder.Host}:{builder.Port}/{builder.Database}";
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
