namespace SaturdayPulse.Core.Diagnostics
{
    /// <summary>
    /// Shared log-entry shape used both by Mobile's on-device AppLogger and
    /// the Api's in-memory server-side log (see ServerLogService). Kept
    /// deliberately minimal/data-only — no MAUI or ASP.NET Core dependency —
    /// since Core is referenced by both Mobile and Api, which cannot share
    /// runtime logging mechanics (MainThread.BeginInvokeOnMainThread doesn't
    /// exist outside MAUI), only this shape.
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;

        /// <summary>"Api" or "Mobile" — set by whichever side produced the
        /// entry, so a merged view (Mobile's Debug Log page) can distinguish
        /// them once Api entries are fetched in.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Originating logger category on the Api side, e.g.
        /// "SaturdayPulse.Services.GameScorePollingService". Null for
        /// Mobile-originated entries.</summary>
        public string? Category { get; set; }
    }

    public enum LogLevel { Info, Warning, Error }
}
