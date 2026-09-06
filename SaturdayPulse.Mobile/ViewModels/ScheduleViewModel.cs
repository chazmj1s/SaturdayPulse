using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using SaturdayPulse.Helpers;
using SaturdayPulse.Models;
using SaturdayPulse.Services;

namespace SaturdayPulse.ViewModels
{
    public class ScheduleViewModel : BaseViewModel
    {
        private readonly GameDataCacheService         _cache;
        private readonly SharedNavigationStateService _navState;
        private readonly PersonalGameService          _personalGameService;
        private readonly EntitlementService           _entitlementService;

        private ObservableRangeCollection<GameResult> _games = new();
        private bool   _isBusy;
        private string _activeFilter   = "All";
        private string _selectedFilter = "All";
        private string _statusMessage  = "Loading...";
        private string _emptyMessage   = "Loading...";

        public ScheduleViewModel(
            GameDataCacheService cache,
            FollowService followService,
            SharedNavigationStateService navState,
            PersonalGameService personalGameService,
            EntitlementService entitlementService)
            : base(followService)
        {
            _cache               = cache;
            _navState            = navState;
            _personalGameService = personalGameService;
            _entitlementService  = entitlementService;

            // No outer Task.Run — LoadDataAsync runs on the main thread; the cache
            // fetch inside it is offloaded via Task.Run and the continuation
            // (ApplyFiltersAndSort) returns to the main thread.
            LoadDataCommand = new Microsoft.Maui.Controls.Command(() => _ = LoadDataAsync());
            RefreshCommand  = new Microsoft.Maui.Controls.Command(() => _ = LoadDataAsync(forceReload: true));

            SelectFilterCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var options = new List<string> { "All", "P4", "G5" };
                var result  = await Shell.Current.DisplayActionSheet(
                    "Filter", "Cancel", null, options.ToArray());
                if (result != null && result != "Cancel")
                {
                    _activeFilter  = result;
                    SelectedFilter = result;
                    ApplyFiltersAndSort();
                }
            });

            PreviousWeekCommand = new Microsoft.Maui.Controls.Command(() =>
            {
                var idx = _navState.Weeks.ToList().FindIndex(w => w.Week == _navState.SelectedWeek);
                if (idx > 0) _navState.SelectedWeek = _navState.Weeks[idx - 1].Week;
            });

            NextWeekCommand = new Microsoft.Maui.Controls.Command(() =>
            {
                var idx = _navState.Weeks.ToList().FindIndex(w => w.Week == _navState.SelectedWeek);
                if (idx < _navState.Weeks.Count - 1)
                    _navState.SelectedWeek = _navState.Weeks[idx + 1].Week;
            });

            TogglePersonalGameCommand = new Microsoft.Maui.Controls.Command<GameResult>(game =>
            {
                if (game == null) return;
                _personalGameService.Toggle(game.AwayId, game.HomeId);
                game.IsGameFavorited = _personalGameService.IsFavorited(game.AwayId, game.HomeId);
            });

            ToggleDetailsCommand = new Microsoft.Maui.Controls.Command<GameResult>(game =>
            {
                if (game == null) return;
                game.IsDetailsExpanded = !game.IsDetailsExpanded;
            });

            // Mirrors ToggleDetailsCommand exactly — separate expand state, same
            // shape. Visibility (entitlement + data) is decided in XAML via
            // RivalryNotesVisibilityConverter, not here.
            ToggleRivalryNotesCommand = new Microsoft.Maui.Controls.Command<GameResult>(game =>
            {
                if (game == null) return;
                game.IsRivalryNotesExpanded = !game.IsRivalryNotesExpanded;
            });

            // Gated Details paywall message (2026-07-25) — same shared
            // login-check as MyTeamsViewModel/SettingsViewModel. The Details
            // section itself stays open for everyone (per design); only the
            // Vegas/projections portion inside it is replaced with this
            // paywall message for free users.
            SeasonPassCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var result = await _entitlementService.EnsureLoggedInForPurchaseAsync();
                if (!result.CanProceed) return;

                await Shell.Current.DisplayAlert(
                    "Season Pass", "Coming soon — payment isn't wired up yet.", "OK");
            });

            _navState.PropertyChanged += OnNavStateChanged;
            _cache.CacheUpdated       += OnCacheUpdated;
            _entitlementService.EntitlementChanged += OnEntitlementChanged;
            _navState.GameHighlightRequested += OnGameHighlightRequested;
        }

        /// <summary>My Teams' opponent-name navigation (2026-09-05). Deferred
        /// via BeginInvokeOnMainThread rather than run synchronously — the
        /// Conference/Week changes the caller just made each queue their own
        /// ApplyFiltersAndSort via OnNavStateChanged, and those ReplaceRange
        /// calls fire a CollectionView Reset that snaps scroll position back
        /// to the top. Queuing this after them (same-thread FIFO order)
        /// means the scroll-to happens last and actually sticks.</summary>
        private void OnGameHighlightRequested(int gameId)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ApplyFiltersAndSort();

                var match = Games.FirstOrDefault(g => g.Id == gameId);
                if (match == null) return;

                foreach (var g in Games) g.IsHighlighted = false;
                match.IsHighlighted = true;
                ScrollToGameRequested?.Invoke(match);
            });
        }

        /// <summary>Raised once the target game is found and flagged — SchedulePage.xaml.cs scrolls to it.</summary>
        public event Action<GameResult>? ScrollToGameRequested;

        private void OnEntitlementChanged()
        {
            OnPropertyChanged(nameof(HasSeasonPass));
            OnPropertyChanged(nameof(IsNotSeasonPass));
        }

        // ── Bindable collections ──────────────────────────────────────────

        /// <summary>
        /// ObservableRangeCollection fires a single Reset notification on
        /// ReplaceRange instead of one per item — significantly faster
        /// CollectionView re-renders on week/conference/filter changes.
        /// </summary>
        public ObservableRangeCollection<GameResult> Games
        {
            get => _games;
            private set { _games = value; OnPropertyChanged(); }
        }

        // ── Bindable properties ───────────────────────────────────────────

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoading)); }
        }

        public bool   IsLoading => _isBusy;
        public bool   HasLoaded { get; set; }

        // ── Season Pass gating (2026-07-25) ─────────────────────────────
        // Sourced from the shared EntitlementService. Schedule has no
        // ranking toggle bar (that's MyTeams/Rankings' concern) — this
        // only gates the Vegas/projections portion of the Details paywall
        // message in SchedulePage.xaml.
        public bool HasSeasonPass => _entitlementService.HasSeasonPass;

        /// <summary>Inverse of HasSeasonPass — no inverse-bool converter needed in XAML.</summary>
        public bool IsNotSeasonPass => !HasSeasonPass;

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }
        public string EmptyMessage
        {
            get => _emptyMessage;
            set { _emptyMessage = value; OnPropertyChanged(); }
        }
        public string SelectedFilter
        {
            get => _selectedFilter;
            set { _selectedFilter = value; OnPropertyChanged(); }
        }

        // ── Commands ──────────────────────────────────────────────────────

        public ICommand LoadDataCommand           { get; }
        public ICommand RefreshCommand            { get; }
        public ICommand SelectFilterCommand       { get; }
        public ICommand PreviousWeekCommand       { get; }
        public ICommand NextWeekCommand           { get; }
        public ICommand TogglePersonalGameCommand { get; }
        public ICommand ToggleDetailsCommand      { get; }
        public ICommand ToggleRivalryNotesCommand { get; }
        public ICommand SeasonPassCommand         { get; }

        // ── Load ──────────────────────────────────────────────────────────

        public async Task LoadDataAsync(bool forceReload = false)
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusMessage = "Loading...";
            EmptyMessage  = "Loading...";

            try
            {
                var games = await Task.Run(() => _cache.GetGamesForYearAsync(_navState.SelectedYear, forceReload));
                if (games == null || games.Count == 0)
                {
                    StatusMessage = "No games found";
                    EmptyMessage  = "No games found";
                    return;
                }

                ApplyFiltersAndSort();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                EmptyMessage  = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Filter / sort ─────────────────────────────────────────────────

        private void ApplyFiltersAndSort()
        {
            if (_cache.AllGames.Count == 0)
            {
                _games.ReplaceRange(Enumerable.Empty<GameResult>());
                StatusMessage = "No games found";
                EmptyMessage  = "No games found";
                return;
            }

            IEnumerable<GameResult> filtered = _cache.AllGames;

            filtered = filtered.Where(g => g.Week == _navState.SelectedWeek);

            var conf = _navState.SelectedConference;
            if (conf != "All")
            {
                filtered = filtered.Where(g =>
                    g.HomeConf.Equals(conf, StringComparison.OrdinalIgnoreCase) ||
                    g.AwayConf.Equals(conf, StringComparison.OrdinalIgnoreCase));
            }

            filtered = _activeFilter switch
            {
                "Favorites" => filtered.Where(g => g.IsGameFavorited),
                "Followed"  => filtered.Where(g => g.HomeIsFollowed || g.VisitorIsFollowed),
                "P4"        => filtered.Where(g => g.HomeTier == "P4" || g.AwayTier == "P4"),
                "G5"        => filtered.Where(g => g.HomeTier == "G5" || g.AwayTier == "G5"),
                _           => filtered
            };

            List<GameResult> sorted;
            if (_navState.ShowFavoritesFirst)
            {
                sorted = filtered
                    .OrderByDescending(g => g.IsGameFavorited)
                    .ThenByDescending(g => g.HomeIsFollowed || g.VisitorIsFollowed)
                    .ThenBy(g => g.IsFinal)
                    .ThenBy(g => g.SequenceNumber)
                    .ToList();
            }
            else
            {
                sorted = filtered
                    .OrderBy(g => g.IsFinal)
                    .ThenBy(g => g.SequenceNumber)
                    .ToList();
            }

            string? lastHeader = null;
            foreach (var g in sorted)
            {
                g.ShowGroupHeader = g.GroupHeader != lastHeader;
                lastHeader = g.GroupHeader;
            }

            // ReplaceRange fires single Reset notification — much faster than
            // replacing the entire ObservableCollection reference
            _games.ReplaceRange(sorted);

            StatusMessage = "( ) = projected value";
            EmptyMessage  = "No games for selected filter";
            HasLoaded     = true;
        }

        // ── Event handlers ────────────────────────────────────────────────

        private async void OnNavStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "FilterChanged") return;
            System.Diagnostics.Debug.WriteLine($"[Schedule] FilterChanged reason={_navState.LastFilterChange} isMain={MainThread.IsMainThread}");

            switch (_navState.LastFilterChange)
            {
                case FilterChangeReason.Year:
                    // New year — Main built the week strip from a lightweight query,
                    // but the full game data is Schedule's responsibility. Fetch it.
                    await LoadDataAsync();
                    break;

                case FilterChangeReason.Week:
                case FilterChangeReason.Conference:
                    // The year's games are already cached — just refilter.
                    MainThread.BeginInvokeOnMainThread(ApplyFiltersAndSort);
                    break;
            }
        }

        private void OnCacheUpdated()
        {
            // Only refilter after initial load — avoids double render on startup
            if (!HasLoaded) return;
            MainThread.BeginInvokeOnMainThread(ApplyFiltersAndSort);
        }
    }

    // ── Week selector item ────────────────────────────────────────────────

    public class WeekItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public int    Week  { get; init; }
        public string Label => $"Wk{Week}";

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
