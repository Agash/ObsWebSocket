using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;

namespace ObsWebSocket.Example;

/// <summary>
/// Records every payload the client could not deserialize.
/// </summary>
/// <remarks>
/// A shape mismatch between a generated record and what OBS actually sends does not fail a check
/// on its own: a request whose response cannot be read throws where the caller can see it, but an
/// event that cannot be read is dropped on purpose so one unmodellable event cannot take the
/// connection down. That is the right behaviour and the wrong thing to be quiet about during
/// validation, so the run collects them and reports them as a failure of its own. Three stub
/// mismatches were found this way, each of which had silently disabled an event for every user.
/// </remarks>
internal sealed class SerializationFailureSink : ILoggerProvider
{
    private static readonly ConcurrentQueue<string> s_failures = new();

    /// <summary>Failures recorded since the last <see cref="Reset"/>.</summary>
    public static IReadOnlyCollection<string> Failures => [.. s_failures];

    /// <summary>Clears the record, so each transport is judged on its own run.</summary>
    public static void Reset() => s_failures.Clear();

    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("ObsWebSocket.Core.Serialization.", StringComparison.Ordinal)
            ? new FailureLogger(categoryName)
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() { }

    private sealed class FailureLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel < LogLevel.Error || exception is not ObsWebSocketSerializationException)
            {
                return;
            }

            string reason = exception.InnerException?.Message ?? exception.Message;
            s_failures.Enqueue($"{category.Split('.')[^1]}: {reason}");
        }
    }
}
