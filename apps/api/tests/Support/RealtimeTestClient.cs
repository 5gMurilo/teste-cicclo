using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Cicclo.Api.Tests.Support;

/// <summary>
/// Cliente SignalR 10.0.13 real do Hub da API (`/hubs/laundry`), por long polling sobre o TestServer do host. Registra os
/// nomes da tabela de eventos do design como literais (não herda as constantes do servidor) e entrega cada aviso recebido
/// com o payload JSON por valor.
/// </summary>
public sealed class RealtimeTestClient : IAsyncDisposable
{
    public const string HubPath = "/hubs/laundry";

    private static readonly string[] EventNames =
        ["booking.updated", "queue.updated", "machine.offered", "machine.offer_expired", "wallet.updated", "machine.failed"];

    private readonly Channel<(string Name, JsonElement Payload)> received = Channel.CreateUnbounded<(string, JsonElement)>();

    private RealtimeTestClient(HubConnection connection)
    {
        Connection = connection;
        foreach (var name in EventNames)
        {
            connection.On<JsonElement>(name, payload => received.Writer.TryWrite((name, payload)));
        }
    }

    public HubConnection Connection { get; }

    /// <summary>
    /// Conecta e espera o registro da conexão: a primeira invocação recusada funciona como barreira, porque o servidor só
    /// despacha invocações depois de incluir a conexão no lifetime manager (Clients.All já a alcança).
    /// </summary>
    public static async Task<RealtimeTestClient> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, HubPath), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();
        var client = new RealtimeTestClient(connection);
        await connection.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<object>("Ready"));
        return client;
    }

    public async Task<(string Name, JsonElement Payload)> NextAsync() =>
        await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Descarta avisos até o primeiro com o nome indicado, inclusive.</summary>
    public async Task SkipThroughAsync(string name)
    {
        while ((await NextAsync()).Name != name)
        {
        }
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
