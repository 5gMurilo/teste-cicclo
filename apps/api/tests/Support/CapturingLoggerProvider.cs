using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cicclo.Api.Tests.Support;

public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, string?> Scopes);

/// <summary>
/// Captura logs em memória para provar o que o host registrou internamente, incluindo os escopos externos
/// (por exemplo TraceId/SpanId do ActivityTrackingOptions) que os formatters de console recebem.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLog> entries = new();

    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<CapturedLog> Entries => [.. entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries, () => scopes);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries, Func<IExternalScopeProvider> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => scopes().Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            scopes().ForEachScope(
                (scope, collected) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var (key, value) in pairs)
                        {
                            collected[key] = value?.ToString();
                        }
                    }
                },
                values);
            entries.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception), exception, values));
        }
    }
}
