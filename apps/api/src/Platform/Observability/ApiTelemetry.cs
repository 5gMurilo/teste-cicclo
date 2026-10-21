using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cicclo.Api.Bookings;
using Cicclo.Api.Laundry;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Cicclo.Api.Platform.Observability;

/// <summary>Seção `OpenTelemetry`: instrumentação local ligada por padrão; exportação OTLP somente com endpoint explícito.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    public bool Enabled { get; init; } = true;

    /// <summary>URL base do coletor OTLP HTTP/protobuf (por exemplo http://127.0.0.1:4318/). Ausente: nada é exportado.</summary>
    public string? OtlpEndpoint { get; init; }
}

/// <summary>
/// Logs JSON no único provider console, traces e métricas HTTP nativas do ASP.NET Core e exportação OTLP opcional.
/// A exportação ocorre em background (lotes limitados e timeout finito); falha do coletor nunca chega ao handler.
/// </summary>
public static class ApiTelemetry
{
    public const string ServiceName = "cicclo.api";

    public const int ExportTimeoutMilliseconds = 5_000;

    public const int MaxQueueSize = 2_048;

    public const int MaxExportBatchSize = 512;

    /// <summary>Spans de negócio da compra (booking.create).</summary>
    public const string CreateBookingActivities = "Cicclo.Api.Bookings";

    public static WebApplicationBuilder AddApiTelemetry(this WebApplicationBuilder builder)
    {
        builder.Logging.AddJsonConsole(console =>
        {
            console.IncludeScopes = true;
            console.UseUtcTimestamp = true;
            console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            console.JsonWriterOptions = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        });

        var options = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        var endpoint = ParseEndpoint(options.OtlpEndpoint);
        if (!options.Enabled)
        {
            return builder;
        }

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(ServiceName, serviceVersion: Version)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddSource(CreateBookingActivities))
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddMeter(LaundryMetrics.MeterName, BookingMetrics.MeterName))
            .WithLogging();

        if (endpoint is not null)
        {
            builder.Services.AddHostedService<FlushOnStop>();
            telemetry
                .WithTracing(tracing => tracing.AddOtlpExporter(exporter =>
                {
                    Configure(exporter, endpoint, "v1/traces");
                    exporter.ExportProcessorType = ExportProcessorType.Batch;
                    exporter.BatchExportProcessorOptions.MaxQueueSize = MaxQueueSize;
                    exporter.BatchExportProcessorOptions.MaxExportBatchSize = MaxExportBatchSize;
                }))
                .WithMetrics(metrics => metrics.AddOtlpExporter((exporter, reader) =>
                {
                    Configure(exporter, endpoint, "v1/metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = ExportTimeoutMilliseconds;
                }))
                .WithLogging(logging => logging.AddOtlpExporter((exporter, processor) =>
                {
                    Configure(exporter, endpoint, "v1/logs");
                    processor.ExportProcessorType = ExportProcessorType.Batch;
                    processor.BatchExportProcessorOptions.MaxQueueSize = MaxQueueSize;
                    processor.BatchExportProcessorOptions.MaxExportBatchSize = MaxExportBatchSize;
                }));
        }

        return builder;
    }

    private static string? Version =>
        typeof(ApiTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static Uri? ParseEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
        }

        throw new OptionsValidationException(
            TelemetryOptions.SectionName,
            typeof(TelemetryOptions),
            [$"{TelemetryOptions.SectionName}:{nameof(TelemetryOptions.OtlpEndpoint)} deve ser uma URL absoluta http ou https."]);
    }

    /// <summary>
    /// Envia o último lote dos três sinais quando o host termina de parar. O descarte do container no encerramento não
    /// exportou nada nas medições desta versão; cada flush tem prazo finito e falha do coletor não impede o encerramento.
    /// </summary>
    private sealed class FlushOnStop(TracerProvider tracer, MeterProvider meter, LoggerProvider logger) : IHostedLifecycleService
    {
        public Task StoppedAsync(CancellationToken cancellationToken)
        {
            tracer.ForceFlush(ExportTimeoutMilliseconds);
            meter.ForceFlush(ExportTimeoutMilliseconds);
            logger.ForceFlush(ExportTimeoutMilliseconds);
            return Task.CompletedTask;
        }

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static void Configure(OtlpExporterOptions exporter, Uri endpoint, string signalPath)
    {
        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        exporter.Endpoint = new Uri(endpoint, signalPath);
        exporter.TimeoutMilliseconds = ExportTimeoutMilliseconds;
    }
}
