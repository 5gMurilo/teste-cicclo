using Xunit;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Projeto Compose descartável para executar apps/api/scripts/start-demo.sh de verdade: nome, .env, portas loopback e
/// volume exclusivos. Limpa somente os recursos do próprio projeto, inclusive o Redis opcional do perfil `cache`.
/// </summary>
public sealed class DemoProject : IAsyncDisposable
{
    /// <summary>Variáveis que o script e o Compose nunca herdam da sessão de quem roda os testes.</summary>
    private static readonly string[] InheritedComposeVariables =
        ["POSTGRES_PASSWORD", "POSTGRES_PORT", "API_PORT", "CICCLO_API_IMAGE", "CICCLO_OTLP_ENDPOINT", "CICCLO_READY_TIMEOUT", "COMPOSE_FILE", "COMPOSE_PROFILES",
            "REDIS_PASSWORD", "REDIS_PORT", "CICCLO_CACHE_ENABLED", "CICCLO_CACHE_TTL_SECONDS"];

    private readonly string image;
    private readonly IReadOnlyList<string> extraEnvLines;

    private DemoProject(string image, string password, string prefix, IReadOnlyList<string> extraEnvLines)
    {
        this.image = image;
        this.extraEnvLines = extraEnvLines;
        Password = password;
        Project = $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Project);
        System.IO.Directory.CreateDirectory(Directory);
        EnvFile = System.IO.Path.Combine(Directory, "demo.env");
        WriteEnvFile(includePassword: true);
    }

    public string Project { get; }

    public string Directory { get; }

    public string EnvFile { get; }

    public string Password { get; }

    /// <summary>Cria o projeto; <paramref name="extraEnvLines"/> entram no .env depois das variáveis-base.</summary>
    public static DemoProject Create(string image, string? password = null, string prefix = "cicclo-t9", IReadOnlyList<string>? extraEnvLines = null) =>
        new(image, password ?? $"pw-{Guid.NewGuid():N}", prefix, extraEnvLines ?? []);

    public void WriteEnvFile(bool includePassword)
    {
        var lines = new List<string>
        {
            $"POSTGRES_PORT={OtlpReceiver.FreeLoopbackPort()}",
            $"API_PORT={OtlpReceiver.FreeLoopbackPort()}",
            $"REDIS_PORT={OtlpReceiver.FreeLoopbackPort()}",
            $"CICCLO_API_IMAGE={image}",
        };
        if (includePassword)
        {
            // Aspas simples: o Compose lê o valor literalmente, sem interpolação.
            lines.Insert(0, $"POSTGRES_PASSWORD='{Password}'");
        }

        lines.AddRange(extraEnvLines);
        File.WriteAllLines(EnvFile, lines);
    }

    public Task<ProcessResult> RunScriptAsync(Dictionary<string, string?>? overrides = null)
    {
        var environment = Environment(overrides);
        return ProcessRunner.RunAsync(RepositoryPaths.Api("scripts", "start-demo.sh"), [], RepositoryPaths.ApiRoot, environment, TimeSpan.FromMinutes(15));
    }

    public Task<ProcessResult> ComposeAsync(params string[] arguments) =>
        ProcessRunner.RunAsync(
            "docker",
            ["compose", "--project-directory", RepositoryPaths.ApiRoot, "--env-file", EnvFile, .. arguments],
            RepositoryPaths.ApiRoot,
            Environment(),
            TimeSpan.FromMinutes(5));

    public async Task<string> ComposeTextAsync(params string[] arguments)
    {
        var result = await ComposeAsync(arguments);
        Assert.True(result.ExitCode == 0, result.Output);
        return result.Output.Trim();
    }

    public Task<string> SqlAsync(string sql) =>
        ComposeTextAsync("exec", "--no-TTY", "postgres", "psql", "--username", "cicclo", "--dbname", "cicclo", "--no-align", "--tuples-only", "--quiet", "--set", "ON_ERROR_STOP=1", "--command", sql);

    public async Task<string> ApiAddressAsync() => await ComposeTextAsync("port", "api", "8080");

    public async ValueTask DisposeAsync()
    {
        await ComposeAsync("--profile", "app", "--profile", "cache", "down", "--volumes", "--remove-orphans");
        System.IO.Directory.Delete(Directory, recursive: true);
    }

    private Dictionary<string, string?> Environment(Dictionary<string, string?>? overrides = null)
    {
        var environment = InheritedComposeVariables.ToDictionary(name => name, _ => (string?)null);
        environment["COMPOSE_PROJECT_NAME"] = Project;
        environment["CICCLO_ENV_FILE"] = EnvFile;
        foreach (var (name, value) in overrides ?? [])
        {
            environment[name] = value;
        }

        return environment;
    }
}
