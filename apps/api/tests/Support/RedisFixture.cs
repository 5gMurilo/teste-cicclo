using System.Globalization;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Um Redis descartável e exclusivo desta execução (nome único, sem volume). Cada cenário usa um prefixo de chaves
/// próprio; nada executa FLUSHALL/FLUSHDB. A conexão administrativa é única e serve só para inspecionar/perturbar.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    /// <summary>Tag e digest do índice multiplataforma conferidos em 2026-10-21 (docker buildx imagetools inspect).</summary>
    public const string Image = "redis:8.2.10-bookworm@sha256:e5f23377739f95a2f530b9c99f93accb4f634577f09d00a7d8e0638f2fdf69e0";

    private readonly RedisContainer container = new RedisBuilder(Image)
        .WithName($"cicclo-api-tests-redis-{Guid.NewGuid():N}")
        .Build();

    private ConnectionMultiplexer? admin;

    public string ConnectionString => container.GetConnectionString();

    public IDatabase Database => Admin.GetDatabase();

    public IServer Server => Admin.GetServer(Admin.GetEndPoints()[0]);

    private ConnectionMultiplexer Admin => admin ?? throw new InvalidOperationException("Fixture Redis não iniciada.");

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        admin = await ConnectionMultiplexer.ConnectAsync($"{ConnectionString},allowAdmin=true");
    }

    public async Task DisposeAsync()
    {
        if (admin is not null)
        {
            await admin.DisposeAsync();
        }

        await container.DisposeAsync();
    }

    /// <summary>Conexões TCP aceitas pelo servidor desde o boot (INFO stats).</summary>
    public async Task<long> TotalConnectionsReceivedAsync()
    {
        var info = (string)(await Server.ExecuteAsync("INFO", "stats"))!;
        var line = info.Split('\n').Single(entry => entry.StartsWith("total_connections_received:", StringComparison.Ordinal));
        return long.Parse(line["total_connections_received:".Length..].Trim(), CultureInfo.InvariantCulture);
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
