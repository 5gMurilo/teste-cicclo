using Cicclo.Api.Platform.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Host de teste que aplica somente a borda HTTP de produção; as rotas que provocam falhas existem apenas aqui,
/// nunca no código da API.
/// </summary>
public sealed class IsolatedHttpHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private IsolatedHttpHost(WebApplication app, CapturingLoggerProvider logs)
    {
        this.app = app;
        Logs = logs;
    }

    public CapturingLoggerProvider Logs { get; }

    public HttpClient CreateClient() => app.GetTestClient();

    public static async Task<IsolatedHttpHost> StartAsync(Action<WebApplication> map, Action<WebApplication>? beforeBoundary = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var logs = new CapturingLoggerProvider();
        builder.Logging.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddHttpBoundary();
        var app = builder.Build();
        beforeBoundary?.Invoke(app);
        app.UseHttpBoundary();
        map(app);
        await app.StartAsync();
        return new IsolatedHttpHost(app, logs);
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
