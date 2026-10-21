using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Cicclo.Api.Tests.Support;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Operation;

/// <summary>
/// Operação opcional do cache Redis (E08-T3) pelo caminho real de demonstração: apps/api/scripts/start-demo.sh, o
/// docker-compose.yml e a imagem da API. Cada cenário usa projeto Compose, portas loopback efêmeras e senhas aleatórias
/// exclusivos, e remove só os próprios recursos. Prova que o Redis é opt-in, nunca pré-requisito, e que o PostgreSQL
/// continua autoritativo; as regras funcionais do cache já foram provadas em E08-T1/T2.
/// </summary>
[Trait("Increment", "E08")]
[Trait("Task", "T3")]
public sealed class OptionalCacheOperationTests(DemoImageFixture image) : IClassFixture<DemoImageFixture>
{
    private const string Key = "wallet:demo:v1";
    private const string Degraded = "Cache da wallet degradado:";

    /// <summary>Intervalo do polling limitado: mantém as leituras bem abaixo do limite de 60 por minuto.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    private static readonly Dictionary<string, string?> CacheOn = new() { ["CICCLO_CACHE_ENABLED"] = "true" };

    [Fact]
    public async Task O01_DefaultDemoRunsWithoutRedisEvenWhenTheEnvFileAsksForCache()
    {
        // O .env pede cache, mas o script só liga a extensão por pedido explícito na chamada.
        await using var demo = Demo(redisPassword: RandomSecret(), "CICCLO_CACHE_ENABLED=true");

        var run = await demo.RunScriptAsync();

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.DoesNotContain("Subindo o Redis opcional", run.Output, StringComparison.Ordinal);
        Assert.Contains("cache Redis desligado)", run.Output, StringComparison.Ordinal);
        Assert.Contains("/api/v1/wallet {\"balanceCents\":5000,", run.Output, StringComparison.Ordinal);
        Assert.Equal(["api", "postgres"], await ServicesAsync(demo));
        Assert.Equal("false", await demo.ComposeTextAsync("exec", "--no-TTY", "api", "printenv", "Caching__Enabled"));
        Assert.Equal(["postgres"], Lines(await demo.ComposeTextAsync("config", "--services")));
        Assert.Equal(["api", "bootstrap", "postgres"], Lines(await demo.ComposeTextAsync("--profile", "app", "config", "--services")).Order(StringComparer.Ordinal));
        using var model = JsonDocument.Parse(await demo.ComposeTextAsync("--profile", "app", "--profile", "cache", "config", "--format", "json"));
        foreach (var service in new[] { "api", "bootstrap" })
        {
            Assert.Equal(["postgres"], model.RootElement.GetProperty("services").GetProperty(service).GetProperty("depends_on").EnumerateObject().Select(dependency => dependency.Name));
        }

        using var client = await ClientAsync(demo);
        Assert.Equal("Healthy", await client.GetStringAsync("/health/ready"));
        Assert.Equal(5000L, await BalanceAsync(client));
    }

    [Fact]
    public async Task O02_CacheProfileServesMissThenHitAndInvalidatesAfterThePurchaseCommit()
    {
        var secret = RandomSecret();
        await using var demo = Demo(secret, "CICCLO_CACHE_ENABLED=true");

        var run = await demo.RunScriptAsync(CacheOn);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("==> Subindo o Redis opcional (perfil cache)", run.Output, StringComparison.Ordinal);
        Assert.Contains("cache Redis ligado)", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, run.Output, StringComparison.Ordinal);
        Assert.Equal(["api", "postgres", "redis"], await ServicesAsync(demo));
        Assert.Equal("true", await demo.ComposeTextAsync("exec", "--no-TTY", "api", "printenv", "Caching__Enabled"));
        Assert.StartsWith("127.0.0.1:", await demo.ComposeTextAsync("port", "redis", "6379"), StringComparison.Ordinal);
        var anonymous = await demo.ComposeAsync("exec", "--no-TTY", "--env", "REDISCLI_AUTH=", "redis", "redis-cli", "EXISTS", Key);
        Assert.Contains("NOAUTH", anonymous.Output, StringComparison.Ordinal);

        // Miss do smoke do script: a projeção do PostgreSQL foi gravada com TTL padrão de 30 s, e só ela.
        Assert.Equal((5000L, "BRL"), CachedProjection(await RedisAsync(demo, "GET", Key)));
        Assert.InRange(long.Parse(await RedisAsync(demo, "PTTL", Key), System.Globalization.CultureInfo.InvariantCulture), 1L, 30_000L);
        Assert.Equal("1", await RedisAsync(demo, "DBSIZE"));

        // Hit: mudança feita fora da API não aparece no GET até invalidação ou TTL (leitura eventual).
        await demo.SqlAsync("UPDATE wallets SET balance = 45.00;");
        using var client = await ClientAsync(demo);
        Assert.Equal(5000L, await BalanceAsync(client));

        // A compra decide pelo PostgreSQL (45.00) e invalida a chave só depois do commit.
        var (status, remaining) = await PurchaseWashAsync(client);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(2610L, remaining);
        Assert.Equal("0", await RedisAsync(demo, "EXISTS", Key));
        Assert.Equal(2610L, await BalanceAsync(client));
        Assert.Equal((2610L, "BRL"), CachedProjection(await RedisAsync(demo, "GET", Key)));
        Assert.Equal("26.10|1|debit|18.90", await demo.SqlAsync(
            "SELECT (SELECT balance FROM wallets) || '|' || count(*) || '|' || string_agg(kind || '|' || amount, ',') FROM wallet_transactions"));
    }

    [Fact]
    public async Task O03_CacheRequestedWithoutAHealthyRedisStillStartsAndServesFromPostgres()
    {
        // Sem REDIS_PASSWORD o serviço redis recusa subir: o script avisa e a API sobe igual.
        await using var demo = Demo(redisPassword: null);

        var run = await demo.RunScriptAsync(CacheOn);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("AVISO: Redis não ficou saudável; a API sobe sem cache efetivo", run.Output, StringComparison.Ordinal);
        Assert.Contains("/api/v1/wallet {\"balanceCents\":5000,", run.Output, StringComparison.Ordinal);
        Assert.Contains("REDIS_PASSWORD ausente", await demo.ComposeTextAsync("logs", "--no-color", "redis"), StringComparison.Ordinal);
        Assert.Equal(["api", "postgres"], await ServicesAsync(demo));
        Assert.Equal("true", await demo.ComposeTextAsync("exec", "--no-TTY", "api", "printenv", "Caching__Enabled"));

        using var client = await ClientAsync(demo);
        Assert.Equal("Healthy", await client.GetStringAsync("/health/live"));
        Assert.Equal("Healthy", await client.GetStringAsync("/health/ready"));
        Assert.Equal(5000L, await BalanceAsync(client));
        var (status, remaining) = await PurchaseWashAsync(client);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(3110L, remaining);
        Assert.Equal(3110L, await BalanceAsync(client));
        Assert.Contains(Degraded, await demo.ComposeTextAsync("logs", "--no-color", "api"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task O04_RedisStoppedAfterBootKeepsHealthReadsAndPurchasesOnPostgresAndRecovers()
    {
        var secret = RandomSecret();
        await using var demo = Demo(secret, "CICCLO_CACHE_ENABLED=true");
        var run = await demo.RunScriptAsync(CacheOn);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("1", await RedisAsync(demo, "EXISTS", Key));

        Assert.Equal(0, (await demo.ComposeAsync("stop", "redis")).ExitCode);

        using var client = await ClientAsync(demo);
        Assert.Equal("Healthy", await client.GetStringAsync("/health/live"));
        Assert.Equal("Healthy", await client.GetStringAsync("/health/ready"));
        Assert.Equal(5000L, await BalanceAsync(client));
        var (status, remaining) = await PurchaseWashAsync(client);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(3110L, remaining);
        Assert.Equal(3110L, await BalanceAsync(client));
        Assert.Equal("31.10|1", await demo.SqlAsync("SELECT (SELECT balance FROM wallets) || '|' || count(*) FROM wallet_transactions"));
        var logs = await demo.ComposeTextAsync("logs", "--no-color", "api");
        Assert.Contains("\"EventId\":800", logs, StringComparison.Ordinal);
        Assert.Contains(Degraded, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);

        // Redis volta vazio (sem persistência); a mesma API repovoa a chave sem reiniciar.
        var restarted = await demo.ComposeAsync("--profile", "cache", "up", "--detach", "--wait", "redis");
        Assert.True(restarted.ExitCode == 0, restarted.Output);
        var elapsed = Stopwatch.StartNew();
        string cached;
        do
        {
            await Task.Delay(Poll);
            Assert.Equal(3110L, await BalanceAsync(client));
            cached = await RedisAsync(demo, "GET", Key);
        }
        while (cached.Length == 0 && elapsed.Elapsed < TimeSpan.FromSeconds(15));

        Assert.Equal((3110L, "BRL"), CachedProjection(cached));
    }

    [Fact]
    public async Task O05_RerunAndResetRemoveOnlyTheWalletKeyAndKeepForeignData()
    {
        await using var demo = Demo(RandomSecret(), "CICCLO_CACHE_ENABLED=true");
        var first = await demo.RunScriptAsync(CacheOn);
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Equal("OK", await RedisAsync(demo, "SET", "wallet:demo:v2", "externo"));
        Assert.Equal("OK", await RedisAsync(demo, "SET", "outro:sistema", "dado"));
        using (var client = await ClientAsync(demo))
        {
            Assert.Equal(HttpStatusCode.Created, (await PurchaseWashAsync(client)).Status);
            Assert.Equal(3110L, await BalanceAsync(client));
        }

        var second = await demo.RunScriptAsync(CacheOn);

        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Contains("/api/v1/wallet {\"balanceCents\":3110,", second.Output, StringComparison.Ordinal);
        Assert.Equal("3", await RedisAsync(demo, "DBSIZE"));
        Assert.Equal((3110L, "BRL"), CachedProjection(await RedisAsync(demo, "GET", Key)));

        Assert.Equal(0, (await demo.ComposeAsync("stop", "api")).ExitCode);
        var reset = await demo.ComposeAsync("run", "--rm", "--no-TTY", "bootstrap", "--reset-demo", "--confirm-reset", "postgres:5432/cicclo");

        Assert.True(reset.ExitCode == 0, reset.Output);
        Assert.Contains("Reset demo concluído: dados iniciais restaurados pelo seed.sql.", reset.Output, StringComparison.Ordinal);
        Assert.Equal("0", await RedisAsync(demo, "EXISTS", Key));
        Assert.Equal("externo", await RedisAsync(demo, "GET", "wallet:demo:v2"));
        Assert.Equal("dado", await RedisAsync(demo, "GET", "outro:sistema"));
        Assert.Equal("50.00|0", await demo.SqlAsync("SELECT (SELECT balance FROM wallets) || '|' || count(*) FROM wallet_transactions"));
        Assert.Equal(0, (await demo.ComposeAsync("up", "--detach", "api")).ExitCode);
        using var restarted = await ClientAsync(demo);
        await WaitReadyAsync(restarted);
        Assert.Equal(5000L, await BalanceAsync(restarted));
    }

    [Fact]
    public async Task O06_ReducedTtlServesAStaleSnapshotOnlyUntilTheWriteExpires()
    {
        await using var demo = Demo(RandomSecret(), "CICCLO_CACHE_ENABLED=true", "CICCLO_CACHE_TTL_SECONDS=5");
        var run = await demo.RunScriptAsync(CacheOn);
        Assert.True(run.ExitCode == 0, run.Output);
        using var client = await ClientAsync(demo);

        // Gravação nova sob controle do teste: o TTL conta a partir dela, no relógio do servidor Redis.
        Assert.Equal("1", await RedisAsync(demo, "DEL", Key));
        var written = Stopwatch.StartNew();
        Assert.Equal(5000L, await BalanceAsync(client));
        Assert.InRange(long.Parse(await RedisAsync(demo, "PTTL", Key), System.Globalization.CultureInfo.InvariantCulture), 1L, 5_000L);
        await demo.SqlAsync("UPDATE wallets SET balance = 12.34;");

        Assert.Equal(5000L, await BalanceAsync(client));
        Assert.True(written.Elapsed < TimeSpan.FromSeconds(5), $"leitura antiga observada só depois do TTL: {written.Elapsed}");

        long balance;
        do
        {
            await Task.Delay(Poll);
            balance = await BalanceAsync(client);
        }
        while (balance == 5000L && written.Elapsed < TimeSpan.FromSeconds(20));

        Assert.Equal(1234L, balance);
        Assert.True(written.Elapsed >= TimeSpan.FromSeconds(4), $"projeção trocada antes do TTL: {written.Elapsed}");
        Assert.Equal((1234L, "BRL"), CachedProjection(await RedisAsync(demo, "GET", Key)));
    }

    private DemoProject Demo(string? redisPassword, params string[] extraEnvLines) =>
        DemoProject.Create(
            image.Image,
            prefix: "cicclo-e08t3",
            extraEnvLines: redisPassword is null ? extraEnvLines : [$"REDIS_PASSWORD={redisPassword}", .. extraEnvLines]);

    private static string RandomSecret() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

    private static Task<string> RedisAsync(DemoProject demo, params string[] command) =>
        demo.ComposeTextAsync(["exec", "--no-TTY", "redis", "redis-cli", .. command]);

    private static (long BalanceCents, string Currency) CachedProjection(string json)
    {
        using var entry = JsonDocument.Parse(json);
        return (entry.RootElement.GetProperty("balanceCents").GetInt64(), entry.RootElement.GetProperty("currency").GetString()!);
    }

    /// <summary>Serviços com container em execução no projeto (Compose ps lista só os ativos).</summary>
    private static async Task<string[]> ServicesAsync(DemoProject demo) =>
        [.. Lines(await demo.ComposeTextAsync("--profile", "app", "--profile", "cache", "ps", "--format", "{{.Service}}")).Order(StringComparer.Ordinal)];

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<HttpClient> ClientAsync(DemoProject demo) =>
        new() { BaseAddress = new Uri($"http://{await demo.ApiAddressAsync()}") };

    /// <summary>Espera limitada pela readiness da API recém-iniciada, como o próprio start-demo.sh.</summary>
    private static async Task WaitReadyAsync(HttpClient client)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var ready = await client.GetAsync("/health/ready");
                if (ready.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (elapsed.Elapsed < TimeSpan.FromSeconds(60))
            {
            }

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(60), "API não ficou pronta em 60 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    private static async Task<long> BalanceAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/wallet"));
        return body.RootElement.GetProperty("balanceCents").GetInt64();
    }

    private static async Task<(HttpStatusCode Status, long Remaining)> PurchaseWashAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings")
        {
            Content = new StringContent("""{"serviceCodes":["wash"],"expectedTotalCents":1890}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        using var body = JsonDocument.Parse(text);
        return (response.StatusCode, body.RootElement.GetProperty("remainingBalanceCents").GetInt64());
    }
}
