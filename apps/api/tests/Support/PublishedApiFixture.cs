using Xunit;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Publica a API em diretório temporário fora da árvore do repositório, como um operador faria,
/// e remove somente esse diretório ao final.
/// </summary>
public sealed class PublishedApiFixture : IAsyncLifetime
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"cicclo-api-publish-{Guid.NewGuid():N}");

    public string EntryAssembly => Path.Combine(Directory, "Cicclo.Api.dll");

    public async Task InitializeAsync()
    {
        var result = await ProcessRunner.RunAsync(
            ProcessRunner.DotNet,
            ["publish", RepositoryPaths.Api("src", "Cicclo.Api.csproj"), "--configuration", "Release", "--no-restore", "--output", Directory, "-nodeReuse:false", "/p:UseAppHost=false"],
            RepositoryPaths.ApiRoot,
            new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
            TimeSpan.FromMinutes(5));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish falhou ({result.ExitCode}).\n{result.Output}");
        }
    }

    public Task DisposeAsync()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }

        return Task.CompletedTask;
    }

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

    /// <summary>Executa `--reset-demo`, com `--confirm-reset` somente quando a confirmação é informada.</summary>
    public Task<ProcessResult> ResetAsync(string? connectionString, string? confirmation, string environment = "Development", string? demoMode = "true") =>
        ProcessRunner.RunAsync(
            ProcessRunner.DotNet,
            confirmation is null ? [EntryAssembly, "--reset-demo"] : [EntryAssembly, "--reset-demo", "--confirm-reset", confirmation],
            Path.GetTempPath(),
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = environment,
                ["Laundry__DemoMode"] = demoMode,
                ["ConnectionStrings__Cicclo"] = connectionString,
            });
}
