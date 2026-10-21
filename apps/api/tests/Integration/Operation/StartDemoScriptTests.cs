using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cicclo.Api.Tests.Support;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Operation;

/// <summary>Imagem exclusiva desta execução, construída pelo próprio script e removida ao final.</summary>
public sealed class DemoImageFixture : IAsyncLifetime
{
    public string Image { get; } = $"cicclo-api-t9-test:{Guid.NewGuid():N}";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() =>
        ProcessRunner.RunAsync("docker", ["image", "rm", "--force", Image], RepositoryPaths.ApiRoot, timeout: TimeSpan.FromMinutes(1));
}

/// <summary>
/// Executa apps/api/scripts/start-demo.sh de verdade, cada cenário em projeto Compose, volume, portas e .env exclusivos.
/// Limpa somente os recursos do próprio projeto de teste.
/// </summary>
[Trait("Increment", "E01")]
[Trait("Task", "T9")]
public sealed class StartDemoScriptTests(DemoImageFixture image) : IClassFixture<DemoImageFixture>
{
    [Fact]
    public async Task D01_FreshProjectStartsDatabaseBootstrapAndApiWithSmoke()
    {
        await using var demo = DemoProject.Create(image.Image);

        var run = await demo.RunScriptAsync();

        Assert.True(run.ExitCode == 0, run.Output);
        var bootstrap = run.Output.IndexOf("==> Executando bootstrap one-shot", StringComparison.Ordinal);
        var seeded = run.Output.IndexOf("Bootstrap demo: seed concluído.", StringComparison.Ordinal);
        var api = run.Output.IndexOf("==> Iniciando a API", StringComparison.Ordinal);
        Assert.True(bootstrap >= 0 && seeded > bootstrap && api > seeded, run.Output);
        Assert.Contains("/api/v1/wallet {\"balanceCents\":5000,", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(demo.Password, run.Output, StringComparison.Ordinal);

        var address = await demo.ApiAddressAsync();
        Assert.StartsWith("127.0.0.1:", address, StringComparison.Ordinal);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{address}") };
        Assert.Equal(5000L, await BalanceAsync(client));
        Assert.Equal([("dry", 2090L, true), ("wash", 1890L, true)], await ServicesAsync(client));
        Assert.Equal("Healthy", await client.GetStringAsync("/health/ready"));
        Assert.Equal("1|2|2", await demo.SqlAsync("SELECT (SELECT count(*) FROM wallets) || '|' || (SELECT count(*) FROM services) || '|' || (SELECT count(*) FROM machines)"));
        Assert.DoesNotContain("bootstrap", await demo.ComposeTextAsync("ps", "--all", "--format", "{{.Service}}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task D02_RepeatedStartRunsANewBootstrapAndPreservesChangedData()
    {
        await using var demo = DemoProject.Create(image.Image);
        var first = await demo.RunScriptAsync();
        Assert.True(first.ExitCode == 0, first.Output);
        await demo.SqlAsync(
            """
            UPDATE wallets SET balance = 31.10;
            UPDATE services SET current_price = 25.00 WHERE code = 'wash';
            UPDATE machines SET state = 'out_of_service' WHERE machine_type = 'washer';
            """);

        var second = await demo.RunScriptAsync();

        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Contains("Bootstrap demo: migrations concluídas.", second.Output, StringComparison.Ordinal);
        Assert.Contains("Bootstrap demo: seed concluído.", second.Output, StringComparison.Ordinal);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{await demo.ApiAddressAsync()}") };
        Assert.Equal(3110L, await BalanceAsync(client));
        Assert.Equal([("dry", 2090L, true), ("wash", 2500L, false)], await ServicesAsync(client));
        Assert.Equal("31.10|25.00|out_of_service|1|2|2", await demo.SqlAsync(
            "SELECT (SELECT balance FROM wallets) || '|' || (SELECT current_price FROM services WHERE code = 'wash') || '|' || (SELECT state FROM machines WHERE machine_type = 'washer') || '|' || (SELECT count(*) FROM wallets) || '|' || (SELECT count(*) FROM services) || '|' || (SELECT count(*) FROM machines)"));
    }

    [Fact]
    public async Task D03_BootstrapFailurePreventsApiStartWithoutDeletingData()
    {
        await using var demo = DemoProject.Create(image.Image);
        var postgres = await demo.ComposeAsync("up", "--detach", "--wait", "postgres");
        Assert.True(postgres.ExitCode == 0, postgres.Output);
        await demo.SqlAsync("CREATE TABLE wallets (id int); INSERT INTO wallets VALUES (42);");

        var run = await demo.RunScriptAsync();

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Bootstrap demo: migrations falharam; seed não executado.", run.Output, StringComparison.Ordinal);
        Assert.Contains("ERRO: bootstrap falhou; a API não foi iniciada", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("==> Iniciando a API", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("api", (await demo.ComposeTextAsync("ps", "--all", "--format", "{{.Service}}")).Split('\n'));
        Assert.Equal("42", await demo.SqlAsync("SELECT string_agg(id::text, ',') FROM wallets"));
    }

    [Fact]
    public async Task D04_MissingPrerequisiteOrConfigurationStopsClearlyWithoutCreatingResources()
    {
        await using var demo = DemoProject.Create(image.Image);
        using var tools = ToolDirectory.Without("docker");

        var noDocker = await demo.RunScriptAsync(new() { ["PATH"] = tools.Path });
        var noEnvFile = await demo.RunScriptAsync(new() { ["CICCLO_ENV_FILE"] = System.IO.Path.Combine(demo.Directory, "ausente.env") });
        demo.WriteEnvFile(includePassword: false);
        var noPassword = await demo.RunScriptAsync();

        Assert.Equal(2, noDocker.ExitCode);
        Assert.Contains("ERRO: pré-requisito ausente: 'docker' não encontrado no PATH.", noDocker.Output, StringComparison.Ordinal);
        Assert.Equal(2, noEnvFile.ExitCode);
        Assert.Contains("ausente.env' não encontrado", noEnvFile.Output, StringComparison.Ordinal);
        Assert.Equal(2, noPassword.ExitCode);
        Assert.Contains("Set POSTGRES_PASSWORD", noPassword.Output, StringComparison.Ordinal);
        Assert.Contains("ERRO: configuração do Compose inválida", noPassword.Output, StringComparison.Ordinal);
        foreach (var run in new[] { noDocker, noEnvFile, noPassword })
        {
            Assert.DoesNotContain("==> Subindo PostgreSQL", run.Output, StringComparison.Ordinal);
        }

        Assert.Equal(string.Empty, (await ProcessRunner.RunAsync("docker", ["ps", "--all", "--quiet", "--filter", $"label=com.docker.compose.project={demo.Project}"], RepositoryPaths.ApiRoot)).Output.Trim());
    }

    [Fact]
    public async Task D05_PasswordWithShellCharactersIsDataNeverExecutedOrPrinted()
    {
        var marker = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cicclo-t9-pwned-{Guid.NewGuid():N}");
        await using var demo = DemoProject.Create(image.Image, password: $"pw-{Guid.NewGuid():N};$(touch {marker})`touch {marker}`&|\"x");

        var run = await demo.RunScriptAsync();

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.False(File.Exists(marker));
        using var client = new HttpClient { BaseAddress = new Uri($"http://{await demo.ApiAddressAsync()}") };
        Assert.Equal(5000L, await BalanceAsync(client));
        Assert.DoesNotContain(demo.Password, run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(demo.Password, await demo.ComposeTextAsync("logs", "--no-color", "api", "postgres"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task D06_ApiThatNeverBecomesReadyFailsAfterAFiniteTimeout()
    {
        await using var demo = DemoProject.Create(image.Image);
        var overrideFile = System.IO.Path.Combine(demo.Directory, "never-ready.yml");
        await File.WriteAllTextAsync(
            overrideFile,
            """
            services:
              api:
                environment:
                  ConnectionStrings__Cicclo: Host=postgres;Port=5432;Database=nao_existe;Username=cicclo
            """);
        var watch = Stopwatch.StartNew();

        var run = await demo.RunScriptAsync(new()
        {
            ["COMPOSE_FILE"] = $"{RepositoryPaths.Api("docker-compose.yml")}:{overrideFile}",
            ["CICCLO_READY_TIMEOUT"] = "5",
        });

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("ERRO: a API não ficou pronta em 5s.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("==> Smoke das consultas", run.Output, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(3), $"script levou {watch.Elapsed}");
        using var client = new HttpClient { BaseAddress = new Uri($"http://{await demo.ApiAddressAsync()}") };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    private static async Task<long> BalanceAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/wallet"));
        return body.RootElement.GetProperty("balanceCents").GetInt64();
    }

    private static async Task<IReadOnlyList<(string Code, long Cents, bool CanBeBooked)>> ServicesAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/services"));
        return [.. body.RootElement.GetProperty("services").EnumerateArray().Select(item => (
            item.GetProperty("code").GetString()!,
            item.GetProperty("unitPriceCents").GetInt64(),
            item.GetProperty("canBeBooked").GetBoolean()))];
    }

    /// <summary>Diretório de PATH com as ferramentas do sistema exceto as indicadas, para simular pré-requisito ausente.</summary>
    private sealed class ToolDirectory : IDisposable
    {
        private ToolDirectory(string path) => Path = path;

        public string Path { get; }

        public static ToolDirectory Without(params string[] missing)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cicclo-t9-tools-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(path);
            foreach (var tool in new[] { "bash", "env", "dirname", "curl", "sleep", "docker" }.Except(missing))
            {
                var source = (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator)
                    .Select(directory => System.IO.Path.Combine(directory, tool))
                    .First(File.Exists);
                File.CreateSymbolicLink(System.IO.Path.Combine(path, tool), source);
            }

            return new ToolDirectory(path);
        }

        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }
}
