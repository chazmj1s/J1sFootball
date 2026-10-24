using SaturdayPulse.Models;
using SaturdayPulse.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;

namespace SaturdayPulse.ViewModels
{
    public class PowerRankingsViewModel : BaseViewModel
    {
        private readonly GameDataApiService           _apiService;
        private readonly RankingsCacheService         _rankingsCache;
        private readonly SharedNavigationStateService _navState;
        private readonly EntitlementService           _entitlementService;
        private List<TeamRanking> _allTeams = new();
        private ObservableCollection<TeamRanking> _filteredTeams = new();
        private bool          _isBusy;
        private RankingFilter _currentFilter         = RankingFilter.All;
        private RankingSort   _currentSort           = RankingSort.Rank;
        private bool          _isSortAscending       = true;
        private string        _selectedFilterDisplay = "All";
        private string        _statusMessage = "Loading...";
        private string        _emptyMessage = "Loading...";

        // ── Teams | Conferences sub-view (2026-09-26) ─────────────────────
        private string        _selectedView          = "Teams";
        private bool          _isRefreshing;
        private ObservableCollection<ConferenceSummary> _conferenceSummaries = new();
        private ObservableCollection<Top25Card> _top25Left  = new();
        private ObservableCollection<Top25Card> _top25Right = new();
        private RankingSort   _confSort              = RankingSort.Rank;
        private bool          _isConfSortAscending   = true;

        // ── Top 25 source: J1s | AP | Coaches (2026-10-18) ─────────────────
        // J1s = existing OverallRank top 25. AP/Coaches = CFBD polls for the
        // same (year, week), fetched alongside rankings in LoadDataAsync.
        private const string SourceJ1s     = "J1s";
        private const string SourceAp      = "AP";
        private const string SourceCoaches = "Coaches";
        private string            _top25Source = SourceJ1s;
        private PollWeekResponse? _polls;


        public PowerRankingsViewModel(
            GameDataApiService apiService,
            RankingsCacheService rankingsCache,
            FollowService followService,
            SharedNavigationStateService navState,
            EntitlementService entitlementService)
            : base(followService)
        {
            _apiService    = apiService;
            _rankingsCache = rankingsCache;
            _navState      = navState;
            _entitlementService = entitlementService;

            // No outer Task.Run — LoadDataAsync runs on the main thread; the HTTP
            // call inside it is offloaded via its own Task.Run, and the continuation
            // (ApplyFiltersAndSort) returns to the main thread.
            LoadDataCommand = new Microsoft.Maui.Controls.Command(() => _ = LoadDataAsync());
            RefreshCommand  = new Microsoft.Maui.Controls.Command(async () => await RefreshAsync());
            ApplyFilterCommand = new Microsoft.Maui.Controls.Command<string>(ApplyFilter);
            ApplySortCommand      = new Microsoft.Maui.Controls.Command<RankingSort>(ApplySort);
            SortColumnCommand     = new Microsoft.Maui.Controls.Command<string>(SortByColumn);

            SelectFilterCommand = new Microsoft.Maui.Controls.Command(async () =>
            {
                var options = new List<string> { "All", "Top 25", "── Tier ──", "P4", "G5", "Independent" };
                
                var result = await Shell.Current.DisplayActionSheet("Filter", "Cancel", null, options.ToArray());

                if (result != null && result != "Cancel" && !result.StartsWith("──"))
                {
                    SelectedFilterDisplay = result;
                    ApplyFilter(result == "Top 25" ? "Top25" : result);
                }
            });

            ToggleStatsExpandCommand = new Microsoft.Maui.Controls.Command<TeamRanking>(t =>
            {
                if (t == null || !HasSeasonPass) return;
                t.IsStatsExpanded = !t.IsStatsExpanded;
            });

            ToggleTrendExpandCommand = new Microsoft.Maui.Controls.Command<TeamRanking>(async t =>
            {
                if (t == null || !HasSeasonPass) return;

                if (!t.IsTrendExpanded && t.TrendHistory == null)
                {
                    var data = await Task.Run(async () =>
                        await _apiService.GetTeamRollingAveragesAsync(t.TeamID, _navState.SelectedYear));

                    if (data?.History?.Count > 0)
                    {
                        var h = data.History[^1];
                        t.TrendRating = h.TrendRating;
                        t.PedigreeRating = h.PedigreeRating;
                        t.SeedRating = h.SeedRating;
                        t.TrendHistory = h.TrendHistory;
                        t.PedigreeHistory = h.PedigreeHistory;
                    }
                }

                t.IsTrendExpanded = !t.IsTrendExpanded;
            });

            ToggleArcExpandCommand = new Microsoft.Maui.Controls.Command<TeamRanking>(async t =>
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

            ToggleRosterExpandCommand = new Microsoft.Maui.Controls.Command<TeamRanking>(async t =>
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

            // Data is already loaded by the time these are tappable (they only render
            // once RosterChanges is populated), so these are plain synchronous toggles
            // — no fetch, no HasSeasonPass re-check needed beyond the panel already
            // being open. Tapping the active one again closes it; tapping a different
            // one switches the shared list area to that selection.
            ToggleRecruitingListCommand = new Microsoft.Maui.Controls.Command<TeamRanking>(t =>
            {
                if (t == null) return;
                t.ActiveRosterListKey = t.IsRecruitingListActive ? null : "Recruiting";
            });

            TogglePortalInListCommand = new Microsoft.Maui.Controls.Command<TeamRanking>(t =>
            {
                if (t == null) return;
                t.ActiveRosterListKey = t.IsPortalInListActive ? null : "PortalIn";
            });

            TogglePortalOutListCommand = new Microsoft.Maui.Controls.Command<TeamRanking>(t =>
            {
                if (t == null) return;
                t.ActiveRosterListKey = t.IsPortalOutListActive ? null : "PortalOut";
            });

            ToggleScheduleExpandCommand = new Microsoft.Maui.Controls.Command<TeamRanking>(async t =>
            {
                if (t == null) return;

                if (!t.IsScheduleExpanded && t.ScheduleGames == null)
                {
                    var data = await Task.Run(async () =>
                        await _apiService.GetTeamScheduleAsync(t.TeamID, _navState.SelectedYear));

                    if (data?.Games?.Count > 0)
                        t.ScheduleGames = data.Games;
                }

                t.IsScheduleExpanded = !t.IsScheduleExpanded;
            });

            ToggleFollowCommand = new Microsoft.Maui.Controls.Command<TeamRanking>(t =>
            {
                if (t == null) return;
                // FollowService fires TeamFollowChanged, which OnTeamFollowChanged already
                // handles (updates t.IsFollowed on the matching _allTeams entry, re-sorts
                // if ShowFavoritesFirst). No manual state mutation needed here — mirrors
                // TeamsViewModel.ToggleFollow.
                _followService.Toggle(t.TeamID);
            });

            // Team-name deep-link (2026-09-13) — mirrors ScheduleViewModel's
            // command of the same name/shape. This ViewModel has no
            // reference to MyTeamsViewModel, so it only raises the request
            // via SharedNavigationStateService.TeamPreviewRequested.
            NavigateToTeamCommand = new Microsoft.Maui.Controls.Command<int>(teamId =>
            {
                if (teamId == 0) return;
                _navState.RequestTeamPreview(teamId);
            });

            SelectViewCommand = new Microsoft.Maui.Controls.Command<string>(view =>
            {
                if (string.IsNullOrWhiteSpace(view)) return;
                SelectedView = view;
            });

            // Top 25 radio row — the radio is the action: picks the source AND
            // switches to the Top 25 view. Polls are already loaded with the
            // rankings, so switching source is a local rebuild, no server call.
            SelectTop25SourceCommand = new Microsoft.Maui.Controls.Command<string>(source =>
            {
                if (source is not (SourceJ1s or SourceAp or SourceCoaches)) return;

                var changed = _top25Source != source;
                _top25Source = source;
                SelectedView = "Top25";

                if (changed)
                    BuildTop25();

                RaiseTop25SourceProperties();
            });

            // Conference row tap — flip back to Teams filtered to that conference.
            // Both SelectedConference and ApplyFiltersAndSort are set/called here;
            // if the setter also raises FilterChanged(Conference), the second
            // refilter is redundant but harmless.
            NavigateToConferenceCommand = new Microsoft.Maui.Controls.Command<string>(key =>
            {
                if (string.IsNullOrWhiteSpace(key)) return;
                _navState.SelectedConference = key;
                SelectedView = "Teams";
                ApplyFiltersAndSort();
            });

            _navState.PropertyChanged += OnNavStateChanged;
            _followService.TeamFollowChanged += OnTeamFollowChanged;
            _rankingsCache.CacheUpdated += OnRankingsCacheUpdated;
            _entitlementService.EntitlementChanged += OnEntitlementChanged;
        }

        private void OnEntitlementChanged()
        {
            OnPropertyChanged(nameof(HasSeasonPass));
            OnPropertyChanged(nameof(IsNotSeasonPass));
        }

        // ── Bindable collections ──────────────────────────────────────────

        public ObservableCollection<TeamRanking> FilteredTeams
        {
            get => _filteredTeams;
            set { _filteredTeams = value; OnPropertyChanged(); }
        }

        public ObservableCollection<ConferenceSummary> ConferenceSummaries
        {
            get => _conferenceSummaries;
            set { _conferenceSummaries = value; OnPropertyChanged(); }
        }

        /// <summary>Top 25 view, left column — OverallRank 1–12.</summary>
        public ObservableCollection<Top25Card> Top25Left
        {
            get => _top25Left;
            set { _top25Left = value; OnPropertyChanged(); }
        }

        /// <summary>Top 25 view, right column — OverallRank 13–25.</summary>
        public ObservableCollection<Top25Card> Top25Right
        {
            get => _top25Right;
            set { _top25Right = value; OnPropertyChanged(); }
        }

        public bool HasTop25   => _top25Left.Count > 0;
        public bool HasNoTop25 => !HasTop25;

        // ── Top 25 source radio state ─────────────────────────────────────
        // Filled radio only while the Top 25 view is showing, so the row reads
        // as "which list am I looking at," not a hidden setting.

        public bool IsJ1sSourceSelected     => IsTop25View && _top25Source == SourceJ1s;
        public bool IsApSourceSelected      => IsTop25View && _top25Source == SourceAp;
        public bool IsCoachesSourceSelected => IsTop25View && _top25Source == SourceCoaches;

        /// <summary>
        /// Empty-state text for the Top 25 view. J1s mirrors EmptyMessage; a poll
        /// source with no poll for the selected week names the poll and week.
        /// </summary>
        public string Top25EmptyMessage
        {
            get
            {
                if (_top25Source == SourceJ1s || IsBusy)
                    return EmptyMessage;

                var pollName = _top25Source == SourceAp ? "AP Top 25" : "Coaches Poll";
                var week = _navState.SelectedWeek;
                return week > 0
                    ? $"No {pollName} for Week {week}"
                    : $"No {pollName} for this view";
            }
        }

        private void RaiseTop25SourceProperties()
        {
            OnPropertyChanged(nameof(IsJ1sSourceSelected));
            OnPropertyChanged(nameof(IsApSourceSelected));
            OnPropertyChanged(nameof(IsCoachesSourceSelected));
            OnPropertyChanged(nameof(Top25EmptyMessage));
        }

        // ── Bindable properties ───────────────────────────────────────────

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Top25EmptyMessage));
            }
        }

        /// <summary>
        /// Dedicated pull-to-refresh flag, bound OneWay to RefreshView.IsRefreshing.
        /// Deliberately separate from IsBusy — see the RefreshView anti-pattern fix
        /// on Schedule/Settings (2026-09-12).
        /// </summary>
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set { _isRefreshing = value; OnPropertyChanged(); }
        }

        /// <summary>"Teams", "Top25" or "Conferences" — mirrors PostseasonViewModel.SelectedView.</summary>
        public string SelectedView
        {
            get => _selectedView;
            set
            {
                if (_selectedView == value) return;
                _selectedView = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTeamsView));
                OnPropertyChanged(nameof(IsTop25View));
                OnPropertyChanged(nameof(IsConferencesView));
                RaiseTop25SourceProperties();
            }
        }

        public bool IsTeamsView       => _selectedView == "Teams";
        public bool IsTop25View       => _selectedView == "Top25";
        public bool IsConferencesView => _selectedView == "Conferences";

        public string SelectedFilterDisplay
        {
            get => _selectedFilterDisplay;
            set { _selectedFilterDisplay = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }
        public string EmptyMessage
        {
            get => _emptyMessage;
            set
            {
                _emptyMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Top25EmptyMessage));
            }
        }
        public bool   HasLoaded     { get; set; }

        /// <summary>
        /// True only while Rankings is the visible tab. Set by MainPage on tab
        /// switch. When false, the page defers FilterChanged work (marks itself
        /// stale) instead of loading off-screen — so it never renders during launch
        /// or behind another tab. The lazy SyncPage path loads it on first visit.
        /// </summary>
        public bool   IsActive      { get; set; }

        // ── Season Pass gating (2026-07-25) ─────────────────────────────
        // Sourced from the shared EntitlementService — same pattern as
        // MyTeamsViewModel. Gates the Trend/Pedigree, Season Arc, and
        // Offense/Defense toggle links (disabled/grayed, no popup).
        // NOTE: ToggleScheduleExpandCommand below is left ungated — it
        // isn't one of the three link types in the locked design table and
        // doesn't appear wired to any element in the uploaded
        // PowerRankingsPage.xaml. Flag if that changes.
        public bool HasSeasonPass => _entitlementService.HasSeasonPass;

        /// <summary>Inverse of HasSeasonPass — no inverse-bool converter needed in XAML.</summary>
        public bool IsNotSeasonPass => !HasSeasonPass;

        public string ActiveSortLabel => _currentSort switch
        {
            RankingSort.PowerRating => "Rating",
            RankingSort.SOS        => "SOS",
            RankingSort.Record     => "Record",
            RankingSort.TierRank   => "Tier",
            RankingSort.Rank       => "Rank",
            RankingSort.RosterRank => "Roster Rank",
            _                      => "Rating"
        };

        public string GetActiveSortValue(TeamRanking t) => _currentSort switch
        {
            RankingSort.PowerRating => t.DisplayRank,
            RankingSort.SOS        => t.DisplaySOS,
            RankingSort.Record     => t.RecordWithProjection,
            RankingSort.TierRank   => t.DisplayTierWithRank,
            RankingSort.Rank       => t.DisplayRank,
            RankingSort.RosterRank => t.DisplayRosterRank,
            _                      => t.DisplayRank
        };

        // ── Commands ──────────────────────────────────────────────────────

        public ICommand LoadDataCommand          { get; }
        public ICommand RefreshCommand           { get; }
        public ICommand ApplyFilterCommand       { get; }
        public ICommand ApplySortCommand         { get; }
        public ICommand SortColumnCommand        { get; }
        public ICommand SelectFilterCommand      { get; }
        public ICommand ToggleStatsExpandCommand { get; }
        public ICommand ToggleTrendExpandCommand { get; }
        public ICommand ToggleArcExpandCommand   { get; }
        public ICommand ToggleRosterExpandCommand { get; }
        public ICommand ToggleRecruitingListCommand { get; }
        public ICommand TogglePortalInListCommand { get; }
        public ICommand TogglePortalOutListCommand { get; }
        public ICommand ToggleScheduleExpandCommand { get; }
        public ICommand ToggleFollowCommand { get; }
        public ICommand NavigateToTeamCommand { get; }
        public ICommand SelectViewCommand { get; }
        public ICommand SelectTop25SourceCommand { get; }
        public ICommand NavigateToConferenceCommand { get; }

        // ── Load ──────────────────────────────────────────────────────────

        private CancellationTokenSource? _loadCts;

        private async Task RefreshAsync()
        {
            IsRefreshing = true;
            try
            {
                await LoadDataAsync(forceReload: true);
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        public async Task LoadDataAsync(bool forceReload = false)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            IsBusy = true;
            StatusMessage = "Loading rankings...";
            EmptyMessage = "Loading...";
            OnPropertyChanged(nameof(StatusMessage));

            try
            {
                var year = _navState.SelectedYear;
                var week = _navState.SelectedWeek;

                // AP/Coaches polls for the same (year, week), fetched in parallel
                // with rankings. GetWeeklyPollsAsync never throws (returns null on
                // failure), so this task can't fault unobserved if rankings throws.
                // Week 0 is the season-final view — no poll lookup.
                var pollsTask = week > 0
                    ? Task.Run(() => _apiService.GetWeeklyPollsAsync(year, week))
                    : Task.FromResult<PollWeekResponse?>(null);

                var teams = await Task.Run(async () =>
                    await _rankingsCache.GetRankingsAsync(
                        year,
                        week,
                        forceReload), token);

                var polls = await pollsTask;

                if (token.IsCancellationRequested) return;

                // Assigned before ApplyFiltersAndSort so BuildTop25 sees this week's polls.
                _polls = polls;

                if (teams != null && teams.Any())
                {
                    // Follow/Top25 flags are stamped once by RankingsCacheService
                    // on these shared instances — no per-consumer stamping needed.
                    _allTeams = teams.ToList();

                    ApplyFiltersAndSort();
                    StatusMessage = _navState.SelectedWeek > 0
                        ? $"{teams.Count} teams · Wk {_navState.SelectedWeek}"
                        : $"{teams.Count} teams · Final";
                }
                else
                {
                    _allTeams.Clear();
                    ApplyFiltersAndSort();
                    StatusMessage = "No rankings available";
                    EmptyMessage = "No rankings available";
                }

                HasLoaded = true;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                System.Diagnostics.Debug.WriteLine(ex.Message);
                StatusMessage = $"Failed to load rankings. Error: {ex.Message}";
                EmptyMessage = "Failed to load rankings.";
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    IsBusy = false;
                    OnPropertyChanged(nameof(StatusMessage));
                }
            }
        }
        // ── Filter / sort ─────────────────────────────────────────────────

        public void ApplyFilter(string filterType)
        {
            _currentFilter = filterType switch
            {
                "Top25"       => RankingFilter.Top25,
                "P4"          => RankingFilter.P4,
                "G5"          => RankingFilter.G5,
                "Independent" => RankingFilter.Independent,
                _             => RankingFilter.All
            };
            ApplyFiltersAndSort();
        }

        public void ApplySort(RankingSort sortType)
        {
            _currentSort = sortType;
            OnPropertyChanged(nameof(ActiveSortLabel));
            ApplyFiltersAndSort();
        }

        public void SortByColumn(string columnName)
        {
            if (IsConferencesView)
            {
                SortConferencesByColumn(columnName);
                return;
            }

            var newSort = columnName switch
            {
                "Rank"       => RankingSort.Rank,
                "Team"       => RankingSort.TeamName,
                "Record"     => RankingSort.Record,
                "Rating"     => RankingSort.PowerRating,
                "Conference" => RankingSort.Conference,
                "SOS"        => RankingSort.SOS,
                "TierRank"   => RankingSort.TierRank,
                "Tier"       => RankingSort.Tier,
                "RosterRank" => RankingSort.RosterRank,
                _            => RankingSort.Rank
            };

            if (_currentSort == newSort)
                _isSortAscending = !_isSortAscending;
            else
            {
                _currentSort = newSort;
                _isSortAscending = newSort switch
                {
                    RankingSort.PowerRating => false,
                    RankingSort.SOS        => false,
                    _                      => true
                };
            }

            ApplyFiltersAndSort();
            OnPropertyChanged(nameof(ActiveSortLabel));
        }

        /// <summary>
        /// Actual+projected win% — same composite the API now defaults the
        /// Rankings order to (ProductionGameDataService.V2.GetPowerRankingsV2Async,
        /// 2026-08-22). Kept here too so client-side re-sorts on the Record column
        /// (tap to sort, toggle direction) match the API's own ordering instead of
        /// falling back to actual-only Wins/Losses, which ties out everyone at 0-0
        /// before any real games are played.
        /// </summary>
        private static double CompositeWinPct(TeamRanking t)
        {
            var total = t.ProjectedWins + t.ProjectedLosses;
            return total > 0 ? (double)t.ProjectedWins / total : 0.0;
        }

        private void ApplyFiltersAndSort()
        {
            var filtered = _allTeams.AsEnumerable();

            // Conference filter
            var conf = _navState.SelectedConference;
            if (conf != "All")
            {
                filtered = filtered.Where(t =>
                    (t.ConferenceAbbr != null &&
                     t.ConferenceAbbr.Equals(conf, StringComparison.OrdinalIgnoreCase)) ||
                    (t.Conference != null &&
                     t.Conference.Equals(conf, StringComparison.OrdinalIgnoreCase)));
            }

            // Tier / top-25 filter
            filtered = _currentFilter switch
            {
                RankingFilter.Top25       => filtered.Where(t => t.IsTop25),
                RankingFilter.P4          => filtered.Where(t => t.Tier == "P4"),
                RankingFilter.G5          => filtered.Where(t => t.Tier == "G5"),
                RankingFilter.Independent => filtered.Where(t => t.Tier == "Independent"),
                _                         => filtered
            };

            // Column sort
            IOrderedEnumerable<TeamRanking> sorted = _currentSort switch
            {
                RankingSort.Rank => _isSortAscending
                    ? filtered.OrderBy(t => t.OverallRank)
                    : filtered.OrderByDescending(t => t.OverallRank),
                RankingSort.TeamName => _isSortAscending
                    ? filtered.OrderBy(t => t.TeamName)
                    : filtered.OrderByDescending(t => t.TeamName),
                RankingSort.PowerRating => _isSortAscending
                    ? filtered.OrderBy(t => t.Ranking ?? 0)
                    : filtered.OrderByDescending(t => t.Ranking ?? 0),
                RankingSort.Record => _isSortAscending
                    ? filtered.OrderBy(t => CompositeWinPct(t)).ThenBy(t => t.CombinedSOS ?? 0)
                              .ThenBy(t => t.RosterRank ?? int.MaxValue)
                    : filtered.OrderByDescending(t => CompositeWinPct(t)).ThenByDescending(t => t.CombinedSOS ?? 0)
                              .ThenBy(t => t.RosterRank ?? int.MaxValue),
                RankingSort.Conference => _isSortAscending
                    ? filtered.OrderBy(t => t.Conference).ThenBy(t => t.OverallRank)
                    : filtered.OrderByDescending(t => t.Conference).ThenBy(t => t.OverallRank),
                RankingSort.SOS => _isSortAscending
                    ? filtered.OrderBy(t => t.CombinedSOS ?? 0)
                    : filtered.OrderByDescending(t => t.CombinedSOS ?? 0),
                RankingSort.TierRank => _isSortAscending
                    ? filtered.OrderBy(t => t.TierRank)
                    : filtered.OrderByDescending(t => t.TierRank),
                RankingSort.Tier => _isSortAscending
                    ? filtered.OrderBy(t => t.Tier).ThenBy(t => t.TierRank)
                    : filtered.OrderByDescending(t => t.Tier).ThenBy(t => t.TierRank),
                // RosterRank is nullable (no ZRoster computed for the team/year) — unlike
                // the CombinedSOS ?? 0 pattern above, defaulting a missing rank to 0 would
                // make "no data" sort as "#0, best in the country." Grouping HasValue first
                // pins null-roster teams to the bottom of the list in EITHER sort direction;
                // the direction toggle only reorders within the group that has real data.
                RankingSort.RosterRank => _isSortAscending
                    ? filtered.OrderBy(t => t.RosterRank.HasValue ? 0 : 1)
                              .ThenBy(t => t.RosterRank ?? int.MaxValue)
                    : filtered.OrderBy(t => t.RosterRank.HasValue ? 0 : 1)
                              .ThenByDescending(t => t.RosterRank ?? int.MaxValue),
                _ => filtered.OrderBy(t => t.OverallRank)
            };

            // ShowFavoritesFirst: float followed teams to top, preserve sort within each group
            var result = _navState.ShowFavoritesFirst
                ? sorted.OrderByDescending(t => t.IsFollowed).ToList()
                : sorted.ToList();

            for (int i = 0; i < result.Count; i++)
            {
                result[i].ActiveSortValue = GetActiveSortValue(result[i]);
                result[i].IsOddRow = i % 2 == 1;
                result[i].IsTop25  = result[i].OverallRank > 0 && result[i].OverallRank <= 25;
            }

            FilteredTeams = new ObservableCollection<TeamRanking>(result);

            BuildConferenceSummaries();
            BuildTop25();
        }

        // ── Top 25 view ───────────────────────────────────────────────────

        /// <summary>
        /// Builds the Top 25 view for the selected source. Ignores the tier
        /// filter, conference picker and favorites-first in every source.
        /// Row alternation is per column.
        ///
        /// J1s: national top 25 by OverallRank from _allTeams, split 1–12 left /
        ///      13–25 right.
        /// AP / Coaches: CFBD poll rows for the loaded week, in the server's
        ///      order. Split by position (first 12 left, rest right) rather than
        ///      by rank — ties make rank numbers non-sequential, and a tie at 25
        ///      produces a 26th row. Same card layout; Record shows first-place
        ///      votes and Rating shows poll points. Team name/abbreviation come
        ///      from _allTeams by TeamID when available so naming matches the
        ///      rest of the app; the poll's own school/conference is the fallback.
        /// </summary>
        private void BuildTop25()
        {
            List<Top25Card> left;
            List<Top25Card> right;

            if (_top25Source == SourceJ1s)
            {
                var top = _allTeams
                    .Where(t => t.OverallRank > 0 && t.OverallRank <= 25)
                    .OrderBy(t => t.OverallRank)
                    .ToList();

                static Top25Card ToCard(TeamRanking t, int index) => new()
                {
                    TeamID         = t.TeamID,
                    Rank           = t.OverallRank,
                    RankDisplay    = $"{t.OverallRank}",
                    TeamName       = t.TeamName,
                    ConferenceAbbr = t.ConferenceAbbr,
                    Record         = t.Record,
                    DisplayRating  = t.DisplayRank,
                    IsOddRow       = index % 2 == 1
                };

                left  = top.Where(t => t.OverallRank <= 12).Select(ToCard).ToList();
                right = top.Where(t => t.OverallRank >= 13).Select(ToCard).ToList();
            }
            else
            {
                var rows = (_top25Source == SourceAp ? _polls?.ApPoll : _polls?.CoachesPoll)
                           ?? new List<PollRankingDto>();

                var teamsById = _allTeams
                    .GroupBy(t => t.TeamID)
                    .ToDictionary(g => g.Key, g => g.First());

                Top25Card ToPollCard(PollRankingDto r, int index)
                {
                    teamsById.TryGetValue(r.TeamId, out var t);

                    return new Top25Card
                    {
                        TeamID         = r.TeamId,
                        Rank           = r.Rank,
                        RankDisplay    = string.IsNullOrWhiteSpace(r.DisplayRank)
                                             ? r.Rank.ToString(CultureInfo.InvariantCulture)
                                             : r.DisplayRank,
                        TeamName       = t?.TeamName ?? r.School ?? string.Empty,
                        ConferenceAbbr = t?.ConferenceAbbr ?? r.Conference,
                        Record         = $"{r.FirstPlaceVotes ?? 0} 1st",
                        DisplayRating  = $"{r.Points ?? 0:N0} pts",
                        IsOddRow       = index % 2 == 1
                    };
                }

                left  = rows.Take(12).Select(ToPollCard).ToList();
                right = rows.Skip(12).Select(ToPollCard).ToList();
            }

            Top25Left  = new ObservableCollection<Top25Card>(left);
            Top25Right = new ObservableCollection<Top25Card>(right);
            OnPropertyChanged(nameof(HasTop25));
            OnPropertyChanged(nameof(HasNoTop25));
            OnPropertyChanged(nameof(Top25EmptyMessage));
        }

        // ── Conferences view ──────────────────────────────────────────────

        /// <summary>Conference name/abbreviation check — Independent is a
        /// conference, not a tier (engineering notes).</summary>
        private static bool IsIndependent(TeamRanking t) =>
            (t.Conference?.Contains("Independent", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (t.ConferenceAbbr?.Contains("Independent", StringComparison.OrdinalIgnoreCase) ?? false) ||
            string.Equals(t.ConferenceAbbr, "Ind", StringComparison.OrdinalIgnoreCase);

        private static string? ConferenceKeyOf(TeamRanking t) =>
            !string.IsNullOrWhiteSpace(t.ConferenceAbbr) ? t.ConferenceAbbr
            : !string.IsNullOrWhiteSpace(t.Conference)   ? t.Conference
            : null;

        /// <summary>
        /// Builds per-conference plain means from _allTeams. Ignores the global
        /// conference picker and favorites-first; honours only the P4/G5 tier
        /// filter. Independents are always omitted.
        /// </summary>
        private void BuildConferenceSummaries()
        {
            var source = _allTeams.Where(t => !IsIndependent(t) && ConferenceKeyOf(t) != null);

            source = _currentFilter switch
            {
                RankingFilter.P4 => source.Where(t => t.Tier == "P4"),
                RankingFilter.G5 => source.Where(t => t.Tier == "G5"),
                _                => source
            };

            var summaries = source
                .GroupBy(t => ConferenceKeyOf(t)!, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var teams   = g.ToList();
                    var count   = teams.Count;
                    var ratings = teams.Where(t => t.Ranking.HasValue).Select(t => t.Ranking!.Value).ToList();
                    var sos     = teams.Where(t => t.CombinedSOS.HasValue).Select(t => t.CombinedSOS!.Value).ToList();
                    var rosters = teams.Where(t => t.RosterRank.HasValue).Select(t => t.RosterRank!.Value).ToList();

                    var tier = teams
                        .Where(t => !string.IsNullOrWhiteSpace(t.Tier))
                        .GroupBy(t => t.Tier)
                        .OrderByDescending(tg => tg.Count())
                        .Select(tg => tg.Key)
                        .FirstOrDefault();

                    var name = teams
                        .Select(t => t.Conference)
                        .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? g.Key;

                    return new ConferenceSummary
                    {
                        ConferenceKey      = g.Key,
                        ConferenceName     = name,
                        Tier               = tier,
                        TeamCount          = count,
                        AvgWins            = (double)teams.Sum(t => t.Wins)            / count,
                        AvgLosses          = (double)teams.Sum(t => t.Losses)          / count,
                        AvgProjectedWins   = (double)teams.Sum(t => t.ProjectedWins)   / count,
                        AvgProjectedLosses = (double)teams.Sum(t => t.ProjectedLosses) / count,
                        AvgRating          = ratings.Count > 0 ? ratings.Average() : null,
                        AvgSOS             = sos.Count     > 0 ? sos.Average()     : null,
                        AvgRosterRank      = rosters.Count > 0 ? rosters.Average() : null,
                        RosterCoverage     = rosters.Count
                    };
                })
                .ToList();

            // Conference rank: AvgRating desc (null last), CombinedSOS tiebreak.
            var ranked = summaries
                .OrderBy(c => c.AvgRating.HasValue ? 0 : 1)
                .ThenByDescending(c => c.AvgRating ?? 0)
                .ThenByDescending(c => c.AvgSOS ?? 0)
                .ToList();
            for (int i = 0; i < ranked.Count; i++)
                ranked[i].ConferenceRank = i + 1;

            IOrderedEnumerable<ConferenceSummary> sorted = _confSort switch
            {
                RankingSort.Conference => _isConfSortAscending
                    ? summaries.OrderBy(c => c.ConferenceName)
                    : summaries.OrderByDescending(c => c.ConferenceName),
                RankingSort.PowerRating => _isConfSortAscending
                    ? summaries.OrderBy(c => c.AvgRating ?? 0)
                    : summaries.OrderByDescending(c => c.AvgRating ?? 0),
                RankingSort.Record => _isConfSortAscending
                    ? summaries.OrderBy(c => c.ProjectedWinPct).ThenBy(c => c.AvgSOS ?? 0)
                               .ThenBy(c => c.AvgRosterRank ?? double.MaxValue)
                    : summaries.OrderByDescending(c => c.ProjectedWinPct).ThenByDescending(c => c.AvgSOS ?? 0)
                               .ThenBy(c => c.AvgRosterRank ?? double.MaxValue),
                RankingSort.SOS => _isConfSortAscending
                    ? summaries.OrderBy(c => c.AvgSOS ?? 0)
                    : summaries.OrderByDescending(c => c.AvgSOS ?? 0),
                // Same null-pinning as the Teams RosterRank sort — no data sorts
                // last in either direction.
                RankingSort.RosterRank => _isConfSortAscending
                    ? summaries.OrderBy(c => c.AvgRosterRank.HasValue ? 0 : 1)
                               .ThenBy(c => c.AvgRosterRank ?? double.MaxValue)
                    : summaries.OrderBy(c => c.AvgRosterRank.HasValue ? 0 : 1)
                               .ThenByDescending(c => c.AvgRosterRank ?? double.MaxValue),
                _ => _isConfSortAscending
                    ? summaries.OrderBy(c => c.ConferenceRank)
                    : summaries.OrderByDescending(c => c.ConferenceRank)
            };

            var result = sorted.ToList();
            for (int i = 0; i < result.Count; i++)
                result[i].IsOddRow = i % 2 == 1;

            ConferenceSummaries = new ObservableCollection<ConferenceSummary>(result);
        }

        private void SortConferencesByColumn(string columnName)
        {
            var newSort = columnName switch
            {
                "Rank"       => RankingSort.Rank,
                "Conference" => RankingSort.Conference,
                "Record"     => RankingSort.Record,
                "Rating"     => RankingSort.PowerRating,
                "SOS"        => RankingSort.SOS,
                "RosterRank" => RankingSort.RosterRank,
                _            => RankingSort.Rank
            };

            if (_confSort == newSort)
                _isConfSortAscending = !_isConfSortAscending;
            else
            {
                _confSort = newSort;
                _isConfSortAscending = newSort switch
                {
                    RankingSort.PowerRating => false,
                    RankingSort.SOS         => false,
                    _                       => true
                };
            }

            BuildConferenceSummaries();
        }

        private async void OnNavStateChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "FilterChanged") return;

            System.Diagnostics.Debug.WriteLine($"[Rankings] FilterChanged isMain={MainThread.IsMainThread} isActive={IsActive}");

            // Off-screen: don't load or render now. Mark stale so SyncPage reloads
            // this page the next time it becomes visible.
            if (!IsActive)
            {
                HasLoaded = false;
                return;
            }

            switch (_navState.LastFilterChange)
            {
                case FilterChangeReason.Year:
                case FilterChangeReason.Week:
                    // Year or week changed — rankings are week-specific, must hit server
                    await LoadDataAsync();
                    break;

                case FilterChangeReason.Conference:
                    // Conference or favorites changed — refilter cached results only
                    ApplyFiltersAndSort();
                    break;
            }
        }
        private void OnTeamFollowChanged(int teamId, bool isFollowed)
        {
            var team = _allTeams.FirstOrDefault(t => t.TeamID == teamId);
            if (team != null)
            {
                team.IsFollowed = isFollowed;
                if (_navState.ShowFavoritesFirst)
                    MainThread.BeginInvokeOnMainThread(ApplyFiltersAndSort);
            }
        }

        /// <summary>
        /// RankingsCacheService also stamps IsFollowed on TeamFollowChanged and
        /// fires CacheUpdated. This is a secondary safety net in case another
        /// consumer (e.g. MyTeamsViewModel) triggers a full cache reload while
        /// Rankings is active — refilter from the now-current shared list rather
        /// than going stale. Guarded by HasLoaded so it doesn't double-fire
        /// immediately after this page's own LoadDataAsync completes.
        /// </summary>
        private void OnRankingsCacheUpdated()
        {
            if (!HasLoaded || !IsActive) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _allTeams = _rankingsCache.AllRankings.ToList();
                ApplyFiltersAndSort();
            });
        }
    }
}
