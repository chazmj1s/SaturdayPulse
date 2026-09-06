using System.Collections.Concurrent;
using SaturdayPulse.Core.Diagnostics;

namespace SaturdayPulse.Services
{
    /// <summary>
    /// In-memory ring buffer of recent server-side log entries. Populated by
    /// InMemoryLoggerProvider, which captures ILogger output from selected
    /// categories (see that class's TrackedCategoryPrefixes) without
    /// requiring a second, separate logging call at every existing
    /// logger.LogX(...) site in services like GameScorePollingService.
    ///
    /// Not persisted — resets on app restart/redeploy, same tradeoff Mobile's
    /// AppLogger already accepts for on-device logs. Exposed read-only via
    /// LogsController (Authorize + AdminOnly gated, no shared secret).
    /// Registered as a singleton (see Program.cs) since it must outlive any
    /// single request/scope and be shared across the whole process,
    /// including the GameScorePollingService BackgroundService.
    /// </summary>
    public class ServerLogService
    {
        private const int MaxEntries = 500;
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private readonly object _trimLock = new();

        public void Add(LogEntry entry)
        {
            _entries.Enqueue(entry);

            if (_entries.Count > MaxEntries)
            {
                lock (_trimLock)
                {
                    while (_entries.Count > MaxEntries)
                        _entries.TryDequeue(out _);
                }
            }
        }

        /// <summary>Most recent entries first, capped at <paramref name="take"/>.</summary>
        public IReadOnlyList<LogEntry> GetRecent(int take) =>
            _entries.Reverse().Take(take).ToList();
    }
}
