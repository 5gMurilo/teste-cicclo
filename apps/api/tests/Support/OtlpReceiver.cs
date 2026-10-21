using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Cicclo.Api.Tests.Support;

public sealed record OtlpRequest(string Method, string Path, string? ContentType, byte[] Body);

/// <summary>
/// Coletor OTLP/HTTP controlado pelo teste, em porta de loopback exclusiva. Registra cada requisição recebida e responde
/// 200 com corpo protobuf vazio (ExportServiceResponse sem campos). Usa HttpListener, não ASP.NET Core, para que os
/// providers do host observado não instrumentem o próprio recebimento.
/// </summary>
public sealed class OtlpReceiver : IDisposable
{
    private readonly HttpListener listener = new();

    private readonly ConcurrentQueue<OtlpRequest> requests = new();

    private readonly Task loop;

    private OtlpReceiver(int port)
    {
        Port = port;
        Endpoint = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(Endpoint.AbsoluteUri);
        listener.Start();
        loop = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public Uri Endpoint { get; }

    public IReadOnlyList<OtlpRequest> Requests => [.. requests];

    public static OtlpReceiver Start() => new(FreeLoopbackPort());

    public static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        listener.Close();
        loop.Wait(TimeSpan.FromSeconds(5));
    }

    private async Task AcceptAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            requests.Enqueue(new OtlpRequest(context.Request.HttpMethod, context.Request.Url!.AbsolutePath, context.Request.ContentType, body.ToArray()));
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/x-protobuf";
            context.Response.ContentLength64 = 0;
            context.Response.Close();
        }
    }
}

/// <summary>Coletor que aceita conexões TCP e nunca responde: simula um destino OTLP pendurado.</summary>
public sealed class HangingCollector : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);

    private readonly ConcurrentBag<TcpClient> clients = [];

    private readonly Task loop;

    public HangingCollector()
    {
        listener.Start();
        Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        loop = Task.Run(AcceptAsync);
    }

    public Uri Endpoint { get; }

    public int Accepted => clients.Count;

    public void Dispose()
    {
        listener.Stop();
        foreach (var client in clients)
        {
            client.Dispose();
        }

        loop.Wait(TimeSpan.FromSeconds(5));
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            try
            {
                clients.Add(await listener.AcceptTcpClientAsync());
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
        }
    }
}
