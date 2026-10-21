using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cicclo.Api.Platform.Observability;
using Cicclo.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Cicclo.Api.Tests.Integration.Observability;

[Collection(PostgresCollection.Name)]
[Trait("Increment", "E01")]
[Trait("Task", "T8")]
public sealed class TelemetryTests(PostgresFixture postgres)
{
    private const string WalletPath = "/api/v1/wallet";

    private const string ServicesPath = "/api/v1/services";

    private const string Endpoint = "OpenTelemetry:OtlpEndpoint";

    private static readonly string[] LowCardinalityHttpTags =
        ["http.request.method", "http.response.status_code", "http.route", "url.scheme", "network.protocol.version", "error.type"];

    [Fact]
    public async Task M01_W3CTraceparentIsContinuedAndLogsShareTheTraceAndResource()
    {
        var database = await SeededAsync();
        var logs = new CapturingLoggerProvider();
        using var spans = new ActivityCapture("Microsoft.AspNetCore");
        await using var factory = Host(database.ConnectionString, logs);
        using var client = factory.CreateClient();
        var traceId = ActivityTraceId.CreateRandom();
        var parentId = ActivitySpanId.CreateRandom();

        using var response = await client.SendAsync(Traced(WalletPath, traceId, parentId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Eventually(() => spans.Stopped.Any(span => span.TraceId == traceId));
        var span = Assert.Single(spans.Stopped, candidate => candidate.TraceId == traceId);
        Assert.Equal(parentId, span.ParentSpanId);
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.Equal("GET /api/v1/wallet", span.DisplayName);
        Assert.Equal(WalletPath, span.GetTagItem("http.route"));
        Assert.Equal(200, Convert.ToInt32(span.GetTagItem("http.response.status_code"), System.Globalization.CultureInfo.InvariantCulture));

        var correlated = logs.Entries.Where(log => log.Scopes.GetValueOrDefault("TraceId") == traceId.ToHexString()).ToList();
        Assert.Contains(correlated, log => log.Category == "Microsoft.AspNetCore.Hosting.Diagnostics");
        Assert.All(correlated, log => Assert.Equal(span.SpanId.ToHexString(), log.Scopes["SpanId"]));

        var resource = factory.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Assert.Equal("cicclo.api", resource["service.name"]);
        Assert.Equal("Development", resource["deployment.environment.name"]);
        var version = typeof(ApiTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(version, resource["service.version"]);
    }

    [Fact]
    public async Task M02_HttpMetricsUseRouteAndStatusWithoutIdentifiers()
    {
        var database = await SeededAsync();
        await using var factory = Host(database.ConnectionString);
        using var client = factory.CreateClient();
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        var measurements = new ConcurrentQueue<IReadOnlyDictionary<string, string?>>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meters) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meterFactory)
                    && instrument.Meter.Name == "Microsoft.AspNetCore.Hosting"
                    && instrument.Name == "http.server.request.duration")
                {
                    meters.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            measurements.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString())));
        listener.Start();
        var identifiers = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };

        foreach (var path in new[] { $"{WalletPath}?walletId={identifiers[0]}", $"{WalletPath}?walletId={identifiers[1]}", $"{ServicesPath}?userId={identifiers[2]}", "/health/ready" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await Eventually(() => measurements.Count >= 4);
        var series = measurements.Select(tags => string.Join(';', tags.OrderBy(tag => tag.Key, StringComparer.Ordinal).Select(tag => $"{tag.Key}={tag.Value}"))).Distinct().ToList();
        Assert.Equal(3, series.Count);
        Assert.Contains(measurements, tags => tags["http.route"] == WalletPath && tags["http.response.status_code"] == "200" && tags["http.request.method"] == "GET");
        Assert.Contains(measurements, tags => tags["http.route"] == ServicesPath && tags["http.response.status_code"] == "200");
        Assert.Contains(measurements, tags => tags["http.route"] == "/health/ready" && tags["http.response.status_code"] == "200");
        Assert.All(measurements, tags => Assert.Subset(LowCardinalityHttpTags.ToHashSet(), tags.Keys.ToHashSet()));
        Assert.All(measurements.SelectMany(tags => tags.Values), value =>
        {
            Assert.DoesNotContain("?", value ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(identifiers, identifier => (value ?? string.Empty).Contains(identifier, StringComparison.OrdinalIgnoreCase));
        });
        Assert.NotNull(factory.Services.GetService<MeterProvider>());
    }

    [Fact]
    public async Task M03_LocalReceiverGetsOtlpLogsTracesAndMetricsOnSignalPaths()
    {
        var database = await SeededAsync();
        using var receiver = OtlpReceiver.Start();
        using var spans = new ActivityCapture("Microsoft.AspNetCore");
        var traceId = ActivityTraceId.CreateRandom();
        var factory = Host(database.ConnectionString, settings: new() { [Endpoint] = receiver.Endpoint.AbsoluteUri });
        using (var client = factory.CreateClient())
        {
            using var response = await client.SendAsync(Traced(WalletPath, traceId, ActivitySpanId.CreateRandom()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // O TestServer devolve a resposta antes de encerrar o span do servidor; o encerramento só começa depois dele.
        await Eventually(() => spans.Stopped.Any(span => span.TraceId == traceId));
        await factory.DisposeAsync();

        var requests = receiver.Requests;
        Assert.Equal(["/v1/logs", "/v1/metrics", "/v1/traces"], requests.Select(request => request.Path).Distinct().Order(StringComparer.Ordinal));
        Assert.All(requests, request =>
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal("application/x-protobuf", request.ContentType);
            Assert.NotEmpty(request.Body);
        });
        var traceBytes = Convert.FromHexString(traceId.ToHexString());
        Assert.Contains(requests, request => request.Path == "/v1/traces" && Has(request.Body, traceBytes) && Has(request.Body, "cicclo.api"));
        Assert.Contains(requests, request => request.Path == "/v1/logs" && Has(request.Body, traceBytes) && Has(request.Body, "cicclo.api"));
        Assert.Contains(requests, request => request.Path == "/v1/metrics" && Has(request.Body, "http.server.request.duration") && Has(request.Body, WalletPath));
    }

    [Fact]
    public async Task M04_UnreachableCollectorDoesNotAffectReadsOrHealth()
    {
        var database = await SeededAsync();
        using var hanging = new HangingCollector();
        var refused = new Uri($"http://127.0.0.1:{OtlpReceiver.FreeLoopbackPort()}/");

        foreach (var endpoint in new[] { hanging.Endpoint, refused })
        {
            await using var factory = Host(database.ConnectionString, settings: new() { [Endpoint] = endpoint.AbsoluteUri });
            using var client = factory.CreateClient();
            await AssertReadsAndHealthAsync(client);
            if (endpoint == hanging.Endpoint)
            {
                // Garante que o exportador já está preso no coletor antes da segunda rodada de leituras.
                await Eventually(() => hanging.Accepted > 0, TimeSpan.FromSeconds(20));
                var watch = Stopwatch.StartNew();
                await AssertReadsAndHealthAsync(client);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"leituras levaram {watch.Elapsed}");
            }
        }
    }

    [Fact]
    public async Task M05_WithoutEndpointNothingIsExportedNorSentToDefaultLocalhostPorts()
    {
        var database = await SeededAsync();
        using var outgoing = new ActivityCapture("System.Net.Http");
        using var receiver = OtlpReceiver.Start();

        var silent = Host(database.ConnectionString);
        using (var client = silent.CreateClient())
        {
            await AssertReadsAndHealthAsync(client);
            Assert.NotNull(silent.Services.GetService<TracerProvider>());
        }

        await silent.DisposeAsync();
        var control = Host(database.ConnectionString, settings: new() { [Endpoint] = receiver.Endpoint.AbsoluteUri });
        using (var client = control.CreateClient())
        {
            await AssertReadsAndHealthAsync(client);
        }

        await control.DisposeAsync();

        // Controle: o mesmo sensor de HttpClient enxerga a exportação quando há endpoint.
        Assert.Contains(outgoing.Stopped, request => Port(request) == receiver.Port);
        Assert.NotEmpty(receiver.Requests);
        Assert.DoesNotContain(outgoing.Stopped, request => Port(request) is 4317 or 4318);
    }

    [Theory]
    [InlineData("localhost:4318")]
    [InlineData("ftp://127.0.0.1/otlp")]
    [InlineData("/v1/traces")]
    public void M05_InvalidEndpointFailsStartupInsteadOfGuessingADestination(string endpoint)
    {
        using var factory = Host(connectionString: null, settings: new() { [Endpoint] = endpoint });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validation = Flatten(error).OfType<OptionsValidationException>().First();
        Assert.Equal(typeof(TelemetryOptions), validation.OptionsType);
        Assert.Contains("OpenTelemetry:OtlpEndpoint", validation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M06_DisabledTelemetryKeepsLoggingAndExportsNothing()
    {
        var database = await SeededAsync();
        using var receiver = OtlpReceiver.Start();
        var logs = new CapturingLoggerProvider();
        var factory = Host(
            database.ConnectionString,
            logs,
            new() { ["OpenTelemetry:Enabled"] = "false", [Endpoint] = receiver.Endpoint.AbsoluteUri });
        using (var client = factory.CreateClient())
        {
            await AssertReadsAndHealthAsync(client);
        }

        Assert.Null(factory.Services.GetService<TracerProvider>());
        Assert.Null(factory.Services.GetService<MeterProvider>());
        Assert.Single(factory.Services.GetServices<ILoggerProvider>().OfType<ConsoleLoggerProvider>());
        Assert.Contains(logs.Entries, log => log.Category == "Microsoft.AspNetCore.Hosting.Diagnostics" && log.Message.Contains(WalletPath, StringComparison.Ordinal));
        await factory.DisposeAsync();

        Assert.Empty(receiver.Requests);
    }

    [Fact]
    public async Task M07_SignalsCarryNoSecretsNorHighCardinalityLabels()
    {
        using var receiver = OtlpReceiver.Start();
        var logs = new CapturingLoggerProvider();
        using var spans = new ActivityCapture("Microsoft.AspNetCore");
        var walletId = Guid.NewGuid().ToString();
        var traceId = ActivityTraceId.CreateRandom();
        var factory = Host(ApiFactory.UnreachableDatabase, logs, new() { [Endpoint] = receiver.Endpoint.AbsoluteUri });
        using (var client = factory.CreateClient())
        {
            using var response = await client.SendAsync(Traced($"{WalletPath}?walletId={walletId}", traceId, ActivitySpanId.CreateRandom()));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        await factory.DisposeAsync();

        const string password = "nao-vazar-down";
        Assert.Contains(logs.Entries, log => log.Level == LogLevel.Warning && log.Exception is not null);
        Assert.All(logs.Entries, log => Assert.DoesNotContain(password, $"{log.Message}{log.Exception}{string.Join(',', log.Scopes.Values)}", StringComparison.Ordinal));
        var span = Assert.Single(spans.Stopped, candidate => candidate.TraceId == traceId);
        Assert.All(span.TagObjects, tag =>
        {
            Assert.DoesNotContain(password, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(walletId, tag.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(receiver.Requests, request => request.Path == "/v1/logs");
        Assert.All(receiver.Requests, request => Assert.False(Has(request.Body, password), $"segredo exportado em {request.Path}"));
        Assert.All(receiver.Requests.Where(request => request.Path is "/v1/metrics" or "/v1/traces"), request =>
            Assert.False(Has(request.Body, walletId), $"identificador exportado em {request.Path}"));
    }

    [Fact]
    public async Task M08_SingleJsonConsoleProviderAndBoundedShutdownWithHangingCollector()
    {
        var database = await SeededAsync();
        using var hanging = new HangingCollector();
        var factory = Host(database.ConnectionString, settings: new() { [Endpoint] = hanging.Endpoint.AbsoluteUri });
        using (var client = factory.CreateClient())
        {
            await AssertReadsAndHealthAsync(client);
        }

        var consoles = factory.Services.GetServices<ILoggerProvider>().OfType<ConsoleLoggerProvider>().ToList();
        Assert.Single(consoles);
        Assert.Equal(ConsoleFormatterNames.Json, factory.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);
        Assert.True(factory.Services.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>().CurrentValue.IncludeScopes);
        await Eventually(() => hanging.Accepted > 0, TimeSpan.FromSeconds(20));

        var watch = Stopwatch.StartNew();
        var shutdown = factory.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.Same(shutdown, finished);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"encerramento levou {watch.Elapsed}");
    }

    private static WebApplicationFactory<Program> Host(
        string? connectionString,
        CapturingLoggerProvider? logs = null,
        Dictionary<string, string>? settings = null) =>
        ApiFactory.Create(connectionString, settings).WithWebHostBuilder(host =>
        {
            if (logs is not null)
            {
                host.ConfigureLogging(logging => logging.AddProvider(logs));
            }
        });

    private async Task<TestDatabase> SeededAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await database.BootstrapAsync();
        return database;
    }

    private static async Task AssertReadsAndHealthAsync(HttpClient client)
    {
        using (var wallet = await client.GetAsync(WalletPath))
        {
            Assert.Equal(HttpStatusCode.OK, wallet.StatusCode);
            using var body = JsonDocument.Parse(await wallet.Content.ReadAsStringAsync());
            Assert.Equal(5000L, body.RootElement.GetProperty("balanceCents").GetInt64());
        }

        using (var services = await client.GetAsync(ServicesPath))
        {
            Assert.Equal(HttpStatusCode.OK, services.StatusCode);
            using var body = JsonDocument.Parse(await services.Content.ReadAsStringAsync());
            Assert.Equal(
                [2090L, 1890L],
                body.RootElement.GetProperty("services").EnumerateArray().Select(item => item.GetProperty("unitPriceCents").GetInt64()));
        }

        foreach (var path in new[] { "/health", "/health/live", "/health/ready" })
        {
            using var health = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
        }
    }

    private static HttpRequestMessage Traced(string path, ActivityTraceId traceId, ActivitySpanId parentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("traceparent", $"00-{traceId.ToHexString()}-{parentId.ToHexString()}-01");
        return request;
    }

    private static bool Has(byte[] body, string text) => Has(body, Encoding.UTF8.GetBytes(text));

    private static bool Has(byte[] body, byte[] needle) => body.AsSpan().IndexOf(needle) >= 0;

    private static int? Port(Activity request)
    {
        if (request.GetTagItem("server.port") is { } port)
        {
            return Convert.ToInt32(port, System.Globalization.CultureInfo.InvariantCulture);
        }

        return Uri.TryCreate(request.GetTagItem("url.full") as string, UriKind.Absolute, out var url) ? url.Port : null;
    }

    private static async Task Eventually(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < (timeout ?? TimeSpan.FromSeconds(5)), "condição não atingida no prazo");
            await Task.Delay(50);
        }
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        var inner = exception is AggregateException aggregate ? aggregate.InnerExceptions : exception.InnerException is { } single ? [single] : [];
        foreach (var child in inner.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    /// <summary>Captura em memória as atividades encerradas de uma ActivitySource do processo.</summary>
    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener listener;

        private readonly ConcurrentQueue<Activity> stopped = new();

        public ActivityCapture(string sourceName)
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(listener);
        }

        public IReadOnlyList<Activity> Stopped => [.. stopped];

        public void Dispose() => listener.Dispose();
    }
}
