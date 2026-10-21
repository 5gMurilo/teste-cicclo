using Xunit;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Publica a API uma única vez por processo de testes, em diretório temporário fora da árvore do repositório, como um
/// operador faria. As classes que usam esta fixture compartilham o mesmo artefato: publicações concorrentes disputariam
/// o mesmo `obj/Release` (MSB4018). O diretório é removido somente ao fim do processo.
/// </summary>
public sealed class PublishedApiFixture : IAsyncLifetime
{
    private static readonly string SharedDirectory = Path.Combine(Path.GetTempPath(), $"cicclo-api-publish-{Environment.ProcessId}-{Guid.NewGuid():N}");

    private static readonly Lazy<Task> Publication = new(PublishAsync);

    public string Directory => SharedDirectory;

    public string EntryAssembly => Path.Combine(Directory, "Cicclo.Api.dll");

    public Task InitializeAsync() => Publication.Value;

    public Task DisposeAsync() => Task.CompletedTask;

    public Task<ProcessResult> BootstrapAsync(string? connectionString, string environment = "Development", string? demoMode = "true") =>
        ProcessRunner.RunAsync(
            ProcessRunner.DotNet,
            [EntryAssembly, "--bootstrap-demo"],
            Path.GetTempPath(),
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = environment,
                ["Laundry__DemoMode"] = demoMode,
                ["ConnectionStrings__Cicclo"] = connectionString,
            });

    /// <summary>
    /// Executa `--reset-demo`, com `--confirm-reset` somente quando a confirmação é informada; <paramref name="extra"/>
    /// acrescenta variáveis de ambiente (por exemplo a seção Caching do cache opcional).
    /// </summary>
    public Task<ProcessResult> ResetAsync(
        string? connectionString, string? confirmation, string environment = "Development", string? demoMode = "true", IReadOnlyDictionary<string, string?>? extra = null)
    {
        var variables = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = environment,
            ["Laundry__DemoMode"] = demoMode,
            ["ConnectionStrings__Cicclo"] = connectionString,
        };
        foreach (var (name, value) in extra ?? new Dictionary<string, string?>())
        {
            variables[name] = value;
        }

        return ProcessRunner.RunAsync(
            ProcessRunner.DotNet,
            confirmation is null ? [EntryAssembly, "--reset-demo"] : [EntryAssembly, "--reset-demo", "--confirm-reset", confirmation],
            Path.GetTempPath(),
            variables);
    }

    private static async Task PublishAsync()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (System.IO.Directory.Exists(SharedDirectory))
            {
                System.IO.Directory.Delete(SharedDirectory, recursive: true);
            }
        };

        var result = await ProcessRunner.RunAsync(
            ProcessRunner.DotNet,
            ["publish", RepositoryPaths.Api("src", "Cicclo.Api.csproj"), "--configuration", "Release", "--no-restore", "--output", SharedDirectory, "-nodeReuse:false", "/p:UseAppHost=false"],
            RepositoryPaths.ApiRoot,
            new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
            TimeSpan.FromMinutes(5));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish falhou ({result.ExitCode}).\n{result.Output}");
        }
    }
}
