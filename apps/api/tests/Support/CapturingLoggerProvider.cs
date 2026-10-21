using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cicclo.Api.Tests.Support;

public sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>Captura logs em memória para provar o que o host registrou internamente.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> entries = new();

    public IReadOnlyList<CapturedLog> Entries => [.. entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception), exception));
    }
}
