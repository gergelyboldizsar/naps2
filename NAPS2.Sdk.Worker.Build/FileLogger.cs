#nullable enable

using Microsoft.Extensions.Logging;

namespace NAPS2.Sdk.Worker;

/// <summary>FOPA: an appending file log for the worker, switched on by NAPS2_WORKER_LOG.</summary>
internal class FileLogger(string path) : ILogger
{
    private readonly object _lock = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}" +
                   (exception == null ? "" : Environment.NewLine + exception);
        lock (_lock)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
