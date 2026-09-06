using Microsoft.Extensions.Logging;
using SaturdayPulse.Services;
using CoreLogLevel = SaturdayPulse.Core.Diagnostics.LogLevel;
using CoreLogEntry = SaturdayPulse.Core.Diagnostics.LogEntry;

namespace SaturdayPulse.Interfaces
{
    /// <summary>
    /// Captures ILogger output from selected categories into
    /// ServerLogService, so background services (GameScorePollingService,
    /// and any future service added to TrackedCategoryPrefixes) show up in
    /// the mobile Debug Log without changing those services' existing
    /// logger.LogX(...) calls at all.
    ///
    /// Only Information level and above is captured — LogDebug calls (e.g.
    /// GameScorePollingService's routine "no games today"/"outside window"
    /// skips) are deliberately excluded to avoid a 5-minute-interval flood
    /// crowding out the 500-entry buffer with routine no-op ticks.
    /// </summary>
    public class InMemoryLoggerProvider(ServerLogService serverLogService) : ILoggerProvider
    {
        // Only categories worth surfacing to the mobile Debug Log go here —
        // add a new prefix as other services need visibility. Full category
        // name is the type's full namespace + name (ILogger<T> convention).
        private static readonly string[] TrackedCategoryPrefixes =
        [
            "SaturdayPulse.Services.GameScorePollingService"
        ];

        public ILogger CreateLogger(string categoryName)
        {
            var tracked = TrackedCategoryPrefixes.Any(p =>
                categoryName.StartsWith(p, StringComparison.Ordinal));

            return tracked
                ? new InMemoryLogger(categoryName, serverLogService)
                : NullLogger.Instance;
        }

        public void Dispose() { }

        private sealed class NullLogger : ILogger
        {
            public static readonly NullLogger Instance = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
                NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            { }
        }

        private sealed class InMemoryLogger(string categoryName, ServerLogService serverLogService) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
                NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var message = formatter(state, exception);
                if (exception != null) message += $" | {exception.Message}";

                serverLogService.Add(new CoreLogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = MapLevel(logLevel),
                    Message = message,
                    Source = "Api",
                    Category = categoryName
                });
            }

            private static CoreLogLevel MapLevel(LogLevel level) => level switch
            {
                LogLevel.Warning => CoreLogLevel.Warning,
                LogLevel.Error or LogLevel.Critical => CoreLogLevel.Error,
                _ => CoreLogLevel.Info
            };
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
