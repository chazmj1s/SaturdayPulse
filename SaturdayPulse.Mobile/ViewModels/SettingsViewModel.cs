using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SaturdayPulse.Core.Content;
using SaturdayPulse.Helpers;
using SaturdayPulse.Models;
using SaturdayPulse.Services;

namespace SaturdayPulse.ViewModels
{
    public class SettingsViewModel : BaseViewModel
    {
        private readonly GameDataApiService           _apiService;
        private readonly PersonalGameService          _personalGameService;
        private readonly SharedNavigationStateService _navState;
        private readonly TeamCacheService              _teamCache;
        private readonly UserApiService                _userApi;
        private readonly AuthService                   _authService;
        private readonly FeedbackService                _feedbackService;
        private readonly EntitlementService              _entitlementService;
        private readonly ContentApiService               _contentApi;

        // ── Raw data ──────────────────────────────────────────────────────
        private List<TeamInfo>    _allTeams      = [];
        private List<RivalryInfo> _allRivalries  = [];
        private List<RivalryInfo> _personalGames = [];

        // ── Sub-tab state ─────────────────────────────────────────────────
        private string _selectedView = "Teams";

        public string SelectedView
        {
            get => _selectedView;
            set
            {
                if (_selectedView == value) return;
                _selectedView = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTeamsView));
                OnPropertyChanged(nameof(IsGamesView));
                OnPropertyChanged(nameof(ShowFlatTeamsList));
                OnPropertyChanged(nameof(ShowGroupedTeamsList));
            }
        }

        // ── Accordion state — only one section open at a time ─────────────
        // User Profile is the default-expanded section. Internal keys are
        // unchanged from before the rename pass (UserConfig/Following/
        // Feedback) even though their XAML labels are now App Settings/
        // Favorites/Support — renaming the keys too would've meant touching
        // every ToggleSectionCommand CommandParameter for no functional
        // benefit. SeasonPass/Content are the two new panels.
        private string? _expandedSection = "UserProfile";

        public bool IsUserProfileExpanded   => _expandedSection == "UserProfile";
        public bool IsUserConfigExpanded    => _expandedSection == "UserConfig";      // App Settings
        public bool IsFollowingExpanded     => _expandedSection == "Following";       // Favorites
        public bool IsSeasonPassExpanded    => _expandedSection == "SeasonPass";      // NEW
        public bool IsContentExpanded       => _expandedSection == "Content";         // NEW
        public bool IsFeedbackExpanded      => _expandedSection == "Feedback";        // Support
        public bool IsDebugLogExpanded      => _expandedSection == "DebugLog";

        public bool IsTeamsView => _selectedView == "Teams";
        public bool IsGamesView => _selectedView == "Games";

        // ── Shared state ──────────────────────────────────────────────────
        private bool   _isBusy;
        private string _statusMessage = string.Empty;

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoading)); }
        }

        public bool IsLoading => _isBusy;
        public bool HasLoaded { get; private set; }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        // ── User preference: Show Favorites First ─────────────────────────
        public bool ShowFavoritesFirst
        {
            get => _navState.ShowFavoritesFirst;
            set => _navState.ShowFavoritesFirst = value;
        }

        public string DefaultWeek
        {
            get => _navState.DefaultWeek;
            set => _navState.DefaultWeek = value;
        }

        public string DefaultConference
        {
            get => _navState.DefaultConference;
            set => _navState.DefaultConference = value;
        }

        // ── User preference: Default team (My Teams' primary team) ────────
        // Lives on FollowService rather than SharedNavigationStateService —
        // it's a team-follow concept (see FollowService.SetPrimaryTeam),
        // not a game-data filter like DefaultWeek/DefaultConference.
        public string DefaultTeamDisplay =>
            _followService.GetPrimaryTeamId() is int id
                ? _teamCache.GetTeam(id)?.TeamName ?? "None"
                : "None";

        // ── User preference: Default landing page ──────────────────────────
        // Standalone app-level preference (which tab MainPage.xaml.cs shows
        // at startup — see GetInitialTabIndex there). Not part of
        // SharedNavigationStateService since it's navigation UI state, not
        // game-data filter state.
        private const string DefaultLandingPageKey = "DefaultLandingPage";

        public string DefaultLandingPage
        {
            get => Preferences.Default.Get(DefaultLandingPageKey, "MyTeams");
            set
            {
                Preferences.Default.Set(DefaultLandingPageKey, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(DefaultLandingPageDisplay));
            }
        }

        public string DefaultLandingPageDisplay => DefaultLandingPage switch
        {
            "MyTeams"    => "My Teams",
            "Games"     => "Games",
            "Rankings"   => "Rankings",
            "Postseason" => "Postseason",
            "Sandbox"    => "Sandbox",
            "Settings"   => "Settings",
            _            => "My Teams"
        };

        // ── User preference: Handle (label shown as "User Name" in XAML) ──
        // Sourced from UserProfile via UserApiService — no local Preferences
        // copy. Populated by LoadDataAsync alongside teams/rivalries.
        private string _handle = string.Empty;

        public string Handle
        {
            get => _handle;
            private set
            {
                _handle = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDefaultHandle));
            }
        }

        /// <summary>
        /// True while the handle still matches the server's auto-generated
        /// "user_{shortguid}" default pattern — i.e. the person has never
        /// picked one. Drives the first-launch routing in MainPage.xaml.cs.
        /// </summary>
        public bool IsDefaultHandle => Handle.StartsWith("user_", StringComparison.OrdinalIgnoreCase);

        private string _email = string.Empty;
        public string Email
        {
            get => _email;
            private set { _email = value; OnPropertyChanged(); OnPropertyChanged(nameof(EmailDisplay)); }
        }

        public string EmailDisplay => string.IsNullOrEmpty(Email) ? "Not set" : Email;

        private string _phoneNumber = string.Empty;
        public string PhoneNumber
        {
            get => _phoneNumber;
            private set { _phoneNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(PhoneDisplay)); }
        }

        public string PhoneDisplay => string.IsNullOrEmpty(PhoneNumber) ? "Not set" : PhoneNumber;

        private bool _marketingSmsConsent;
        public bool MarketingSmsConsent
        {
            get => _marketingSmsConsent;
            private set { _marketingSmsConsent = value; OnPropertyChanged(); }
        }

        // ── User preference: marketing email consent ───────────────────────
        // Unlike MarketingSmsConsent (set only via the popup that follows
        // editing the phone number - see EditPhoneCommand), this is a live
        // inline checkbox that saves immediately on tap. The public setter
        // fires the save; ApplyProfile below sets the backing field directly
        // so loading a profile never triggers an unwanted PATCH.
        private bool _marketingEmailConsent;
        public bool MarketingEmailConsent
        {
            get => _marketingEmailConsent;
            set
            {
                if (_marketingEmailConsent == value) return;
                _marketingEmailConsent = value;
                OnPropertyChanged();
                _ = SaveEmailConsentAsync(value);
            }
        }

        private async Task SaveEmailConsentAsync(bool consent)
        {
            var ok = await _userApi.UpdateEmailConsentAsync(consent);
            if (!ok)
                StatusMessage = "Couldn't update notification preference.";
        }

        // ── Auth0 — login state ─────────────────────────────────────────
        // No forced login anywhere in the app. This is purely opt-in: the
        // person taps Login/Create Account (or Season Pass, which offers to
        // log in first) when THEY want to. See AuthService for StayLoggedIn.

        // IsLoggedIn now means "authenticated AND a profile was found" —
        // not just "Auth0 handed back a token." A successful Auth0 login
        // with no server-side account is deliberately NOT treated as
        // logged in here; see LoginCommand/TryLoginAsync below.
        private bool _isLoggedIn;
        public bool IsLoggedIn
        {
            get => _isLoggedIn;
            private set
            {
                if (_isLoggedIn == value) return;
                _isLoggedIn = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLoggedOut));
            }
        }

        /// <summary>Drives which link row Settings shows — Login/Create
        /// Account, or Logout/Delete Account.</summary>
        public bool IsLoggedOut => !IsLoggedIn;

        public bool StayLoggedIn
        {
            get => _authService.StayLoggedIn;
            set { _authService.StayLoggedIn = value; OnPropertyChanged(); }
        }

        // ── Admin ────────────────────────────────────────────────────────
        // Sourced directly from UserProfileResponse.IsAdmin — server-side
        // only, never client-writable. Gates the Debug Log section and
        // swaps the Season Pass "Get Season Pass" link for the dev toggle.
        // Populated by ApplyProfile alongside everything else.
        private bool _isAdmin;
        public bool IsAdmin
        {
            get => _isAdmin;
            private set
            {
                if (_isAdmin == value) return;
                _isAdmin = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAccessDebugLog));
                OnPropertyChanged(nameof(IsNotAdmin));
            }
        }

        /// <summary>Admin-only — same shape as CanAccessFeedback below, but
        /// gated on IsAdmin rather than HasSeasonPass per its own
        /// requirement (Debug Log is a dev/support tool, not a paid feature).</summary>
        public bool CanAccessDebugLog => IsAdmin;

        /// <summary>Inverse of IsAdmin — exists purely so SettingsPage.xaml
        /// can show/hide the two Season Pass UI states (real link vs. dev
        /// toggle) without needing an inverse-bool converter registered.</summary>
        public bool IsNotAdmin => !IsAdmin;

        // ── Season Pass entitlement ──────────────────────────────────────
        // Sourced directly from UserProfileResponse.IsEntitled (server-computed:
        // ExpiryDate.HasValue && ExpiryDate > UtcNow) — no client-side date math,
        // avoids clock-skew disagreements between device and server. Populated
        // by LoadDataAsync alongside the rest of the profile.
        private bool _hasSeasonPass;
        public bool HasSeasonPass
        {
            get => _hasSeasonPass;
            private set
            {
                if (_hasSeasonPass == value) return;
                _hasSeasonPass = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAccessFeedback));
            }
        }

        /// <summary>
        /// One row per distinct product the user holds (deduped to the
        /// latest row per ProductKey - repeated dev-toggle on/off, for
        /// example, can leave several historical rows for the same bare key;
        /// AccountAuditLog keeps the full history, this panel shows current
        /// status only), plus a synthetic "Purchase" row for the current
        /// season if the user doesn't already hold it. Rebuilt by
        /// RebuildSeasonPassEntries whenever ApplyProfile runs.
        /// </summary>
        public ObservableCollection<SeasonPassEntryViewModel> SeasonPassEntries { get; } = new();

        // ── Teams ─────────────────────────────────────────────────────────
        public ObservableCollection<TeamInfo> Teams { get; } = new();

        /// <summary>
        /// Populated instead of Teams when the header conference filter is
        /// "All" - groups the same underlying team list by conference,
        /// each group independently expandable. Teams stays empty in that
        /// case (and vice versa) rather than keeping both in sync, since
        /// only one is ever visible at a time - see IsConferenceAll.
        /// </summary>
        public ObservableCollection<ConferenceGroupInfo> TeamGroups { get; } = new();

        public bool IsConferenceAll => _navState.SelectedConference == "All";
        public bool IsConferenceNotAll => !IsConferenceAll;

        /// <summary>
        /// These, not IsConferenceAll/IsConferenceNotAll directly, are what
        /// SettingsPage.xaml's two Teams CollectionViews bind IsVisible to.
        /// Bug fixed 2026-07-26: the first pass only checked the conference
        /// filter, so a Teams list stayed visible even when the Games sub-tab
        /// was selected - the two are supposed to be mutually exclusive.
        /// </summary>
        public bool ShowFlatTeamsList => IsTeamsView && IsConferenceNotAll;
        public bool ShowGroupedTeamsList => IsTeamsView && IsConferenceAll;

        // ── Games ─────────────────────────────────────────────────────────
        public ObservableCollection<RivalryInfo> Games { get; } = new();

        public ObservableCollection<string> TierFilters { get; } = new();

        private string _selectedTier = "♥ Personal";
        public string SelectedTier
        {
            get => _selectedTier;
            set
            {
                if (value == "── Rivalries ──") return;
                if (_selectedTier == value) return;
                _selectedTier = value;
                OnPropertyChanged();
                ApplyGamesFilter();
            }
        }

        // ── Feedback / Support (Season Pass gated) ─────────────────────────
        // Beta access: Season Pass is being granted free to beta testers
        // specifically so this can sit behind the same entitlement gate
        // real paying members will eventually use.
        public bool CanAccessFeedback => HasSeasonPass;

        private string _feedbackText = string.Empty;
        public string FeedbackText
        {
            get => _feedbackText;
            set { _feedbackText = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSubmitFeedback)); }
        }

        private bool _isSendingFeedback;
        public bool IsSendingFeedback
        {
            get => _isSendingFeedback;
            private set { _isSendingFeedback = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSubmitFeedback)); }
        }

        public bool CanSubmitFeedback => !IsSendingFeedback && !string.IsNullOrWhiteSpace(FeedbackText);

        private string _feedbackStatus = string.Empty;
        public string FeedbackStatus
        {
            get => _feedbackStatus;
            private set { _feedbackStatus = value; OnPropertyChanged(); }
        }

        // Destination for the "Email the Dev Team" link - admin-editable via
        // the Content document rather than hardcoded, so it can change
        // without an app release. Empty until content loads; the link
        // command no-ops if it's still empty.
        private string _supportEmail = string.Empty;
        public string SupportEmail
        {
            get => _supportEmail;
            private set { _supportEmail = value; OnPropertyChanged(); }
        }

        // ── Content / About (read-only) ────────────────────────────────────
        // One entry per non-empty ContentSection (About/Privacy/Terms/Season
        // Pass info/FAQ/Announcements/Release Notes) - sections with no
        // content yet (Title and Content both blank) are skipped rather than
        // shown as an empty expandable panel.
        public ObservableCollection<ContentSectionViewModel> ContentSections { get; } = new();

        // ── Debug Log ─────────────────────────────────────────────────────

        /// <summary>Bound to the Debug Log CollectionView in Settings.</summary>
        public ObservableCollection<LogEntry> LogEntries => AppLogger.Entries;

        public int LogEntryCount => AppLogger.Entries.Count;

        // ── Commands ──────────────────────────────────────────────────────
        public ICommand LoadDataCommand                { get; }
        public ICommand SelectViewCommand              { get; }
        public ICommand TogglePersonalCommand          { get; }
        public ICommand ToggleSectionCommand           { get; }
        public ICommand ToggleFollowCommand            { get; }
        public ICommand RefreshCommand                 { get; }
        public ICommand SelectDefaultWeekCommand       { get; }
        public ICommand SelectDefaultConferenceCommand { get; }
        public ICommand SelectDefaultTeamCommand         { get; }
        public ICommand SelectDefaultLandingPageCommand  { get; }
        public ICommand EditHandleCommand              { get; }
        public ICommand EditEmailCommand               { get; }
        public ICommand EditPhoneCommand                { get; }
        public ICommand ClearLogCommand                { get; }
        public ICommand RefreshLogCommand              { get; }
        public ICommand LoginCommand                   { get; }
        public ICommand CreateAccountCommand           { get; }
        public ICommand LogoutCommand                  { get; }
        public ICommand ChangeAccountCommand           { get; }
        public ICommand DeleteAccountCommand           { get; }
        public ICommand SeasonPassCommand              { get; }
        public ICommand SetDevEntitlementCommand       { get; }
        public ICommand SubmitFeedbackCommand          { get; }
        public ICommand EmailSupportCommand            { get; }
        public ICommand CloseCommand                   { get; }

        /// <summary>
        /// Raised when the Close link is tapped. Settings' BindingContext is
        /// this ViewModel, not MainViewModel (see AddPageToHost in
        /// MainPage.xaml.cs), so the tab-switch logic that actually closes
        /// Settings can't be reached via a XAML binding — MainPage.xaml.cs
        /// subscribes to this event and forwards to
        /// MainViewModel.CloseSettingsCommand.
        /// </summary>
        public event EventHandler? CloseRequested;

        // ── Constructor ───────────────────────────────────────────────────
        public SettingsViewModel(
            GameDataApiService apiService,
            FollowService followService,
            PersonalGameService personalGameService,
            SharedNavigationStateService navState,
            TeamCacheService teamCache,
            UserApiService userApi,
            AuthService authService,
            FeedbackService feedbackService,
            EntitlementService entitlementService,
            ContentApiService contentApi)
            : base(followService)
        {
            _apiService          = apiService;
            _personalGameService = personalGameService;
            _navState            = navState;
            _teamCache           = teamCache;
            _userApi             = userApi;
            _authService         = authService;
            _feedbackService     = feedbackService;
            _entitlementService  = entitlementService;
            _contentApi          = contentApi;

            TierFilters.Add("All");
            TierFilters.Add("♥ Personal");
            TierFilters.Add("── Rivalries ──");
            TierFilters.Add("🔥 Epic");
            TierFilters.Add("⭐ National");
            TierFilters.Add("🏠 Regional");
            TierFilters.Add("• Meh");

            // No outer Task.Run — LoadDataAsync runs on the main thread; the team +
            // rivalry fetch inside it is offloaded via Task.Run and the continuation
            // (ApplyTeamFilter / ApplyGamesFilter) returns to the main thread.
            LoadDataCommand = new Microsoft.Maui.Controls.Command(() => _ = LoadDataAsync());
            RefreshCommand  = new Microsoft.Maui.Controls.Command(() => _ = LoadDataAsync());

            SelectViewCommand = new Microsoft.Maui.Controls.Command<string>(view =>
            {
                SelectedView = view;
            });

            TogglePersonalCommand = new Microsoft.Maui.Controls.Command<RivalryInfo>(rivalry =>
            {
                if (rivalry == null) return;
                _personalGameService.Toggle(rivalry.Team1Id, rivalry.Team2Id);
                rivalry.IsGameFavorited = _personalGameService.IsFavorited(
                    rivalry.Team1Id, rivalry.Team2Id);

                if (!rivalry.IsGameFavorited && _selectedTier == "♥ Personal")
                    ApplyGamesFilter();
            });

            // Toggle team follow. FollowService.Toggle flips + persists state and raises
            // TeamFollowChanged, which OnTeamFollowChanged handles to refresh team and
            // rivalry follow flags and re-filter both lists. Drives the Teams-tab follow
            // icon and the per-team hearts on the Games cards.
            ToggleFollowCommand = new Microsoft.Maui.Controls.Command<int>(teamId =>
                _followService.Toggle(teamId));

            ToggleSectionCommand = new Command<string>(section =>
            {
                _expandedSection = _expandedSection == section ? null : section;
                OnPropertyChanged(nameof(IsUserProfileExpanded));
                OnPropertyChanged(nameof(IsUserConfigExpanded));
                OnPropertyChanged(nameof(IsFollowingExpanded));
                OnPropertyChanged(nameof(IsSeasonPassExpanded));
                OnPropertyChanged(nameof(IsContentExpanded));
                OnPropertyChanged(nameof(IsFeedbackExpanded));
                OnPropertyChanged(nameof(IsDebugLogExpanded));
            });

            SelectDefaultWeekCommand = new Microsoft.Maui.Controls.Command<string>(value =>
            {
                DefaultWeek = value;
            });

            SelectDefaultConferenceCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var options = new List<string> { "All" };
                options.AddRange(ConferenceHelper.OrderedConferences.Select(c => c.Display));

                var result = await Shell.Current.DisplayActionSheet(
                    "Default Conference", "Cancel", null, options.ToArray());

                if (result != null && result != "Cancel")
                    DefaultConference = result == "All" ? "All"
                        : ConferenceHelper.DisplayToAbbr(result) ?? result;
            });

            SelectDefaultTeamCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                // Settings may be opened before My Teams has ever loaded —
                // make sure the team list is warm before building the sheet.
                await _teamCache.EnsureLoadedAsync();

                var options = new List<string> { "None" };
                options.AddRange(_teamCache.Teams.OrderBy(t => t.TeamName).Select(t => t.TeamName));

                var result = await Shell.Current.DisplayActionSheet(
                    "Default Team", "Cancel", null, options.ToArray());

                if (result == null || result == "Cancel") return;

                if (result == "None")
                {
                    await _followService.SetPrimaryTeam(null);
                }
                else
                {
                    var team = _teamCache.Teams.FirstOrDefault(t => t.TeamName == result);
                    if (team != null)
                        await _followService.SetPrimaryTeam(team.TeamID);
                }

                OnPropertyChanged(nameof(DefaultTeamDisplay));
            });

            SelectDefaultLandingPageCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var options = new[] { "My Teams", "Games", "Rankings", "Postseason", "Sandbox", "Settings" };

                var result = await Shell.Current.DisplayActionSheet(
                    "Default Landing Page", "Cancel", null, options);

                if (result == null || result == "Cancel") return;

                // Maps display label -> the same string keys GetInitialTabIndex
                // in MainPage.xaml.cs switches on.
                DefaultLandingPage = result switch
                {
                    "My Teams"   => "MyTeams",
                    "Games" => "Games",
                    "Rankings"   => "Rankings",
                    "Postseason" => "Postseason",
                    "Sandbox"    => "Sandbox",
                    "Settings"   => "Settings",
                    _            => "MyTeams"
                };
            });

            // Prompts for a new handle, PATCHes it, and only updates the
            // bound Handle (and therefore IsDefaultHandle) on success — a
            // failed/duplicate-handle response leaves the displayed value
            // untouched rather than showing something that didn't save.
            EditHandleCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var result = await Shell.Current.DisplayPromptAsync(
                    "User Name", "Choose a display name", initialValue: Handle, maxLength: 32);

                if (string.IsNullOrWhiteSpace(result) || result.Trim() == Handle) return;

                var trimmed = result.Trim();
                var ok = await _userApi.UpdateHandleAsync(trimmed);
                if (ok)
                    Handle = trimmed;
                else
                    StatusMessage = "Couldn't update user name — it may already be taken.";
            });

            EditEmailCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var result = await Shell.Current.DisplayPromptAsync(
                    "Email", "Enter your email address",
                    initialValue: Email, keyboard: Keyboard.Email, maxLength: 254);

                if (string.IsNullOrWhiteSpace(result) || result.Trim() == Email) return;

                var trimmed = result.Trim();
                var ok = await _userApi.UpdateEmailAsync(trimmed);
                if (ok)
                    Email = trimmed;
                else
                    StatusMessage = "Couldn't update email — it may already be in use.";
            });

            // The phone endpoint bundles marketing SMS consent into the same
            // PATCH as the number itself (see UserController — there's no
            // separate consent-only endpoint, unlike email consent below),
            // so this asks both in one flow rather than exposing SMS consent
            // as a standalone, always-editable toggle that would have
            // nothing to save on its own.
            EditPhoneCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var result = await Shell.Current.DisplayPromptAsync(
                    "Phone Number", "Enter your phone number",
                    initialValue: PhoneNumber, keyboard: Keyboard.Telephone, maxLength: 20);

                if (string.IsNullOrWhiteSpace(result)) return;
                var trimmed = result.Trim();

                var consent = await Shell.Current.DisplayAlert(
                    "Text Alerts", "OK to text you game and score alerts at this number?", "Yes", "No");

                var ok = await _userApi.UpdatePhoneAsync(trimmed, consent);
                if (ok)
                {
                    PhoneNumber = trimmed;
                    MarketingSmsConsent = consent;
                }
                else
                {
                    StatusMessage = "Couldn't update phone number.";
                }
            });

            // Two distinct actions now, not one button whose behavior was
            // inferred from HasAccount (2026-07-22). See TryLoginAsync/
            // TryCreateAccountAsync below for what each actually does.
            LoginCommand = new Microsoft.Maui.Controls.Command(async () => await TryLoginAsync());
            CreateAccountCommand = new Microsoft.Maui.Controls.Command(async () => await TryCreateAccountAsync());

            LogoutCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                await _authService.LogoutAsync();
                ClearLocalAccountState();
            });

            // Logout + immediately re-open Auth0's Universal Login, for the
            // "wrong account is signed in" case. Reuses LogoutAsync/
            // ClearLocalAccountState (same as LogoutCommand) followed by the
            // existing TryLoginAsync — no new Auth0-facing logic. LogoutAsync
            // hits Auth0's own /v2/logout endpoint in the system browser,
            // which clears the SSO session there too, so the login screen
            // that follows isn't just silently re-authenticating the same
            // account via a lingering browser cookie.
            ChangeAccountCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                await _authService.LogoutAsync();
                ClearLocalAccountState();
                await TryLoginAsync();
            });

            // Permanent, server-side deletion (2026-07-26) — confirms first,
            // then calls DeleteAccountAsync (which itself writes a permanent
            // AccountAuditLog entry server-side before removing everything
            // else), then clears the Auth0 session and all local state the
            // same way LogoutCommand does, since the account no longer
            // exists to log back into.
            DeleteAccountCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var confirmed = await Shell.Current.DisplayAlert(
                    "Delete Account",
                    "This permanently deletes your account and all associated data — followed teams, followed games, and season pass history. This cannot be undone.",
                    "Delete", "Cancel");

                if (!confirmed) return;

                var ok = await _userApi.DeleteAccountAsync();
                if (!ok)
                {
                    StatusMessage = "Couldn't delete account — try again.";
                    return;
                }

                await _authService.LogoutAsync();
                ClearLocalAccountState();

                await Shell.Current.DisplayAlert(
                    "Account Deleted", "Your account and all associated data have been permanently deleted.", "OK");
            });

            // Placeholder — Stripe isn't wired up yet (separate feature), and
            // real Apple/Google IAP isn't scoped yet either (2026-07-26
            // decision: keep this a placeholder for now). The login-check
            // itself lives in EntitlementService.EnsureLoggedInForPurchaseAsync,
            // shared with MyTeamsViewModel's gated Details paywall message —
            // one method means IAP only needs wiring up in one place once it
            // exists. This command just applies the freshly-fetched profile
            // to its own local bound properties if a new login just happened
            // (EntitlementService already has it either way).
            SeasonPassCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var result = await _entitlementService.EnsureLoggedInForPurchaseAsync();
                if (!result.CanProceed) return;

                if (result.FreshProfile != null)
                {
                    ApplyProfile(result.FreshProfile);
                    IsLoggedIn = true;
                }

                await Shell.Current.DisplayAlert(
                    "Season Pass", "Coming soon — payment isn't wired up yet.", "OK");
            });

            // Admin-only dev toggle — replaces the "Get Season Pass" link in
            // the new Season Pass panel when IsAdmin, letting an admin flip
            // their own entitlement on/off to verify both experiences
            // without a real purchase. Server enforces the IsAdmin check
            // independently (UserProfileService.SetDevEntitlementAsync) —
            // this command being hidden from non-admins client-side is a UX
            // convenience, not the actual security boundary.
            SetDevEntitlementCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                if (!IsAdmin) return;

                var target = !HasSeasonPass;
                var profile = await _userApi.SetDevEntitlementAsync(target);
                if (profile != null)
                {
                    ApplyProfile(profile);
                }
                else
                {
                    StatusMessage = "Couldn't update entitlement — try again.";
                }
            });

            SubmitFeedbackCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                if (!CanSubmitFeedback) return;

                IsSendingFeedback = true;
                FeedbackStatus = string.Empty;

                var ok = await _feedbackService.SubmitFeedbackAsync(FeedbackText);

                IsSendingFeedback = false;
                if (ok)
                {
                    FeedbackText = string.Empty;
                    FeedbackStatus = "Thanks — feedback sent!";
                }
                else
                {
                    FeedbackStatus = "Couldn't send — try again.";
                }
            });

            // Opens the device's mail app with SupportEmail pre-addressed.
            // No-ops quietly if content hasn't loaded yet (SupportEmail
            // still empty) rather than opening a blank mailto: link.
            EmailSupportCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                if (string.IsNullOrWhiteSpace(SupportEmail)) return;

                try
                {
                    await Launcher.Default.OpenAsync(new Uri($"mailto:{SupportEmail}"));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Settings] Error opening mail app: {ex.Message}");
                    StatusMessage = "Couldn't open your mail app.";
                }
            });

            CloseCommand = new Microsoft.Maui.Controls.Command(() =>
                CloseRequested?.Invoke(this, EventArgs.Empty));

            ClearLogCommand = new Microsoft.Maui.Controls.Command(() =>
            {
                AppLogger.Clear();
                OnPropertyChanged(nameof(LogEntryCount));
            });

            // Pulls recent server-side log entries (GameScorePollingService,
            // etc. — see ServerLogService/InMemoryLoggerProvider on the Api
            // side) and merges them alongside on-device entries. A failed
            // fetch (network error, non-admin, etc.) leaves existing entries
            // untouched rather than clearing anything.
            RefreshLogCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var remote = await _userApi.GetServerLogsAsync();
                if (remote != null)
                {
                    AppLogger.MergeRemote(remote);
                    OnPropertyChanged(nameof(LogEntryCount));
                }
                else
                {
                    StatusMessage = "Couldn't refresh server logs.";
                }
            });

            // Keep LogEntryCount in sync as entries are added/removed
            AppLogger.Entries.CollectionChanged += (s, e) =>
                OnPropertyChanged(nameof(LogEntryCount));

            _followService.TeamFollowChanged         += OnTeamFollowChanged;
            _followService.PrimaryTeamChanged        += _ => OnPropertyChanged(nameof(DefaultTeamDisplay));
            _personalGameService.GameFavoritedChange += OnGameFavoritedChange;

            _navState.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SharedNavigationStateService.SelectedConference))
                {
                    ApplyTeamFilter();
                    OnPropertyChanged(nameof(IsConferenceAll));
                    OnPropertyChanged(nameof(IsConferenceNotAll));
                    OnPropertyChanged(nameof(ShowFlatTeamsList));
                    OnPropertyChanged(nameof(ShowGroupedTeamsList));
                }
                if (e.PropertyName == nameof(SharedNavigationStateService.DefaultWeek))
                    OnPropertyChanged(nameof(DefaultWeek));
                if (e.PropertyName == nameof(SharedNavigationStateService.DefaultConference))
                    OnPropertyChanged(nameof(DefaultConference));
            };
        }

        /// <summary>Shared by LogoutCommand and DeleteAccountCommand - both
        /// end with the same "nobody's logged in anymore" local state.</summary>
        private void ClearLocalAccountState()
        {
            IsLoggedIn = false;
            Handle = string.Empty;
            Email = string.Empty;
            PhoneNumber = string.Empty;
            MarketingSmsConsent = false;
            _marketingEmailConsent = false;
            OnPropertyChanged(nameof(MarketingEmailConsent));
            HasSeasonPass = false;
            IsAdmin = false;
            SeasonPassEntries.Clear();
            _entitlementService.Clear();
        }

        // ── Auth actions ──────────────────────────────────────────────────

        /// <summary>
        /// Login flow: Auth0 auth, then a fetch-only profile lookup (never
        /// creates — see UserApiService.GetMeAsync / UserController.GetMe).
        /// A successful Auth0 login with no matching profile is surfaced as
        /// "no account found," not silently treated as logged in — that's
        /// the whole point of splitting Login from Create Account. Shared by
        /// LoginCommand and the Season Pass "log in first" prompt so both
        /// have identical no-account handling. Returns true only if an
        /// actual account was found and IsLoggedIn is now true.
        /// </summary>
        private async Task<bool> TryLoginAsync()
        {
            var authOk = await _authService.LoginAsync(isSignup: false);
            if (!authOk)
            {
                StatusMessage = "Login failed — try again.";
                return false;
            }

            var profile = await _userApi.GetMeAsync();
            if (profile == null)
            {
                StatusMessage = "No account found for that login. Try again, or tap Create Account.";
                return false;
            }

            ApplyProfile(profile);
            IsLoggedIn = true;
            StatusMessage = string.Empty;
            return true;
        }

        /// <summary>
        /// Create Account flow: Auth0 signup, then the one endpoint allowed
        /// to create a profile (UserApiService.CreateAccountAsync ->
        /// POST /user/me). A 409 (account already exists for this identity,
        /// or the email's already in use by a different account) surfaces
        /// the server's own conflict message rather than a generic failure.
        /// </summary>
        private async Task<bool> TryCreateAccountAsync()
        {
            var authOk = await _authService.LoginAsync(isSignup: true);
            if (!authOk)
            {
                StatusMessage = "Sign up failed — try again.";
                return false;
            }

            var outcome = await _userApi.CreateAccountAsync(_authService.LastLoginEmail);

            if (outcome.IsConflict)
            {
                StatusMessage = outcome.ConflictMessage ?? "That account already exists.";
                return false;
            }

            if (!outcome.IsSuccess || outcome.Profile == null)
            {
                StatusMessage = "Couldn't create account — try again.";
                return false;
            }

            ApplyProfile(outcome.Profile);
            IsLoggedIn = true;
            StatusMessage = string.Empty;
            return true;
        }

        /// <summary>Applies a fetched/created profile's fields — shared by
        /// LoadDataAsync (passive startup fetch), both auth actions above,
        /// and SetDevEntitlementCommand, so there's one place that knows how
        /// a UserProfileDto maps onto this ViewModel's bound properties.
        ///
        /// Sets the MarketingEmailConsent backing field directly (not via
        /// its public setter) - going through the setter would fire an
        /// unwanted save-to-server PATCH every time a profile loads.</summary>
        private void ApplyProfile(UserProfileDto profile)
        {
            Handle = profile.Handle;
            Email = profile.Email ?? string.Empty;
            PhoneNumber = profile.PhoneNumber ?? string.Empty;
            MarketingSmsConsent = profile.MarketingSmsConsent;
            _marketingEmailConsent = profile.MarketingEmailConsent;
            OnPropertyChanged(nameof(MarketingEmailConsent));
            HasSeasonPass = profile.IsEntitled;
            IsAdmin = profile.IsAdmin;

            RebuildSeasonPassEntries(profile.Entitlements);

            // Keep the shared EntitlementService in lockstep so
            // PowerRankingsViewModel/ScheduleViewModel/MyTeamsViewModel/
            // PostseasonViewModel/SandboxViewModel see the same state
            // without depending on this ViewModel directly.
            _entitlementService.ApplyProfile(profile);
        }

        /// <summary>Builds ContentSections from a fetched document, skipping
        /// any section with no content yet. Called once per LoadDataAsync -
        /// content doesn't change often enough to warrant re-fetching on
        /// every panel expand.</summary>
        private void ApplyContent(ApplicationContentDocument? content)
        {
            SupportEmail = content?.SupportEmail ?? string.Empty;

            ContentSections.Clear();
            if (content == null) return;

            void AddSection(string fallbackTitle, ContentSection section)
            {
                if (string.IsNullOrWhiteSpace(section.Content)) return;

                ContentSections.Add(new ContentSectionViewModel
                {
                    Title = string.IsNullOrWhiteSpace(section.Title) ? fallbackTitle : section.Title,
                    Html = Markdig.Markdown.ToHtml(section.Content)
                });
            }

            AddSection("About J1S Sports", content.About);
            AddSection("Privacy Policy", content.PrivacyPolicy);
            AddSection("Terms of Service", content.TermsOfService);
            AddSection("Season Pass", content.SeasonPass);
            AddSection("FAQ", content.Faq);
            AddSection("Announcements", content.Announcements);
            AddSection("Release Notes", content.ReleaseNotes);
        }

        /// <summary>
        /// Builds the Season Pass panel's catalog view from the raw
        /// entitlement list: one row per distinct ProductKey actually held
        /// (deduped to the latest-expiring row per key - see
        /// SeasonPassEntries' doc comment for why), plus a synthetic
        /// "Purchase" row for the current season if not already held.
        /// "Current season" = the current calendar year, per the 2026-07-26
        /// example (today being mid-2026, "set up 2026" means this year).
        /// The bare, non-seasoned key (the admin dev-toggle sentinel) is
        /// never offered as a Purchase row - only real dated products are.
        /// </summary>
        private void RebuildSeasonPassEntries(List<EntitlementSummaryDto> allEntitlements)
        {
            SeasonPassEntries.Clear();

            var latestPerProduct = allEntitlements
                .GroupBy(e => e.ProductKey)
                .Select(g => g.OrderByDescending(e => e.ExpiryDate ?? DateTime.MinValue).First())
                .OrderByDescending(e => e.ExpiryDate);

            foreach (var e in latestPerProduct)
            {
                SeasonPassEntries.Add(new SeasonPassEntryViewModel
                {
                    ProductKey = e.ProductKey,
                    IsActive = e.IsActive,
                    IsPurchasable = false,
                    DisplayLine = e.DisplayLine
                });
            }

            const string baseProductKey = "cfb-season-pass";
            var currentSeasonKey = $"{baseProductKey}-{DateTime.Now.Year}";
            var alreadyHasCurrentSeason = allEntitlements.Any(e => e.ProductKey == currentSeasonKey && e.IsActive);

            if (!alreadyHasCurrentSeason)
            {
                SeasonPassEntries.Add(new SeasonPassEntryViewModel
                {
                    ProductKey = currentSeasonKey,
                    IsActive = false,
                    IsPurchasable = true,
                    DisplayLine = null
                });
            }
        }


        public async Task LoadDataAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusMessage = "Loading...";

            try
            {
                var teamsTask     = Task.Run(() => _apiService.GetTeamsAsync());
                var rivalriesTask = Task.Run(() => _apiService.GetNamedRivalriesAsync());
                var profileTask   = _userApi.GetMeAsync();
                var contentTask   = _contentApi.GetContentAsync();

                // DefaultTeamDisplay reads _teamCache.GetTeam(id), not
                // _allTeams below — a completely separate cache from the
                // GetTeamsAsync() call above, which only feeds this VM's own
                // Favorites/Teams tab. If FollowService.GetPrimaryTeamId()
                // resolves before TeamCacheService finishes loading,
                // GetTeam(id) returns null and DefaultTeamDisplay silently
                // falls back to "None" — indistinguishable from "no primary
                // team set." SelectDefaultTeamCommand already guards against
                // this for the picker; LoadDataAsync didn't for the display
                // itself. Awaited alongside the rest so Settings never shows
                // a stale/empty read on this specific field.
                var teamCacheTask = _teamCache.EnsureLoadedAsync();

                await Task.WhenAll(teamsTask, rivalriesTask, profileTask, contentTask, teamCacheTask);
                var (teams, rivalries, profile, content) =
                    (teamsTask.Result, rivalriesTask.Result, profileTask.Result, contentTask.Result);

                // Now that both FollowService's primary team id and
                // TeamCacheService are guaranteed loaded, force a
                // re-evaluation regardless of which one happened to resolve
                // first — covers the race in both directions, not just the
                // order this method happens to await in.
                OnPropertyChanged(nameof(DefaultTeamDisplay));

                if (teams != null && teams.Count > 0)
                {
                    foreach (var t in teams)
                        t.IsFollowed = _followService.IsFollowed(t.TeamID);

                    _allTeams = [.. teams.OrderBy(t => t.TeamName)];
                    ApplyTeamFilter();
                }

                // IsLoggedIn is derived from "did GetMeAsync find a profile,"
                // not a separate IsAuthenticatedAsync() token check — a
                // valid Auth0 token with no matching server-side profile is
                // NOT logged in, from this ViewModel's perspective. Keeps
                // this in lockstep with TryLoginAsync/TryCreateAccountAsync.
                if (profile != null)
                {
                    ApplyProfile(profile);
                    IsLoggedIn = true;
                }
                else
                {
                    IsLoggedIn = false;
                }

                ApplyContent(content);

                var allRivalries = rivalries ?? [];
                var followedIds  = _followService.GetFollowedIds();

                foreach (var r in allRivalries)
                {
                    r.Team1IsFollowed = followedIds.Contains(r.Team1Id);
                    r.Team2IsFollowed = followedIds.Contains(r.Team2Id);
                    r.IsGameFavorited = _personalGameService.IsFavorited(r.Team1Id, r.Team2Id);
                }

                _allRivalries = [.. allRivalries
                    .OrderBy(r => TierSortOrder(r.RivalryTier))
                    .ThenBy(r => r.RivalryName)];

                await LoadPersonalGamesAsync(_allRivalries, followedIds);

                _selectedTier = "♥ Personal";
                OnPropertyChanged(nameof(SelectedTier));
                ApplyGamesFilter();

                StatusMessage = string.Empty;
                HasLoaded = true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadPersonalGamesAsync(
            List<RivalryInfo> namedRivalries,
            HashSet<int> followedIds)
        {
            _personalGames.Clear();

            var namedKeys = namedRivalries
                .Select(r => PersonalGameService.Key(r.Team1Id, r.Team2Id))
                .ToHashSet();

            var personalKeys = _personalGameService.GetAll()
                .Where(k => !namedKeys.Contains(k))
                .ToList();

            if (personalKeys.Count == 0) return;

            var tasks = personalKeys.Select(async key =>
            {
                var (id1, id2) = PersonalGameService.ParseKey(key);
                var info       = await _apiService.GetMatchupHistoryAsync(id1, id2);
                if (info != null)
                {
                    info.Team1IsFollowed = followedIds.Contains(id1);
                    info.Team2IsFollowed = followedIds.Contains(id2);
                    info.IsGameFavorited = true;
                }
                return info;
            });

            var results = await Task.WhenAll(tasks);

            _personalGames = results
                .Where(r => r != null)
                .OrderBy(r => r!.Team1Name)
                .ToList()!;
        }

        // ── Filters ───────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds either the flat Teams list (a specific conference
        /// selected) or the grouped TeamGroups list ("All" selected) - the
        /// two are mutually exclusive, matching which CollectionView
        /// SettingsPage.xaml shows via IsConferenceAll. Same "followed
        /// teams first" sort in both cases.
        /// </summary>
        private void ApplyTeamFilter()
        {
            if (IsConferenceAll)
            {
                Teams.Clear();
                RebuildTeamGroups();
                return;
            }

            TeamGroups.Clear();

            // SelectedConference already stores the abbreviation — compare directly.
            // (The old DisplayToAbbr call treated it as a display name and, after the
            //  abbreviation refactor, silently filtered the Teams list to nothing.)
            var conf = _navState.SelectedConference;
            var filtered = _allTeams.Where(t =>
                t.ConferenceAbbr != null &&
                t.ConferenceAbbr.Equals(conf, StringComparison.OrdinalIgnoreCase));

            var sorted = filtered
                .OrderByDescending(t => t.IsFollowed)
                .ThenBy(t => t.TeamName);

            Teams.Clear();
            foreach (var t in sorted)
                Teams.Add(t);
        }

        private void RebuildTeamGroups()
        {
            // Preserve which groups were already expanded across a rebuild
            // (e.g. after a follow toggle re-sorts within groups) rather than
            // collapsing everything every time.
            var previouslyExpanded = TeamGroups
                .Where(g => g.IsExpanded)
                .Select(g => g.ConferenceName)
                .ToHashSet();

            var groups = _allTeams
                .GroupBy(t => t.ConferenceAbbr ?? "Independent")
                .OrderBy(g => g.Key)
                .Select(g => new ConferenceGroupInfo
                {
                    ConferenceName = g.Key,
                    IsExpanded = previouslyExpanded.Contains(g.Key),
                    Teams = new ObservableCollection<TeamInfo>(
                        g.OrderByDescending(t => t.IsFollowed).ThenBy(t => t.TeamName))
                });

            TeamGroups.Clear();
            foreach (var group in groups)
                TeamGroups.Add(group);
        }

        private void ApplyGamesFilter()
        {
            Games.Clear();

            IEnumerable<RivalryInfo> filtered;

            if (_selectedTier == "♥ Personal")
            {
                var namedPersonal = _allRivalries.Where(r => r.IsGameFavorited);
                filtered = namedPersonal.Concat(_personalGames)
                    .OrderBy(r => r.RivalryName ?? $"{r.Team1Name} vs {r.Team2Name}");
            }
            else
            {
                filtered = _selectedTier switch
                {
                    "🔥 Epic"     => _allRivalries.Where(r => r.RivalryTier == "EPIC"),
                    "⭐ National" => _allRivalries.Where(r => r.RivalryTier == "NATIONAL"),
                    "🏠 Regional" => _allRivalries.Where(r => r.RivalryTier == "STATE"),
                    "• Meh"       => _allRivalries.Where(r => r.RivalryTier == "MEH"),
                    _             => _allRivalries.AsEnumerable()
                };
            }

            foreach (var r in filtered)
                Games.Add(r);
        }

        // ── Event handlers ────────────────────────────────────────────────
        private void OnTeamFollowChanged(int teamId, bool isFollowed)
        {
            var team = _allTeams.FirstOrDefault(t => t.TeamID == teamId);
            if (team != null)
            {
                team.IsFollowed = isFollowed;
                ApplyTeamFilter();
            }

            foreach (var r in _allRivalries.Concat(_personalGames))
            {
                if (r.Team1Id == teamId) r.Team1IsFollowed = isFollowed;
                if (r.Team2Id == teamId) r.Team2IsFollowed = isFollowed;
            }
            ApplyGamesFilter();
        }

        private void OnGameFavoritedChange(string key, bool isFollowed)
        {
            var rivalry = _allRivalries.FirstOrDefault(r =>
                PersonalGameService.Key(r.Team1Id, r.Team2Id) == key);
            if (rivalry != null)
                rivalry.IsGameFavorited = isFollowed;

            var personalMatch = _personalGames.FirstOrDefault(r =>
                PersonalGameService.Key(r.Team1Id, r.Team2Id) == key);

            if (isFollowed && rivalry == null && personalMatch == null)
            {
                _ = LoadDataAsync();
                return;
            }

            if (!isFollowed && personalMatch != null)
                _personalGames.Remove(personalMatch);

            if (_selectedTier == "♥ Personal")
                ApplyGamesFilter();
        }

        private static int TierSortOrder(string? tier) => tier switch
        {
            "EPIC"     => 0,
            "NATIONAL" => 1,
            "STATE"    => 2,
            "MEH"      => 3,
            _          => 4
        };
    }

    /// <summary>
    /// One row in the Season Pass panel - either a product the user
    /// actually holds (IsPurchasable false, status is Active/Expired) or a
    /// synthetic "you could buy this" row (IsPurchasable true). Plain
    /// immutable class, not INotifyPropertyChanged - unlike
    /// ConferenceGroupInfo/ContentSectionViewModel, these are rebuilt fresh
    /// each time rather than mutated in place, so there's no state to notify
    /// changes on.
    /// </summary>
    public class SeasonPassEntryViewModel
    {
        public required string ProductKey { get; init; }
        public required bool IsActive { get; init; }
        public required bool IsPurchasable { get; init; }
        public string? DisplayLine { get; init; }

        public bool HasDisplayLine => !string.IsNullOrWhiteSpace(DisplayLine);

        /// <summary>
        /// Single-valued status used to drive the status label's text AND
        /// color via one set of mutually-exclusive DataTriggers, rather than
        /// juggling IsPurchasable/IsActive as separate booleans in XAML
        /// (which risks two triggers matching the same Setter simultaneously).
        /// </summary>
        public string StatusKey => IsPurchasable ? "Purchase" : (IsActive ? "Active" : "Expired");
    }

    /// <summary>
    /// One conference's worth of teams, independently expandable - backs
    /// SettingsPage.xaml's Favorites panel when the header conference filter
    /// is "All". Plain INotifyPropertyChanged rather than a full ViewModel
    /// since it's a display-only grouping wrapper, not a service-backed page.
    /// </summary>
    public class ConferenceGroupInfo : INotifyPropertyChanged
    {
        public required string ConferenceName { get; init; }
        public required ObservableCollection<TeamInfo> Teams { get; init; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public ICommand ToggleCommand { get; }

        public ConferenceGroupInfo()
        {
            ToggleCommand = new Microsoft.Maui.Controls.Command(() => IsExpanded = !IsExpanded);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One content section (About/Privacy/Terms/etc.), pre-rendered to HTML
    /// via Markdig and independently expandable - backs the Content panel.
    /// </summary>
    public class ContentSectionViewModel : INotifyPropertyChanged
    {
        public required string Title { get; init; }
        public required string Html { get; init; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public ICommand ToggleCommand { get; }

        public ContentSectionViewModel()
        {
            ToggleCommand = new Microsoft.Maui.Controls.Command(() => IsExpanded = !IsExpanded);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
