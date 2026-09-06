using System.Collections.ObjectModel;
using CoreLogEntry = SaturdayPulse.Core.Diagnostics.LogEntry;
using CoreLogLevel = SaturdayPulse.Core.Diagnostics.LogLevel;

namespace SaturdayPulse.Helpers
{
    /// <summary>
    /// In-memory logger for on-device diagnostics.
    /// Accessible from the Debug Log section in Settings.
    /// 
    /// Usage: AppLogger.Log("message")
    ///        AppLogger.Log("message", LogLevel.Error)
    ///
    /// MergeRemote (added alongside the new api/logs endpoint) folds in
    /// server-side entries (GameScorePollingService, etc. — see
    /// ServerLogService/InMemoryLoggerProvider on the Api side) fetched via
    /// UserApiService.GetServerLogsAsync, so both device and server activity
    /// are visible in one place without a second logging call anywhere.
    /// </summary>
    public static class AppLogger
    {
        private static readonly object _lock = new();
        private const int MaxEntries = 500;

        public static ObservableCollection<LogEntry> Entries { get; } = new();

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level     = level,
                Message   = message,
                Source    = "Mobile"
            };

            lock (_lock)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Entries.Insert(0, entry); // newest first
                    while (Entries.Count > MaxEntries)
                        Entries.RemoveAt(Entries.Count - 1);
                });
            }

            // Also write to debug output for when debugger is attached
            System.Diagnostics.Debug.WriteLine($"[{level}] {entry.Timestamp:HH:mm:ss.fff} {message}");
        }

        /// <summary>
        /// Merges server-fetched log entries into Entries, re-sorted newest
        /// first alongside existing on-device entries. Any previously-merged
        /// Source == "Api" entries are removed first, so repeated manual
        /// refreshes replace the last server batch rather than accumulating
        /// duplicates every time the Debug Log section is refreshed.
        /// </summary>
        public static void MergeRemote(IEnumerable<CoreLogEntry> remoteEntries)
        {
            lock (_lock)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    for (var i = Entries.Count - 1; i >= 0; i--)
                    {
                        if (Entries[i].Source == "Api")
                            Entries.RemoveAt(i);
                    }

                    var mapped = remoteEntries.Select(r => new LogEntry
                    {
                        Timestamp = r.Timestamp,
                        Level     = MapLevel(r.Level),
                        Message   = r.Category != null ? $"[{r.Category}] {r.Message}" : r.Message,
                        Source    = "Api"
                    });

                    var merged = Entries
                        .Concat(mapped)
                        .OrderByDescending(e => e.Timestamp)
                        .Take(MaxEntries)
                        .ToList();

                    Entries.Clear();
                    foreach (var entry in merged)
                        Entries.Add(entry);
                });
            }
        }

        private static LogLevel MapLevel(CoreLogLevel level) => level switch
        {
            CoreLogLevel.Warning => LogLevel.Warning,
            CoreLogLevel.Error   => LogLevel.Error,
            _                    => LogLevel.Info
        };

        public static void Clear()
        {
            MainThread.BeginInvokeOnMainThread(() => Entries.Clear());
        }
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level     { get; set; }
        public string   Message   { get; set; } = string.Empty;

        /// <summary>"Mobile" for on-device entries (the default via Log()),
        /// "Api" for entries merged in via MergeRemote. Additive — existing
        /// bindings (Display, LevelColor) are unaffected.</summary>
        public string   Source    { get; set; } = "Mobile";

        public string Display => $"{Timestamp:HH:mm:ss.fff} [{Level}] {Message}";

        public string LevelColor => Level switch
        {
            LogLevel.Error   => "#FF4444",
            LogLevel.Warning => "#FFB344",
            _                => "#AAAAAA"
        };
    }

    public enum LogLevel { Info, Warning, Error }
}
