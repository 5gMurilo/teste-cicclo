using System.Diagnostics;
using System.Text;

namespace Cicclo.Api.Tests.Support;

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Executa processos reais (dotnet, docker) com ambiente explícito e tempo limite finito.</summary>
public static class ProcessRunner
{
    /// <summary>Variáveis que o processo filho nunca herda da sessão; cada teste declara as suas.</summary>
    private static readonly string[] IsolatedVariables =
        ["ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ConnectionStrings__Cicclo", "Laundry__DemoMode"];

    public static string DotNet { get; } = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in IsolatedVariables)
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Não foi possível iniciar {fileName}.");
        var output = new StringBuilder();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} excedeu o tempo limite.\n{await stdout}\n{await stderr}");
        }

        output.Append(await stdout).Append(await stderr);
        return new ProcessResult(process.ExitCode, output.ToString());
    }
}
