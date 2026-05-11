using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core.Tests;

internal sealed class TestLogger : ILogger
{
    public List<TestLogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        Entries.Add(new TestLogEntry(logLevel, eventId, formatter(state, exception), exception));
    }
}

internal sealed record TestLogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
