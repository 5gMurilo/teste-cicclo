using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Simula o endereço remoto da conexão (como o Kestrel faria) a partir de um cabeçalho exclusivo do teste e registra o
/// endereço observado. Sem o cabeçalho, mantém o comportamento do TestServer.
/// </summary>
public sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public ConcurrentQueue<IPAddress?> Observed { get; } = new();

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var address))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
            }

            Observed.Enqueue(context.Connection.RemoteIpAddress);
            await nextMiddleware(context);
        });
        next(app);
    };
}
