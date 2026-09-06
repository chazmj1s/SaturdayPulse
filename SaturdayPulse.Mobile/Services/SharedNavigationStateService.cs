using SaturdayPulse.Models;
using SaturdayPulse.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SaturdayPulse.Services
{
    /// <summary>
    /// Single source of truth for year, week, conference selection, and user
    /// display preferences across all tabs. Registered as Singleton.
    ///
    /// Ownership: MainViewModel is the SOLE driver of year / week / conference.
    /// It mutates this state during year-change orchestration, then fires one
    /// FilterChanged. Consumer pages (Schedule, Rankings, etc.) only READ from
    /// here and refilter on FilterChanged — they never set week/conference or
    /// build the week strip.
    ///
    /// The week/conference mutators below assume they are called on the main
    /// thread, which MainViewModel guarantees (its year-change continuation
    /// runs on the main thread).
    /// </summary>
    public class SharedNavigationStateService : INotifyPropertyChanged
    {
        private int    _selectedYear       = 2026;
        private int    _selectedWeek       = 1;
        private string _selectedConference = "All";
        private bool   _showFavoritesFirst = Preferences.Get("ShowFavoritesFirst", false);
        private string _defaultWeek        = Preferences.Get("DefaultWeek", "Current");
        private string _defaultConference  = Preferences.Get("DefaultConference", "All");
        private readonly ObservableCollection<ConferenceInfo> _availableConferences = new();

        // ── Filter change reason ──────────────────────────────────────────

        public FilterChangeReason LastFilterChange { get; private set; } = FilterChangeReason.Year;

        private void FireFilterChanged(FilterChangeReason reason)
        {
            var caller = new System.Diagnostics.StackFrame(2, false).GetMethod()?.Name ?? "unknown";
            System.Diagnostics.Debug.WriteLine($"[FilterChanged] reason={reason} caller={caller} isMain={MainThread.IsMainThread}");

            LastFilterChange = reason;
            OnPropertyChanged("FilterChanged");
        }

        /// <summary>
        /// Fires FilterChanged(Year) unconditionally. Used by MainViewModel.InitializeAsync
        /// at startup, where SelectedYear equals its default so the property setter
        /// would not fire on its own.
        /// </summary>
        public void RaiseInitialFilterChanged() => FireFilterChanged(FilterChangeReason.Year);

        // ── Year ──────────────────────────────────────────────────────────

        /// <summary>
        /// Set by MainViewModel after warming the cache and settling week/conference.
        /// Fires FilterChanged(Year). Do not set from consumer ViewModels.
        /// </summary>
        public int SelectedYear
        {
            get => _selectedYear;
            set
            {
                if (_selectedYear != value)
                {
                    _selectedYear = value;
                    OnPropertyChanged();
                    FireFilterChanged(FilterChangeReason.Year);
                }
            }
        }

        // ── Week ──────────────────────────────────────────────────────────

        /// <summary>
        /// User-driven week selection from the MainView week strip. Fires
        /// FilterChanged(Week). Consumer pages read SelectedWeek but never set it.
        /// </summary>
        public int SelectedWeek
        {
            get => _selectedWeek;
            set
            {
                if (_selectedWeek != value)
                {
                    _selectedWeek = value;
                    OnPropertyChanged();
                    SyncWeekItems();
                    FireFilterChanged(FilterChangeReason.Week);
                }
            }
        }

        /// <summary>
        /// Sets the week without firing FilterChanged — used during year-change
        /// orchestration so the unified FilterChanged(Year) is the only signal.
        /// Main thread only.
        /// </summary>
        private void SetWeekSilent(int week)
        {
            _selectedWeek = week;
            OnPropertyChanged(nameof(SelectedWeek));
            SyncWeekItems();
        }

        // ── Conference ────────────────────────────────────────────────────

        /// <summary>
        /// Stores the conference Abbreviation ("SEC", "SWC", "All"). User-driven
        /// selection fires FilterChanged(Conference). Consumer pages read only.
        /// </summary>
        public string SelectedConference
        {
            get => _selectedConference;
            set
            {
                if (_selectedConference != value)
                {
                    _selectedConference = value;
                    OnPropertyChanged();
                    FireFilterChanged(FilterChangeReason.Conference);
                }
            }
        }

        // ── Available conferences ─────────────────────────────────────────

        public ObservableCollection<ConferenceInfo> AvailableConferences => _availableConferences;

        /// <summary>
        /// Replaces the available conference list for the current year.
        /// Pure list replacement — does NOT validate or reset SelectedConference.
        /// Conference resolution is owned by MainViewModel.
        /// Main thread only (MainViewModel calls this from its main-thread continuation).
        /// </summary>
        public void SetAvailableConferences(IEnumerable<ConferenceInfo> conferences)
        {
            _availableConferences.Clear();
            foreach (var c in conferences)
                _availableConferences.Add(c);
        }

        /// <summary>
        /// Sets SelectedConference without firing FilterChanged. Used by
        /// MainViewModel during year-change orchestration. Still raises the
        /// property notification so the header label updates.
        /// </summary>
        public void SetConferenceSilent(string abbreviation)
        {
            if (_selectedConference == abbreviation) return;
            _selectedConference = abbreviation;
            OnPropertyChanged(nameof(SelectedConference));
        }

        // ── Show Favorites First ──────────────────────────────────────────

        public bool ShowFavoritesFirst
        {
            get => _showFavoritesFirst;
            set
            {
                if (_showFavoritesFirst != value)
                {
                    _showFavoritesFirst = value;
                    Preferences.Set("ShowFavoritesFirst", value);
                    OnPropertyChanged();
                    FireFilterChanged(FilterChangeReason.Conference); // local refilter only
                }
            }
        }

        // ── Default Week preference ───────────────────────────────────────

        public string DefaultWeek
        {
            get => _defaultWeek;
            set
            {
                if (_defaultWeek == value) return;
                _defaultWeek = value;
                Preferences.Set("DefaultWeek", value);
                OnPropertyChanged();
            }
        }

        // ── Default Conference preference ─────────────────────────────────

        public string DefaultConference
        {
            get => _defaultConference;
            set
            {
                if (_defaultConference == value) return;
                _defaultConference = value;
                Preferences.Set("DefaultConference", value);
                OnPropertyChanged();
                SelectedConference = value;  // fires FilterChanged(Conference)
            }
        }

        // ── Week items ────────────────────────────────────────────────────

        public ObservableCollection<WeekItem> Weeks { get; } = new();

        public void SetWeeks(IEnumerable<int> weekNumbers)
        {
            var weekList = weekNumbers.ToList(); // materialize before crossing threads
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Weeks.Clear();
                foreach (var w in weekList)
                    Weeks.Add(new WeekItem { Week = w, IsSelected = w == _selectedWeek });
            });
        }

        public void SyncWeekItems()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var w in Weeks)
                    w.IsSelected = w.Week == _selectedWeek;
            });
        }

        // ── Startup / year-change default week ────────────────────────────

        /// <summary>
        /// Resolves and applies the default week for a freshly-loaded year, per
        /// the DefaultWeek preference, WITHOUT firing FilterChanged (MainViewModel
        /// fires it once after all state settles). Main thread only.
        ///
        /// Dates are projected once (getDate runs a string concat + parse per game),
        /// then reused across both the season-start and current-week passes.
        /// </summary>
        public void ApplyStartupDefaults<T>(
            IEnumerable<T> schedule,
            Func<T, int>       getWeek,
            Func<T, DateTime?> getDate)
        {
            if (_defaultWeek == "Week1")
            {
                SetWeekSilent(1);
                return;
            }

            // Single parse pass: (week, date) per game.
            var dated = schedule
                .Select(g => (Week: getWeek(g), Date: getDate(g)?.Date))
                .ToList();

            var seasonStart = dated
                .Where(x => x.Date.HasValue)
                .Select(x => x.Date!.Value)
                .DefaultIfEmpty(DateTime.MaxValue)
                .Min();

            var today = DateTime.Today;
            if (today < seasonStart)
            {
                SetWeekSilent(1);
                return;
            }

            // Highest week that has at least one game on or before today.
            var currentWeek = dated
                .Where(x => x.Date.HasValue && x.Date.Value <= today)
                .Select(x => x.Week)
                .DefaultIfEmpty(1)
                .Max();

            SetWeekSilent(currentWeek);
        }

        // ── Cross-tab game navigation (2026-09-05) ─────────────────────────
        // My Teams' opponent-name tap needs to (a) switch to the Games tab
        // and (b) tell Games which game to scroll to/highlight once it's
        // filtered into view. Neither MyTeamsViewModel nor ScheduleViewModel
        // reference MainViewModel or each other, so both signals route
        // through here — same shape as FilterChanged/EntitlementChanged
        // elsewhere in this app.
        //
        // TabChangeRequested: MainViewModel is the sole owner of tab
        // selection (SelectedIndex) and subscribes to this to switch tabs.
        //
        // GameHighlightRequested: ScheduleViewModel subscribes and searches
        // its already-filtered Games for a match. Callers should set
        // SelectedConference/SelectedWeek BEFORE raising this — those
        // setters each queue their own ApplyFiltersAndSort via
        // OnNavStateChanged (MainThread.BeginInvokeOnMainThread), and
        // ScheduleViewModel's handler queues itself the same way so it
        // runs after them (same-thread FIFO order) rather than racing
        // ahead and having its ScrollTo undone by their CollectionView Reset.

        public event Action? TabChangeRequested;
        public void RequestTabChange() => TabChangeRequested?.Invoke();

        public event Action<int>? GameHighlightRequested;
        public void RequestGameHighlight(int gameId) => GameHighlightRequested?.Invoke(gameId);

        // ── INotifyPropertyChanged ────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── Filter change reason ──────────────────────────────────────────────

    public enum FilterChangeReason
    {
        Year,           // year changed — ViewModels may need server call
        Week,           // week changed — Rankings needs server call, others refilter
        Conference      // conference/favorites changed — all ViewModels refilter only
    }
}
