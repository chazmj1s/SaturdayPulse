using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SaturdayPulse.Helpers;
using SaturdayPulse.Models;
using SaturdayPulse.Services;

namespace SaturdayPulse.ViewModels
{
    /// <summary>
    /// Sole owner of year, week, and conference selection for the entire app.
    /// Consumer pages read these from SharedNavigationStateService and refilter
    /// on FilterChanged — they never build the week strip or resolve conferences.
    ///
    /// LoadYearContextAsync is the single place that warms the cache, builds the
    /// week strip, resolves the default week, and resolves the conference for a
    /// year. Both the startup path (InitializeAsync) and the user-driven year
    /// change (ApplyYearChangeAsync) route through it, then fire exactly one
    /// FilterChanged so consumers do a single refilter against warm state.
    ///
    /// Threading: ApplyYearChangeAsync / InitializeAsync are invoked on the main
    /// thread and never use ConfigureAwait(false), so the continuation that
    /// mutates nav state runs on the main thread. InitializeAsync MUST be called
    /// without Task.Run for this to hold (see MainPage).
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly SharedNavigationStateService _navState;
        private readonly GameDataApiService           _apiService;
        private readonly GameDataCacheService         _cache;
        private readonly MyTeamsViewModel              _myTeamsViewModel;
        private readonly ScheduleViewModel             _scheduleViewModel;
        private readonly EntitlementService            _entitlementService;
        private int  _selectedIndex = 0;
        private bool _yearChangeInFlight;   // re-entrancy guard (main-thread only)
        private bool _initialized;

        // My Teams is tab 0 — see MainPage.xaml.cs AddPageToHost order.
        private const int MyTeamsTabIndex = 0;

        // Games is tab 1 — INFERRED from the visible tab-strip order (My
        // Teams, Games, Rankings, Postseason, Sandbox) and the existing
        // MyTeamsTabIndex(0)/SettingsTabIndex(5) consts, not confirmed
        // against MainPage.xaml.cs's actual AddPageToHost calls. Used by
        // TabChangeRequested (My Teams' opponent-name navigation) — if the
        // app lands on the wrong tab, this is the one line to fix.
        private const int GamesTabIndex = 1;

        // Settings still lives at PageHost position 5 (AddPageToHost order is
        // unchanged), it's just no longer one of the entries in TabItems, so
        // the tab strip can't scroll/swipe to it. Only the gear icon
        // (OpenSettingsCommand) and Settings' own Close link
        // (CloseSettingsCommand) reach it now.
        private const int SettingsTabIndex = 5;

        // Where to return to when Settings' Close link is tapped. Captured
        // right before OpenSettingsCommand switches to SettingsTabIndex, so
        // Close always lands back wherever the person actually came from.
        private int _previousIndexBeforeSettings = MyTeamsTabIndex;

        public MainViewModel(
            SharedNavigationStateService navState,
            GameDataApiService apiService,
            GameDataCacheService cache,
            MyTeamsViewModel myTeamsViewModel,
            ScheduleViewModel scheduleViewModel,
            EntitlementService entitlementService)
        {
            _navState         = navState;
            _apiService       = apiService;
            _cache            = cache;
            _myTeamsViewModel = myTeamsViewModel;
            _scheduleViewModel = scheduleViewModel;
            _entitlementService = entitlementService;

            // Admin-only manual refresh (2026-09-05) — reuses ScheduleViewModel's
            // existing RefreshCommand rather than duplicating LoadDataAsync here.
            // Gated to IsAdmin (server-set via SQL only, no client toggle) and to
            // the Games tab via ShowDevForceRefresh — see that property.
            DevForceRefreshCommand = new Microsoft.Maui.Controls.Command(() =>
            {
                _scheduleViewModel.RefreshCommand.Execute(null);
            });

            SelectTabCommand = new Microsoft.Maui.Controls.Command<int>(idx =>
            {
                SelectedIndex = idx;
            });

            NextTabCommand = new Microsoft.Maui.Controls.Command(() =>
            {
                if (SelectedIndex < TabItems.Count - 1) SelectedIndex++;
            });

            PreviousTabCommand = new Microsoft.Maui.Controls.Command(() =>
            {
                if (SelectedIndex > 0) SelectedIndex--;
            });

            // Gear icon in the header (MainPage.xaml row 0). Settings isn't in
            // TabItems anymore, so this is the only tap-driven way in (besides
            // DefaultLandingPage == "Settings" at startup) — bypasses the
            // TabItems.Count bound in Next/PreviousTabCommand on purpose.
            OpenSettingsCommand = new Microsoft.Maui.Controls.Command(() =>
            {
                if (SelectedIndex != SettingsTabIndex)
                    _previousIndexBeforeSettings = SelectedIndex;

                SelectedIndex = SettingsTabIndex;
            });

            // Settings' "Close" link. SettingsViewModel raises CloseRequested;
            // MainPage.xaml.cs subscribes and forwards to this command (cross-
            // page, so it can't be a direct XAML binding — see MainPage.xaml.cs).
            CloseSettingsCommand = new Microsoft.Maui.Controls.Command(() =>
            {
                SelectedIndex = _previousIndexBeforeSettings;
            });

            SelectYearCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var currentYear = DateTime.Now.Year;

                // Year filter gating (2026-07-26): free users pick between
                // the current year and last year — last year's Score/
                // Spread/O-U projections are unlocked too (see
                // ProjectionGateConverter), everything else stays behind
                // the paywall. Entitled users still see the full 1965-
                // present list.
                var years = _entitlementService.HasSeasonPass
                    ? Enumerable.Range(1965, currentYear - 1965 + 1)
                        .Select(y => y.ToString())
                        .Reverse()
                        .ToArray()
                    : new[] { currentYear.ToString(), (currentYear - 1).ToString() };

                var result = await Shell.Current.DisplayActionSheet(
                    "Select Year", "Cancel", null, years);

                if (result == null || result == "Cancel" || !int.TryParse(result, out int year))
                    return;

                await ApplyYearChangeAsync(year);
            });

            SelectWeekCommand = new Microsoft.Maui.Controls.Command<int>(week =>
            {
                AppLogger.Log($"[Week] Selected week={_navState.SelectedWeek} year={_navState.SelectedYear}");

                _navState.SelectedWeek = week;
            });

            // "All" is index 0 of the server list — no injection, no special-case.
            SelectConferenceCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var available = _navState.AvailableConferences;
                if (available.Count == 0) return;

                var result = await Shell.Current.DisplayActionSheet(
                    "Conference", "Cancel", null,
                    available.Select(c => c.Name).ToArray());

                if (result is null or "Cancel") return;

                var picked = available.FirstOrDefault(c => c.Name == result);
                if (picked != null)
                    _navState.SelectedConference = picked.Abbreviation;
            });

            // Wraps SelectConferenceCommand: on My Teams the pill is a
            // read-only display of the selected team's conference for the
            // year (see ConferencePillText below), so tapping it there is a
            // no-op instead of opening the global conference picker.
            ConferencePillTapCommand = new Microsoft.Maui.Controls.Command(() =>
            {
                if (SelectedIndex == MyTeamsTabIndex) return;
                SelectConferenceCommand.Execute(null);
            });

            // Refresh the pill whenever the selected team's ranking changes
            // (team switch, week change, etc.) while My Teams is on-screen.
            _myTeamsViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MyTeamsViewModel.SelectedTeamRanking) &&
                    SelectedIndex == MyTeamsTabIndex)
                {
                    OnPropertyChanged(nameof(ConferencePillText));
                }
            };

            // Forward nav state changes to XAML bindings
            _navState.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(SharedNavigationStateService.SelectedYear):
                        OnPropertyChanged(nameof(SelectedYear));
                        break;
                    case nameof(SharedNavigationStateService.SelectedWeek):
                        OnPropertyChanged(nameof(SelectedWeek));
                        break;
                    case nameof(SharedNavigationStateService.SelectedConference):
                        OnPropertyChanged(nameof(SelectedConference));
                        break;
                    case nameof(SharedNavigationStateService.ShowFavoritesFirst):
                        OnPropertyChanged(nameof(ShowFavoritesFirst));
                        break;
                }
            };

            // Year filter gating (2026-07-26): if a Season Pass lapses
            // (expiry, admin dev-toggle flip) while looking at a year
            // outside the free window (current year or last year), snap
            // back to the current year — SelectYearCommand above only
            // prevents a free user from picking a new out-of-window year,
            // it doesn't by itself undo one they already had access to.
            _entitlementService.EntitlementChanged += OnEntitlementChanged;

            // My Teams' opponent-name navigation (2026-09-05) — MyTeamsViewModel
            // has no reference to this VM, so it asks via SharedNavigationStateService.
            _navState.TabChangeRequested += () => SelectedIndex = GamesTabIndex;
        }

        private async void OnEntitlementChanged()
        {
            OnPropertyChanged(nameof(IsAdmin));
            OnPropertyChanged(nameof(ShowDevForceRefresh));

            if (_entitlementService.HasSeasonPass) return;

            var currentYear = DateTime.Now.Year;
            if (_navState.SelectedYear == currentYear || _navState.SelectedYear == currentYear - 1)
                return;

            await ApplyYearChangeAsync(currentYear);
        }

        // ── Startup ───────────────────────────────────────────────────────

        /// <summary>
        /// Establishes the initial year context once at startup. Call this from
        /// MainPage WITHOUT Task.Run (so the nav-state continuation stays on the
        /// main thread). Fires FilterChanged(Year) so consumer pages render.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized || _yearChangeInFlight) return;
            _yearChangeInFlight = true;
            IsLoading = true;

            try
            {
                var year = _navState.SelectedYear;
                AppLogger.Log($"[Init] start year={year}");

                await LoadYearContextAsync(year);

                // Year equals its default, so the setter won't fire — force it once.
                _navState.RaiseInitialFilterChanged();
                _initialized = true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Init] failed: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
                _yearChangeInFlight = false;
            }
        }

        // ── Year change orchestration ─────────────────────────────────────

        /// <summary>
        /// User explicitly picks a new year. Warms context, then fires one
        /// FilterChanged(Year). Re-taps are ignored while a change is loading.
        /// </summary>
        private async Task ApplyYearChangeAsync(int year)
        {
            if (_yearChangeInFlight) return;
            _yearChangeInFlight = true;
            IsLoading = true;

            try
            {
                AppLogger.Log($"[YearChange] start year={year}");

                await LoadYearContextAsync(year);

                // Single unified rebuild signal — consumers refilter from warm cache.
                _navState.SelectedYear = year;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[YearChange] failed year={year}: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
                _yearChangeInFlight = false;
            }
        }

        /// <summary>
        /// The single owner of year/week/conference setup. Warms the games cache,
        /// builds the week strip, resolves the default week, and resolves the
        /// conference — all before any FilterChanged fires. Does NOT fire it.
        ///
        /// Conferences + games load concurrently. forceReload is gated to the
        /// in-progress season; historical years (immutable) serve from warm cache,
        /// which also keeps GameDataCacheService from firing CacheUpdated and
        /// causing consumers to double-render.
        /// </summary>
        private async Task LoadYearContextAsync(int year)
        {
            bool currentSeason = year >= DateTime.Now.Year;

            // Both calls run concurrently on background threads (fetch + deserialize +
            // conference/tier stamping are CPU-heavy on device, so they stay off the
            // main thread). We publish the conference list the moment IT returns rather
            // than waiting on the multi-MB games fetch — otherwise the conference picker
            // sits empty (and acts disabled) for the whole games cold-start window.
            // Each await resumes on the main thread (this method is invoked on the main
            // thread with no ConfigureAwait(false)), so the nav-state mutations are UI-safe.
            var conferences = await _apiService.GetConferencesForYearAsync(year);
            var gameWeeks = await _apiService.GetPlayedWeeksByYear(year);

            if (conferences != null)
                _navState.SetAvailableConferences(conferences);   // picker ready ASAP


            // ── Remaining nav-state mutation (week strip + defaults) is UI-safe here ──

            var weeks = gameWeeks.Select(g => g.Week).Distinct().OrderBy(w => w).ToList();
            _navState.SetWeeks(weeks);

            _navState.ApplyStartupDefaults(
                gameWeeks,
                g => g.Week,
                g =>
                {
                    if (string.IsNullOrWhiteSpace(g.GameDate)) return null;
                    var dateStr = $"{g.GameDate} {year}";
                    return DateTime.TryParse(dateStr, out var d) ? d : (DateTime?)null;
                });

            var resolved =
                IsConferenceValid(_navState.DefaultConference)  ? _navState.DefaultConference
              : "All";

            _navState.SetConferenceSilent(resolved);
        }

        private bool IsConferenceValid(string abbreviation) =>
            string.Equals(abbreviation, "All", StringComparison.OrdinalIgnoreCase) ||
            _navState.AvailableConferences.Any(c =>
                string.Equals(c.Abbreviation, abbreviation, StringComparison.OrdinalIgnoreCase));

        // ── Tab nav ───────────────────────────────────────────────────────

        public ObservableCollection<TabItem> TabItems { get; } = new();
        public ObservableCollection<object>  Pages    { get; } = new();

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_selectedIndex != value)
                {
                    _selectedIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ConferencePillText));
                    OnPropertyChanged(nameof(IsConferencePillInteractive));
                    OnPropertyChanged(nameof(ShowDevForceRefresh));
                }
            }
        }

        /// <summary>
        /// Sets the initial tab without going through the SelectedIndex
        /// setter's change-notification path — MainPage.xaml.cs calls
        /// SyncTabItems/SyncPage explicitly right after this during startup,
        /// so a duplicate notification-driven SyncPage call isn't needed
        /// (and would race InitializeAsync's own FilterChanged warm-up).
        /// </summary>
        public void SetInitialTabIndex(int index) => _selectedIndex = index;

        public ICommand SelectTabCommand     { get; }
        public ICommand NextTabCommand       { get; }
        public ICommand PreviousTabCommand   { get; }
        public ICommand OpenSettingsCommand  { get; }
        public ICommand CloseSettingsCommand { get; }

        // ── Shared navigation proxy properties ────────────────────────────

        public int    SelectedYear       => _navState.SelectedYear;
        public int    SelectedWeek       => _navState.SelectedWeek;
        public string SelectedConference => _navState.SelectedConference;
        public bool   ShowFavoritesFirst => _navState.ShowFavoritesFirst;

        /// <summary>Server-set via SQL only (see EntitlementService) — no client-side toggle.</summary>
        public bool IsAdmin => _entitlementService.IsAdmin;

        /// <summary>
        /// Admin-only manual refresh control shown next to the Conference
        /// filter pill, visible only while on the Games/Schedule tab.
        /// </summary>
        public bool ShowDevForceRefresh => IsAdmin;

        public ICommand DevForceRefreshCommand { get; }

        /// <summary>
        /// On every tab except My Teams: the global conference filter
        /// (SelectedConference), unchanged behavior. On My Teams: a
        /// read-only display of the selected team's conference for the
        /// selected year, sourced from the shared MyTeamsViewModel instance.
        /// </summary>
        public string ConferencePillText =>
            SelectedIndex == MyTeamsTabIndex
                ? _myTeamsViewModel.SelectedTeamRanking?.DisplayConferenceTier
                  ?? "No team selected"
                : _navState.SelectedConference;

        public bool IsConferencePillInteractive => SelectedIndex != MyTeamsTabIndex;

        public ICommand ConferencePillTapCommand { get; }

        public ObservableCollection<WeekItem> Weeks => _navState.Weeks;

        public ICommand SelectYearCommand       { get; }
        public ICommand SelectWeekCommand       { get; }
        public ICommand SelectConferenceCommand { get; }

        public SharedNavigationStateService NavState => _navState;

        // ── Loading state ─────────────────────────────────────────────────

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                }
            }
        }

        // ── INotifyPropertyChanged ────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── Tab item ──────────────────────────────────────────────────────────

    public class TabItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Label { get; init; } = string.Empty;
        public int    Index { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
