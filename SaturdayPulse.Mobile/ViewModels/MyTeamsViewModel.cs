using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using SaturdayPulse.Helpers;
using SaturdayPulse.Models;
using SaturdayPulse.Services;

namespace SaturdayPulse.ViewModels
{
    /// <summary>
    /// Drives the My Teams page: team chip scroller, single-team rankings
    /// card, single-team schedule list.
    ///
    /// Rankings and games are both sourced from shared caches
    /// (RankingsCacheService, GameDataCacheService) — the same instances
    /// PowerRankingsViewModel and ScheduleViewModel use — so My Teams never
    /// issues its own network call on a team switch; it just refilters the
    /// already-warm shared lists down to SelectedTeamId.
    ///
    /// IMPORTANT — shared TeamRanking instances: because rankings come from
    /// a shared cache, the SAME TeamRanking object can be rendered both here
    /// (as the header card) and in PowerRankingsPage's list. This ViewModel
    /// deliberately never touches IsOddRow (that's PowerRankingsViewModel's
    /// list-position concern) to avoid corrupting the other page's zebra
    /// striping. Expand state (IsTrendExpanded/IsArcExpanded/IsStatsExpanded)
    /// and lazily-fetched history (TrendHistory/SeasonArcWeeks) ARE shared
    /// across both pages by design — expanding a panel here means it's
    /// already-expanded (and already-fetched) if you flip to Rankings for
    /// the same team, and vice versa.
    ///
    /// IsActive mirrors PowerRankingsViewModel's pattern exactly: only true
    /// while My Teams is the visible tab. When false, FilterChanged work is
    /// deferred (marked stale via HasLoaded = false) rather than loading
    /// off-screen. Set by MainViewModel on tab switch.
    /// </summary>
    public class MyTeamsViewModel : BaseViewModel
    {
        private readonly GameDataApiService           _apiService;
        private readonly GameDataCacheService         _gameCache;
        private readonly RankingsCacheService         _rankingsCache;
        private readonly TeamCacheService              _teamCache;
        private readonly PersonalGameService           _personalGameService;
        private readonly SharedNavigationStateService  _navState;
        private readonly EntitlementService            _entitlementService;

        private ObservableRangeCollection<MyTeamsGameRow> _selectedTeamGames = new();
        private ObservableRangeCollection<MyTeamsGameRow> _selectedTeamPostseasonGames = new();
        private int             _selectedTeamId;
        private TeamRanking?    _selectedTeamRanking;
        private bool            _isBusy;
        private string          _statusMessage = "Loading...";
        private string          _emptyMessage  = "Follow a team, or set a default team in Settings, to get started.";

        public MyTeamsViewModel(
            GameDataApiService apiService,
            GameDataCacheService gameCache,
            RankingsCacheService rankingsCache,
            TeamCacheService teamCache,
            FollowService followService,
            PersonalGameService personalGameService,
            SharedNavigationStateService navState,
            EntitlementService entitlementService)
            : base(followService)
        {
            _apiService          = apiService;
            _gameCache           = gameCache;
            _rankingsCache       = rankingsCache;
            _teamCache           = teamCache;
            _personalGameService = personalGameService;
            _navState            = navState;
            _entitlementService  = entitlementService;

            SelectTeamCommand = new Command<int>(teamId =>
            {
                if (teamId == 0 || teamId == SelectedTeamId) return;
                SelectedTeamId = teamId;
                UpdateChipSelection();
                ApplyTeamFilter(); // no network call — both caches already warm
            });

            RefreshCommand = new Command(() => _ = LoadForYearOrWeekChangeAsync(forceReload: true));

            TogglePostseasonCommand = new Command(() => IsPostseasonExpanded = !IsPostseasonExpanded);

            // Tapping a game sets the week selector to that game's week —
            // _navState is shared with MainViewModel, so this has the exact
            // same effect as tapping the week strip directly.
            SelectWeekCommand = new Command<int>(week =>
            {
                if (week > 0) _navState.SelectedWeek = week;
            });

            // Opponent-name link (2026-09-05) — forces the Conference filter
            // to All (the target game's conference may not match whatever's
            // currently selected) and jumps the week selector to the game's
            // week, then asks MainViewModel to switch tabs and
            // ScheduleViewModel to scroll/highlight — see
            // SharedNavigationStateService's TabChangeRequested/
            // GameHighlightRequested remarks for why this doesn't just call
            // either ViewModel directly.
            NavigateToGameCommand = new Command<GameResult>(game =>
            {
                if (game == null) return;

                _navState.SelectedConference = "All";
                _navState.SelectedWeek = game.Week;
                _navState.RequestTabChange();
                _navState.RequestGameHighlight(game.Id);
            });

            // Copied from PowerRankingsViewModel's expand commands exactly —
            // same lazy-fetch-once-then-toggle shape, same TeamRanking fields.
            ToggleTrendExpandCommand = new Command<TeamRanking>(async t =>
            {
                if (t == null || !HasSeasonPass) return;

                if (!t.IsTrendExpanded && t.TrendHistory == null)
                {
                    var data = await Task.Run(async () =>
                        await _apiService.GetTeamRollingAveragesAsync(t.TeamID, _navState.SelectedYear));

                    if (data?.History?.Count > 0)
                    {
                        var h = data.History[^1];
                        t.TrendRating    = h.TrendRating;
                        t.PedigreeRating = h.PedigreeRating;
                        t.SeedRating     = h.SeedRating;
                        t.TrendHistory   = h.TrendHistory;
                        t.PedigreeHistory = h.PedigreeHistory;
                    }
                }

                t.IsTrendExpanded = !t.IsTrendExpanded;
            });

            ToggleArcExpandCommand = new Command<TeamRanking>(async t =>
            {
                if (t == null || !HasSeasonPass) return;

                if (!t.IsArcExpanded && t.SeasonArcWeeks == null)
                {
                    var data = await Task.Run(async () =>
                        await _apiService.GetTeamSeasonArcAsync(t.TeamID, _navState.SelectedYear));

                    if (data?.Weeks?.Count > 0)
                        t.SeasonArcWeeks = data.Weeks;
                }

                t.IsArcExpanded = !t.IsArcExpanded;
            });

            ToggleStatsExpandCommand = new Command<TeamRanking>(t =>
            {
                if (t == null || !HasSeasonPass) return;
                t.IsStatsExpanded = !t.IsStatsExpanded;
            });

            ToggleRosterExpandCommand = new Command<TeamRanking>(async t =>
            {
                if (t == null || !HasSeasonPass) return;

                if (!t.IsRosterExpanded && t.RosterChanges == null)
                {
                    var data = await Task.Run(async () =>
                        await _apiService.GetRosterChangesAsync(t.TeamID, _navState.SelectedYear));

                    if (data != null)
                        t.RosterChanges = data;
                }

                t.IsRosterExpanded = !t.IsRosterExpanded;
                if (!t.IsRosterExpanded)
                    t.ActiveRosterListKey = null;
            });

            // Data is already loaded by the time these are tappable (they only
            // render once RosterChanges is populated) — plain synchronous
            // toggles, no fetch. Mirrors PowerRankingsViewModel exactly.
            ToggleRecruitingListCommand = new Command<TeamRanking>(t =>
            {
                if (t == null) return;
                t.ActiveRosterListKey = t.IsRecruitingListActive ? null : "Recruiting";
            });

            TogglePortalInListCommand = new Command<TeamRanking>(t =>
            {
                if (t == null) return;
                t.ActiveRosterListKey = t.IsPortalInListActive ? null : "PortalIn";
            });

            TogglePortalOutListCommand = new Command<TeamRanking>(t =>
            {
                if (t == null) return;
                t.ActiveRosterListKey = t.IsPortalOutListActive ? null : "PortalOut";
            });

            // Gated Details paywall message (2026-07-25) — tapping the
            // locked Vegas/projections message routes through the same
            // login-check EntitlementService uses for Settings' Season Pass
            // button. This ViewModel doesn't need to track its own
            // IsLoggedIn/profile fields for this — EntitlementService
            // already applied any fresh profile internally.
            SeasonPassCommand = new Command(async () =>
            {
                var result = await _entitlementService.EnsureLoggedInForPurchaseAsync();
                if (!result.CanProceed) return;

                await Shell.Current.DisplayAlert(
                    "Season Pass", "Coming soon — payment isn't wired up yet.", "OK");
            });

            // Mirrors ScheduleViewModel's TogglePersonalGameCommand exactly.
            TogglePersonalGameCommand = new Command<GameResult>(game =>
            {
                if (game == null) return;
                _personalGameService.Toggle(game.AwayId, game.HomeId);
                game.IsGameFavorited = _personalGameService.IsFavorited(game.AwayId, game.HomeId);
            });

            _followService.TeamFollowChanged  += OnTeamFollowChanged;
            _followService.PrimaryTeamChanged += OnPrimaryTeamChanged;
            _navState.PropertyChanged         += OnNavStateChanged;
            _gameCache.CacheUpdated           += OnSharedCacheUpdated;
            _rankingsCache.CacheUpdated       += OnSharedCacheUpdated;

            // Keeps HasSeasonPass/IsNotSeasonPass (and everything bound to
            // them in MyTeamsPage.xaml) live if entitlement changes while
            // this tab is open — e.g. an admin flipping the dev toggle in
            // Settings without leaving My Teams.
            _entitlementService.EntitlementChanged += OnEntitlementChanged;
        }

        private void OnEntitlementChanged()
        {
            OnPropertyChanged(nameof(HasSeasonPass));
            OnPropertyChanged(nameof(IsNotSeasonPass));
        }

        // ── Bindable collections ──────────────────────────────────────────

        public ObservableRangeCollection<MyTeamsGameRow> SelectedTeamGames
        {
            get => _selectedTeamGames;
            private set { _selectedTeamGames = value; OnPropertyChanged(); }
        }

        public ObservableRangeCollection<MyTeamsGameRow> SelectedTeamPostseasonGames
        {
            get => _selectedTeamPostseasonGames;
            private set { _selectedTeamPostseasonGames = value; OnPropertyChanged(); }
        }

        /// <summary>Gates the collapsible postseason section — only shown once the
        /// selected team actually has postseason games loaded for this year.</summary>
        public bool HasPostseasonGames => SelectedTeamPostseasonGames.Count > 0;

        private bool _isPostseasonExpanded = true;
        public bool IsPostseasonExpanded
        {
            get => _isPostseasonExpanded;
            set
            {
                _isPostseasonExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PostseasonExpandIcon));
            }
        }
        public string PostseasonExpandIcon => IsPostseasonExpanded ? "▲" : "▼";

        public ObservableCollection<TeamChipItem> Chips { get; } = new();

        // ── Bindable properties ───────────────────────────────────────────

        public int SelectedTeamId
        {
            get => _selectedTeamId;
            private set { _selectedTeamId = value; OnPropertyChanged(); }
        }

        public TeamRanking? SelectedTeamRanking
        {
            get => _selectedTeamRanking;
            private set
            {
                _selectedTeamRanking = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedTeamRanking));
            }
        }

        /// <summary>
        /// The pinned team card row (outside the CollectionView) binds its
        /// IsVisible here rather than to SelectedTeamRanking directly, since
        /// that card sets its own BindingContext to SelectedTeamRanking —
        /// this property has to be read off the page's BindingContext
        /// (MyTeamsViewModel), one level up.
        /// </summary>
        public bool HasSelectedTeamRanking => SelectedTeamRanking != null;

        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string EmptyMessage
        {
            get => _emptyMessage;
            private set { _emptyMessage = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// True only while My Teams is the visible tab. Set by MainPage on tab
        /// switch — same role as PowerRankingsViewModel.IsActive.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Set true once InitializeAsync/LoadForYearOrWeekChangeAsync has
        /// completed at least once. MainPage.xaml.cs resets this to false on
        /// year change (ResetAllPages) and checks it in SyncPage's lazy-load
        /// switch — same convention as ScheduleViewModel/PowerRankingsViewModel.
        /// </summary>
        public bool HasLoaded { get; set; }

        // ── Season Pass gating (2026-07-25) ─────────────────────────────
        // Sourced from the shared EntitlementService, not a local fetch —
        // stays in sync with SettingsViewModel via EntitlementService's
        // ApplyProfile/Clear, without this ViewModel depending on
        // SettingsViewModel directly. Gates the Trend/Pedigree, Season Arc,
        // and Offense/Defense toggle links (disabled/grayed, no popup) and
        // the Vegas/projections portion of the Details paywall message.
        public bool HasSeasonPass => _entitlementService.HasSeasonPass;

        /// <summary>Inverse of HasSeasonPass — lets MyTeamsPage.xaml show the
        /// locked paywall message without an inverse-bool converter.</summary>
        public bool IsNotSeasonPass => !HasSeasonPass;

        // ── Commands ──────────────────────────────────────────────────────

        public ICommand SelectTeamCommand         { get; }
        public ICommand SelectWeekCommand          { get; }
        public ICommand NavigateToGameCommand      { get; }
        public ICommand RefreshCommand             { get; }
        public ICommand ToggleTrendExpandCommand   { get; }
        public ICommand ToggleArcExpandCommand     { get; }
        public ICommand ToggleStatsExpandCommand   { get; }
        public ICommand ToggleRosterExpandCommand    { get; }
        public ICommand ToggleRecruitingListCommand  { get; }
        public ICommand TogglePortalInListCommand    { get; }
        public ICommand TogglePortalOutListCommand   { get; }
        public ICommand TogglePersonalGameCommand  { get; }
        public ICommand SeasonPassCommand          { get; }
        public ICommand TogglePostseasonCommand    { get; }
        // ToggleFollowCommand is inherited from BaseViewModel — already
        // pattern-matches int (GameCardTemplate hearts) and TeamRanking
        // (TeamCardTemplate heart).

        // ── Initial load ──────────────────────────────────────────────────

        /// <summary>
        /// Call once on first navigation to this page. Since My Teams is the
        /// default landing page, InitializeAsync's TeamCacheService warm-up
        /// is effectively the app's first data load.
        /// </summary>
        public async Task InitializeAsync()
        {
            IsBusy = true;
            try
            {
                await _teamCache.EnsureLoadedAsync();
                BuildChips();

                if (Chips.Count == 0)
                {
                    StatusMessage = "No teams followed yet.";
                    return;
                }

                if (SelectedTeamId == 0)
                    SelectedTeamId = Chips[0].TeamId;

                UpdateChipSelection();
                await LoadForYearOrWeekChangeAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Reload paths ──────────────────────────────────────────────────

        /// <summary>
        /// Year or Week changed (or explicit refresh) — warms both shared
        /// caches for the current year/week, then refilters to the selected
        /// team. Both cache calls no-op server-side if already warm and
        /// forceReload is false.
        /// </summary>
        private async Task LoadForYearOrWeekChangeAsync(bool forceReload = false)
        {
            if (SelectedTeamId == 0) return;

            IsBusy = true;
            try
            {
                var year = _navState.SelectedYear;
                var week = _navState.SelectedWeek;

                await _rankingsCache.GetRankingsAsync(year, week, forceReload);
                await _gameCache.GetGamesForYearAsync(year, forceReload);

                HasLoaded = true;
                ApplyTeamFilter();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ApplyTeamFilter()
        {
            SelectedTeamRanking = _rankingsCache.AllRankings
                .FirstOrDefault(r => r.TeamID == SelectedTeamId);

            var teamGames = _gameCache.AllGames
                .Where(g => g.AwayId == SelectedTeamId || g.HomeId == SelectedTeamId)
                .OrderBy(g => g.Week)
                .ToList();

            // Two independently-filtered views of the same source list — regular
            // season gets bye-week rows inserted (see InsertByeWeeks), postseason
            // does not (bowl/playoff week gaps aren't byes). SelectedTeamRanking's
            // Rec/ProjectedRecord total is untouched by this split — it's a
            // separate, server-computed value that counts all games, same as before.
            var regularRows = teamGames
                .Where(g => string.Equals(g.SeasonType, "regular", StringComparison.OrdinalIgnoreCase))
                .Select(BuildGameRow)
                .ToList();
            regularRows = InsertByeWeeks(regularRows);
            SelectedTeamGames.ReplaceRange(regularRows);

            var postseasonRows = teamGames
                .Where(g => !string.Equals(g.SeasonType, "regular", StringComparison.OrdinalIgnoreCase))
                .Select(BuildGameRow)
                .ToList();
            SelectedTeamPostseasonGames.ReplaceRange(postseasonRows);
            OnPropertyChanged(nameof(HasPostseasonGames));

            StatusMessage = SelectedTeamRanking is null
                ? "No ranking data for this team/week yet."
                : string.Empty;
        }

        /// <summary>
        /// Fills any gap in the selected team's week sequence with a synthetic
        /// Bye Week row (Game = null — see MyTeamsGameRow.IsByeWeek). Purely a
        /// display concern for this schedule list: it doesn't touch _gameCache
        /// or anything the Rec/ProjectedRecord total is computed from, since
        /// that's a separate, server-computed value (PowerRankingRowResponse.
        /// ProjectedWins/Losses) that never sees this list at all.
        ///
        /// Only fills interior gaps (between the first and last game on the
        /// schedule) — a gap before the first game or after the last isn't a
        /// bye, it's just outside the season.
        /// </summary>
        private List<MyTeamsGameRow> InsertByeWeeks(List<MyTeamsGameRow> rows)
        {
            if (rows.Count < 2) return rows;

            var result = new List<MyTeamsGameRow>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                result.Add(rows[i]);

                if (i == rows.Count - 1) continue;

                for (int missingWeek = rows[i].Week + 1; missingWeek < rows[i + 1].Week; missingWeek++)
                    result.Add(BuildByeWeekRow(missingWeek));
            }

            return result;
        }

        private MyTeamsGameRow BuildByeWeekRow(int week) => new()
        {
            Game           = null,
            Week           = week,
            IsSelectedWeek = week == _navState.SelectedWeek
        };

        /// <summary>
        /// Resolves a GameResult into the selected team's perspective —
        /// opponent name/id/follow-state, "my score" vs "their score", short
        /// date. SpreadLine/OULine reuse Game.DisplayMargin/DisplayOU as-is
        /// (already fully-formatted strings, e.g. "Margin: -7 (-2.5)").
        /// </summary>
        private MyTeamsGameRow BuildGameRow(GameResult g)
        {
            bool teamIsHome = g.HomeId == SelectedTeamId;

            var dateShort = TryFormatShortDate(g.GameDate); // see class-header note re: field name assumption

            // Raw HomePoints/AwayPoints (not the pre-formatted Display*Score
            // strings, which append a projection in parens) so this is a
            // clean numeric comparison from the selected team's perspective.
            // Unplayed games fall back to HomeProjScore/AwayProjScore so the
            // (W)/(L) badge shows a projected result instead of staying blank
            // until the game is actually played.
            var resultLetter = string.Empty;
            var myScore  = g.IsPlayed
                ? teamIsHome ? g.HomePoints : g.AwayPoints
                : teamIsHome ? g.HomeProjScore : g.AwayProjScore;
            var oppScore = g.IsPlayed
                ? teamIsHome ? g.AwayPoints : g.HomePoints
                : teamIsHome ? g.AwayProjScore : g.HomeProjScore;
            resultLetter = myScore > oppScore ? "(W)" : myScore < oppScore ? "(L)" : "(T)";

            return new MyTeamsGameRow
            {
                Game               = g,
                Week               = g.Week,
                DateShort          = dateShort,
                AtPrefix           = teamIsHome ? "vs " : "@ ",
                ResultLetter       = resultLetter,
                OpponentName       = teamIsHome ? g.VisitorName       : g.HomeName,
                OpponentTeamId     = teamIsHome ? g.AwayId            : g.HomeId,
                OpponentIsFollowed = teamIsHome ? g.VisitorIsFollowed : g.HomeIsFollowed,
                ScoreLine          = teamIsHome
                    ? $"{g.DisplayHomeScore} - {g.DisplayVisitorScore}"
                    : $"{g.DisplayVisitorScore} - {g.DisplayHomeScore}",
                SpreadLine         = g.DisplayMargin,
                OULine             = g.DisplayOU,
                IsSelectedWeek     = g.Week == _navState.SelectedWeek
            };
        }

        private static string TryFormatShortDate(string? rawDate) =>
            string.IsNullOrEmpty(rawDate) ? string.Empty : rawDate.ToDisplayDate();

        // ── Chip management ──────────────────────────────────────────────

        private void BuildChips()
        {
            Chips.Clear();

            var primaryId   = _followService.GetPrimaryTeamId();
            var followedIds = _followService.GetFollowedIds();

            if (primaryId.HasValue)
            {
                var team = _teamCache.GetTeam(primaryId.Value);
                if (team != null)
                {
                    Chips.Add(new TeamChipItem
                    {
                        TeamId    = team.TeamID,
                        TeamName  = team.TeamName,
                        IsPrimary = true
                    });
                }
            }

            var followedTeams = followedIds
                .Where(id => id != primaryId)
                .Select(id => _teamCache.GetTeam(id))
                .Where(t => t != null)
                .OrderBy(t => t!.TeamName);

            foreach (var team in followedTeams)
            {
                Chips.Add(new TeamChipItem
                {
                    TeamId    = team!.TeamID,
                    TeamName  = team.TeamName,
                    IsPrimary = false
                });
            }

            UpdateChipSelection();
        }

        private void UpdateChipSelection()
        {
            foreach (var chip in Chips)
                chip.IsSelected = chip.TeamId == SelectedTeamId;
        }

        // ── Event handlers ────────────────────────────────────────────────

        private async void OnNavStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "FilterChanged") return;

            // Off-screen: defer, mark stale so the next appearance reloads —
            // same pattern as PowerRankingsViewModel.
            if (!IsActive)
            {
                HasLoaded = false;
                return;
            }

            switch (_navState.LastFilterChange)
            {
                case FilterChangeReason.Year:
                case FilterChangeReason.Week:
                    await LoadForYearOrWeekChangeAsync();
                    break;

                case FilterChangeReason.Conference:
                    // Ignored — My Teams doesn't use the global conference
                    // filter (see MainViewModel's ConferencePillText context
                    // switch instead).
                    break;
            }
        }

        /// <summary>
        /// Fires from either GameDataCacheService or RankingsCacheService —
        /// covers follow-flag stamps and reloads triggered by other tabs
        /// (e.g. Rankings force-refreshing while My Teams is active).
        /// Refilter only, never reload — reload is LoadForYearOrWeekChangeAsync's job.
        /// </summary>
        private void OnSharedCacheUpdated()
        {
            if (!HasLoaded || !IsActive) return;
            MainThread.BeginInvokeOnMainThread(ApplyTeamFilter);
        }

        private void OnTeamFollowChanged(int teamId, bool isFollowed)
        {
            BuildChips();

            // If the currently-selected team lost its only chip (un-followed
            // and not primary), fall back to the first remaining chip.
            if (!isFollowed && teamId == SelectedTeamId && Chips.All(c => c.TeamId != SelectedTeamId))
            {
                SelectedTeamId = Chips.FirstOrDefault()?.TeamId ?? 0;
                UpdateChipSelection();
                ApplyTeamFilter();
            }
        }

        private async void OnPrimaryTeamChanged(int? teamId)
        {
            BuildChips();

            // Per design: primary-team change is treated as a filter change —
            // re-point at the new team immediately.
            if (teamId.HasValue)
            {
                SelectedTeamId = teamId.Value;
                UpdateChipSelection();

                // If this fires before the very first load ever completed —
                // e.g. FollowService.InitializeAsync() resolving after
                // InitializeAsync() above already hit its "no teams
                // followed yet" early-return because the follow cache
                // wasn't warm yet — the shared rankings/games caches were
                // never fetched. Refiltering an empty cache leaves chips
                // and a primary team visible with no game data, until
                // something else (a tab switch) triggers a real load. Do
                // the real load here instead of just refiltering.
                if (HasLoaded)
                    ApplyTeamFilter();
                else
                    await LoadForYearOrWeekChangeAsync();
            }
        }
    }

    // ── My Teams compact game row ────────────────────────────────────────

    /// <summary>
    /// Wraps a GameResult with the selected team's perspective already
    /// resolved — opponent name/id/follow-state, "my score" vs "their
    /// score", short date — so the compact single-line card in
    /// MyTeamsPage.xaml doesn't need any converters or Home/Away branching
    /// in XAML. Rebuilt fresh every time ApplyTeamFilter runs (team switch,
    /// year/week change, cache update), so plain get-only properties are
    /// fine — no INotifyPropertyChanged needed, the whole row gets replaced.
    ///
    /// Game.IsGameFavorited / SpreadLine / OULine are the SAME pre-formatted
    /// display strings the original card already used (DisplayMargin /
    /// DisplayOU) — reused as-is, not reconstructed from VegasLines.
    ///
    /// DateShort is parsed from GameResult.GameDate (yyyy-MM-dd), same field
    /// GroupHeader uses — formatted via the shared ToDisplayDate() extension.
    /// </summary>
    public class MyTeamsGameRow
    {
        /// <summary>
        /// Null for a synthetic Bye Week row (see MyTeamsViewModel.InsertByeWeeks) —
        /// null is the deliberate signal, not an omission. Any code branching on
        /// played/unplayed/real data should check this (or IsByeWeek below) first.
        /// </summary>
        public GameResult? Game               { get; init; }
        public bool         IsByeWeek          => Game == null;
        public int         Week               { get; init; }
        public string      DateShort          { get; init; } = string.Empty;
        public string      AtPrefix           { get; init; } = string.Empty; // "@ " or "vs "
        /// <summary>"W", "L", "T", or "" if the game hasn't been played yet. From the selected team's perspective.</summary>
        public string      ResultLetter       { get; init; } = string.Empty;
        public string      OpponentName       { get; init; } = string.Empty;
        public int         OpponentTeamId     { get; init; }
        public bool         OpponentIsFollowed { get; init; }
        public string      ScoreLine          { get; init; } = string.Empty; // "7 (26) - 14 (28)"
        public string      SpreadLine         { get; init; } = string.Empty; // Game.DisplayMargin, reused as-is
        public string      OULine             { get; init; } = string.Empty; // Game.DisplayOU, reused as-is
        public bool         IsSelectedWeek     { get; init; }
    }


    public class TeamChipItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public int    TeamId    { get; init; }
        public string TeamName  { get; init; } = string.Empty;
        public bool   IsPrimary { get; init; }

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
