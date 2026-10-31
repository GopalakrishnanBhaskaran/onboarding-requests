using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PmoReview.Tests.Support;

/// <summary>Keeps every log line (formatted message, structured values and exception text) so a test can scan them.</summary>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    /// <summary>All lines as one text.</summary>
    public string Text => string.Join("\n", Lines);

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(" ", kv.Select(p => $"{p.Key}={p.Value}")) : "";
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} | {values} | {exception}");
        }
    }
}
