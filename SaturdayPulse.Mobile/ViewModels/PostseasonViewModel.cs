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
    /// Drives the Postseason page — Title Games, Playoffs, and Bowls tabs.
    /// Title Games come from the championship qualifiers endpoint.
    /// Playoffs and Bowls are filtered out of the shared schedule cache.
    /// </summary>
    public class PostseasonViewModel : BaseViewModel
    {
        private readonly GameDataApiService           _apiService;
        private readonly GameDataCacheService         _cache;
        private readonly SharedNavigationStateService _navState;

        private List<ChampionshipMatchup> _allChampionships = new();
        private bool   _isBusy;
        private string _selectedView  = "Championship";
        private string _statusMessage = "Loading...";
        private string _emptyMessage = "Loading...";

        public PostseasonViewModel(
            GameDataApiService apiService,
            GameDataCacheService cache,
            FollowService followService,
            SharedNavigationStateService navState)
            : base(followService)
        {
            _apiService = apiService;
            _cache      = cache;
            _navState   = navState;

            // No outer Task.Run — LoadDataAsync runs on the main thread; the HTTP
            // calls inside it are offloaded via their own Task.Run, and the
            // continuations (ApplyConferenceFilter / RebuildPostseasonFromCache)
            // return to the main thread.
            LoadDataCommand = new Microsoft.Maui.Controls.Command(() => _ = LoadDataAsync());
            RefreshCommand  = new Microsoft.Maui.Controls.Command(() => _ = LoadDataAsync(forceReload: true));

            SelectViewCommand = new Microsoft.Maui.Controls.Command<string>(view =>
            {
                SelectedView = view;
            });

            ToggleMatchupExpandCommand = new Microsoft.Maui.Controls.Command<ChampionshipMatchup>(matchup =>
            {
                if (matchup != null) matchup.IsExpanded = !matchup.IsExpanded;
            });

            ToggleContendersExpandCommand = new Microsoft.Maui.Controls.Command<ChampionshipMatchup>(matchup =>
            {
                if (matchup != null) matchup.IsContendersExpanded = !matchup.IsContendersExpanded;
            });

            ToggleDetailsCommand = new Microsoft.Maui.Controls.Command<GameResult>(game =>
            {
                if (game == null) return;
                game.IsDetailsExpanded = !game.IsDetailsExpanded;
            });

            // Section collapse toggles
            ToggleRoundExpandCommand = new Microsoft.Maui.Controls.Command<PlayoffRound>(round =>
            {
                if (round != null) round.IsExpanded = !round.IsExpanded;
            });

            ToggleWeekendExpandCommand = new Microsoft.Maui.Controls.Command<BowlWeekendGroup>(weekend =>
            {
                if (weekend != null) weekend.IsExpanded = !weekend.IsExpanded;
            });

            _navState.PropertyChanged += OnNavStateChanged;
            _cache.CacheUpdated       += OnCacheUpdated;
        }

        // ── Bindable collections ──────────────────────────────────────────

        public ObservableCollection<ChampionshipMatchup> Championships { get; } = new();
        public ObservableCollection<PlayoffRound>        PlayoffRounds { get; } = new();
        public ObservableCollection<BowlWeekendGroup>    BowlWeekends  { get; } = new();

        // ── Bindable properties ───────────────────────────────────────────

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoading)); }
        }

        public bool IsLoading       => _isBusy;
        public bool HasLoaded       { get; set; }

        /// <summary>
        /// True only while Postseason is the visible tab (set by MainPage on tab
        /// switch). When false, the page defers FilterChanged work instead of
        /// loading off-screen; the lazy SyncPage path loads it on first visit.
        /// </summary>
        public bool IsActive        { get; set; }
        public bool HasPlayoffData  => PlayoffRounds.Any();
        public bool HasBowlData     => BowlWeekends.Any();

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

        public string SelectedView
        {
            get => _selectedView;
            set
            {
                _selectedView = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsChampionshipView));
                OnPropertyChanged(nameof(IsPlayoffsView));
                OnPropertyChanged(nameof(IsBowlsView));
            }
        }

        public bool IsChampionshipView => _selectedView == "Championship";
        public bool IsPlayoffsView     => _selectedView == "Playoffs";
        public bool IsBowlsView        => _selectedView == "Bowls";

        // ── Commands ──────────────────────────────────────────────────────

        public ICommand LoadDataCommand               { get; }
        public ICommand RefreshCommand                { get; }
        public ICommand SelectViewCommand             { get; }
        public ICommand ToggleMatchupExpandCommand    { get; }
        public ICommand ToggleContendersExpandCommand { get; }
        public ICommand ToggleDetailsCommand          { get; }
        public ICommand ToggleRoundExpandCommand      { get; }
        public ICommand ToggleWeekendExpandCommand    { get; }

        // ── Load ──────────────────────────────────────────────────────────

        public async Task LoadDataAsync(bool forceReload = false)
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusMessage = "Loading...";

            try
            {
                if (_navState.SelectedYear >= 2016)
                {
                    var championships = await Task.Run(async () =>
                        await _apiService.GetProjectedChampionshipQualifiersAsync(
                            _navState.SelectedYear, _navState.SelectedWeek));
                    if (championships != null)
                    {
                        _allChampionships = championships;
                        ApplyConferenceFilter();
                    }
                }
                else
                {
                    _allChampionships.Clear();
                    Championships.Clear();
                }

                await Task.Run(async () =>
                    await _cache.GetGamesForYearAsync(_navState.SelectedYear, forceReload));

                RebuildPostseasonFromCache();

                StatusMessage = $"{_navState.SelectedYear} projections";
                HasLoaded = true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load game data. Error: {ex.Message}";
                EmptyMessage = "Failed to load game data.";
            }
            finally
            {
                IsBusy = false;
            }
        }
        // ── Build Bowls + Playoffs from cached schedule ───────────────────

        private void RebuildPostseasonFromCache()
        {
            var allGames = _cache.AllGames;
            if (allGames == null || allGames.Count == 0)
            {
                PlayoffRounds.Clear();
                BowlWeekends.Clear();
                OnPropertyChanged(nameof(HasPlayoffData));
                OnPropertyChanged(nameof(HasBowlData));
                return;
            }

            // ── Playoffs ──────────────────────────────────────────────────
            var weekToRound = new Dictionary<int, string>
            {
                { 17, "First Round" },
                { 19, "Quarterfinals" },
                { 20, "Semifinals" },
                { 21, "National Championship" }
            };

            var playoffRounds = allGames
                .Where(g => g.SeasonType == "playoff" && g.Year >= 2014)
                .GroupBy(g => g.Week)
                .OrderBy(g => g.Key)
                .Select(weekGrp =>
                {
                    var label = weekToRound.TryGetValue(weekGrp.Key, out var r)
                                    ? r : $"Week {weekGrp.Key}";
                    var days = weekGrp
                        .GroupBy(g => g.GroupHeader)
                        .OrderBy(g => g.Key)
                        .Select(dayGrp => new PlayoffDayGroup(dayGrp.Key, dayGrp.ToList()))
                        .ToList();
                    return new PlayoffRound(label, days);
                })
                .ToList();

            PlayoffRounds.Clear();
            foreach (var round in playoffRounds)
                PlayoffRounds.Add(round);
            OnPropertyChanged(nameof(HasPlayoffData));

            // ── Bowls — grouped by weekend (Fri–Sun), then by day ─────────
            // SelectedConference now stores Abbreviation directly — no DisplayToAbbr needed
            var conf     = _navState.SelectedConference;
            var confAbbr = conf == "All" ? null : conf;

            var bowlGames = allGames.Where(g => g.SeasonType == "postseason");

            if (confAbbr != null)
            {
                bowlGames = bowlGames.Where(g =>
                    g.HomeConf.Equals(confAbbr, StringComparison.OrdinalIgnoreCase) ||
                    g.AwayConf.Equals(confAbbr, StringComparison.OrdinalIgnoreCase));
            }

            BowlWeekends.Clear();
            foreach (var wk in BuildBowlWeekends(bowlGames))
                BowlWeekends.Add(wk);
            OnPropertyChanged(nameof(HasBowlData));
        }

        // ── Helper: build bowl weekends from filtered games ───────────────

        private static List<BowlWeekendGroup> BuildBowlWeekends(IEnumerable<GameResult> bowlGames)
        {
            static DateTime WeekendSaturday(DateTime d)
            {
                int daysToSat = ((int)DayOfWeek.Saturday - (int)d.DayOfWeek + 7) % 7;
                return d.AddDays(daysToSat).Date;
            }

            var  weekendGroups = bowlGames
                .GroupBy(g =>
                {
                    var d = g.GameDate.ToDateTime();
                    return d.HasValue ? WeekendSaturday(d.Value) : DateTime.MaxValue;
                })
                .OrderBy(g => g.Key)
                .Select(wkGrp =>
                {
                    var label = wkGrp.Key == DateTime.MaxValue
                        ? "TBD"
                        : $"Weekend of {wkGrp.Key:ddd, MMM d}";
                    var days = wkGrp
                        .GroupBy(g => g.GroupHeader)
                        .OrderBy(g =>
                        {
                            try
                            {
                                var first = g.FirstOrDefault()?.GameDate?.ToDateTime();
                                return first ?? DateTime.MaxValue;
                            }
                            catch
                            {
                                return DateTime.MaxValue;
                            }
                        })
                        .Select(dayGrp => new BowlDayGroup(dayGrp.Key, dayGrp.ToList()))
                        .ToList();
                    return new BowlWeekendGroup(label, days);
                })
                .ToList();

            return weekendGroups;
        }

        // ── Conference filter (Championship view only) ────────────────────

        private void ApplyConferenceFilter()
        {
            Championships.Clear();

            // SelectedConference now stores Abbreviation directly — no DisplayToAbbr needed
            var conf     = _navState.SelectedConference;
            var confAbbr = conf == "All" ? null : conf;

            // ── Championships ─────────────────────────────────────────────
            var filteredChamps = confAbbr == null
                ? _allChampionships
                : _allChampionships.Where(c =>
                    c.Conference.Equals(confAbbr, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            Championships.Clear();
            foreach (var c in filteredChamps)
                Championships.Add(c);

            // ── Bowls ─────────────────────────────────────────────────────
            var allGames = _cache.AllGames;
            if (allGames == null || allGames.Count == 0) return;

            var bowlGames = allGames.Where(g => g.SeasonType == "postseason");

            if (confAbbr != null)
            {
                bowlGames = bowlGames.Where(g =>
                    g.HomeConf.Equals(confAbbr, StringComparison.OrdinalIgnoreCase) ||
                    g.AwayConf.Equals(confAbbr, StringComparison.OrdinalIgnoreCase));
            }

            BowlWeekends.Clear();
            foreach (var wk in BuildBowlWeekends(bowlGames))
                BowlWeekends.Add(wk);
            OnPropertyChanged(nameof(HasBowlData));
        }

        // ── Event handlers ────────────────────────────────────────────────

        private async void OnNavStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "FilterChanged") return;

            System.Diagnostics.Debug.WriteLine($"[Postseason] FilterChanged isMain={MainThread.IsMainThread} isActive={IsActive}");

            // Off-screen: defer. Mark stale so SyncPage reloads on next visit.
            if (!IsActive)
            {
                HasLoaded = false;
                return;
            }

            switch (_navState.LastFilterChange)
            {
                case FilterChangeReason.Year:
                    // Full reload — new year means new schedule + new championships
                    await LoadDataAsync();
                    break;

                case FilterChangeReason.Week:
                    // Week change only matters for championship qualifiers (not bowls/playoffs)
                    if (_selectedView != "Bowls" && _selectedView != "Playoffs")
                        await LoadDataAsync();
                    else
                        ApplyConferenceFilter();
                    break;

                case FilterChangeReason.Conference:
                    // Conference/favorites — refilter cached data only
                    ApplyConferenceFilter();
                    break;
            }
        }
        private void OnCacheUpdated()
        {
            // Only refilter if we've already loaded — avoids double render on initial load
            if (!HasLoaded) return;

            MainThread.BeginInvokeOnMainThread(RebuildPostseasonFromCache);
        }
    }
}

// ── Grouping models ──────────────────────────────────────────────────────────

namespace SaturdayPulse.ViewModels
{
    /// <summary>One round of the CFP bracket. Collapsible — tap the round header.</summary>
    public class PlayoffRound : INotifyPropertyChanged
    {
        public string RoundLabel { get; }
        public List<PlayoffDayGroup> Days { get; }

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandIcon)); }
        }

        public string ExpandIcon => _isExpanded ? "▼" : "▶";

        public PlayoffRound(string label, List<PlayoffDayGroup> days)
        {
            RoundLabel = label;
            Days       = days;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One calendar day of CFP playoff games within a round (not collapsible).</summary>
    public class PlayoffDayGroup
    {
        public string DateLabel { get; }
        public List<SaturdayPulse.Models.GameResult> Games { get; }
        public PlayoffDayGroup(string dateLabel, List<SaturdayPulse.Models.GameResult> games)
        {
            DateLabel = dateLabel;
            Games     = games;
        }
    }

    /// <summary>One calendar day of bowl games within a weekend (not collapsible).</summary>
    public class BowlDayGroup
    {
        public string DateLabel { get; }
        public List<SaturdayPulse.Models.GameResult> Games { get; }
        public BowlDayGroup(string dateLabel, List<SaturdayPulse.Models.GameResult> games)
        {
            DateLabel = dateLabel;
            Games     = games;
        }
    }

    /// <summary>One weekend of bowl games. Collapsible — tap the weekend header.</summary>
    public class BowlWeekendGroup : INotifyPropertyChanged
    {
        public string WeekendLabel { get; }
        public List<BowlDayGroup> Days { get; }

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandIcon)); }
        }

        public string ExpandIcon => _isExpanded ? "▼" : "▶";

        public BowlWeekendGroup(string label, List<BowlDayGroup> days)
        {
            WeekendLabel = label;
            Days         = days;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
