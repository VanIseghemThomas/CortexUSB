using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace OpenCortex.CortexUSB.Client
{
    internal sealed class NullScope : IDisposable { public static NullScope Instance { get; } = new NullScope(); public void Dispose() { } }

    // Minimal console logger to avoid external dependencies in tests.
    // Implements the generic ILogger<T> so callers can request a typed logger.
    public class SimpleConsoleLogger<T> : ILogger<T>
    {
        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        // Generic implementation left for compatibility but not used by interface dispatch
        [ExcludeFromCodeCoverage]
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                string msg = formatter(state, exception);
                string time = DateTime.UtcNow.ToString("o");
                if (exception != null)
                    Console.WriteLine($"[{time}] {typeof(T).Name} {logLevel}: {msg} - {exception}");
                else
                    Console.WriteLine($"[{time}] {typeof(T).Name} {logLevel}: {msg}");
            }
            catch
            {
                // swallow logging errors
            }
        }
    }
}
