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
    ///
    /// Playoffs tab has two states: before the real CFP games exist in the
    /// schedule cache, it shows a projected 12-team seed table (Ranking-only,
    /// no conference championship simulation — see PlayoffSeedingService on
    /// the Api side). Once real "playoff" SeasonType games appear (the
    /// committee's actual field, which is authoritative over our own
    /// projection), it switches to the existing real-bracket view and the
    /// seed table is dropped.
    ///
    /// Projections | CFP source toggle (2026-11-04): while no real bracket
    /// exists, the header radios switch both sub-tabs between our projected
    /// field (PlayoffSeedingService) and the CFP committee's rankings for the
    /// header (year, week). CFP mode is a straight dump — committee rank 1–12
    /// is seed 1–12, seeds 1–4 get byes, and the bracket shows only the
    /// pairings known from rank (no projected scores or winners). CFP data
    /// comes from the same polls endpoint the Rankings page uses.
    /// </summary>
    public class PostseasonViewModel : BaseViewModel
    {
        private readonly GameDataApiService           _apiService;
        private readonly GameDataCacheService         _cache;
        private readonly SharedNavigationStateService _navState;

        private List<ChampionshipMatchup> _allChampionships = new();
        private bool   _isBusy;
        private string _selectedView          = "Championship";
        private string _selectedPlayoffSubView = "Projected Field";
        private string _statusMessage = "Loading...";
        private string _emptyMessage = "Loading...";

        // ── Playoffs source: Projections | CFP (2026-11-04) ───────────────
        private const string PlayoffSourceProjections = "Projections";
        private const string PlayoffSourceCfp         = "CFP";
        private string _playoffSource = PlayoffSourceProjections;
        private int    _cfpWeek;

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

            SelectPlayoffSubViewCommand = new Microsoft.Maui.Controls.Command<string>(subView =>
            {
                SelectedPlayoffSubView = subView;
            });

            // Header radios. CFP data is loaded with the projections, so
            // switching source is display-only — no server call.
            SelectPlayoffSourceCommand = new Microsoft.Maui.Controls.Command<string>(source =>
            {
                if (source is not (PlayoffSourceProjections or PlayoffSourceCfp)) return;
                PlayoffSource = source;
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

            ToggleCardTiebreakerCommand = new Microsoft.Maui.Controls.Command<GameResult>(game =>
            {
                if (game == null) return;
                game.IsTiebreakerExpanded = !game.IsTiebreakerExpanded;
            });

            ToggleCardContendersCommand = new Microsoft.Maui.Controls.Command<GameResult>(game =>
            {
                if (game == null) return;
                game.IsContendersExpanded = !game.IsContendersExpanded;
            });

            // Section collapse toggles
            ToggleRoundExpandCommand = new Microsoft.Maui.Controls.Command<PlayoffRound>(round =>
            {
                if (round != null) round.IsExpanded = !round.IsExpanded;
            });

            ToggleBracketRoundExpandCommand = new Microsoft.Maui.Controls.Command<ProjectedBracketRound>(round =>
            {
                if (round != null) round.IsExpanded = !round.IsExpanded;
            });

            ToggleCfpBracketRoundExpandCommand = new Microsoft.Maui.Controls.Command<CfpBracketRound>(round =>
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

        /// <summary>Projected 12-team seed table — populated only when HasPlayoffData is false.</summary>
        public ObservableCollection<PlayoffSeed>         PlayoffSeeds  { get; } = new();

        /// <summary>Projected bracket, First Round through National Championship —
        /// populated only when HasPlayoffData is false, alongside PlayoffSeeds.</summary>
        public ObservableCollection<ProjectedBracketRound> BracketRounds { get; } = new();

        /// <summary>CFP committee top 12 for the header week — seed = rank.</summary>
        public ObservableCollection<CfpSeedRow>      CfpSeeds         { get; } = new();

        /// <summary>CFP bracket built from CfpSeeds — pairings only, no projections.</summary>
        public ObservableCollection<CfpBracketRound> CfpBracketRounds { get; } = new();

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

        /// <summary>True once the committee's real CFP games exist in the schedule cache.</summary>
        public bool HasPlayoffData  => PlayoffRounds.Any();
        public bool HasBowlData     => BowlWeekends.Any();

        /// <summary>True once a projected seed table has been loaded.</summary>
        public bool HasSeedingData  => PlayoffSeeds.Any();

        /// <summary>True once the projected bracket has been loaded.</summary>
        public bool HasBracketData  => BracketRounds.Any();

        /// <summary>Seed table is the thing to show: no real bracket yet, but we have a projection.</summary>
        public bool IsSeedingView   => !HasPlayoffData && HasSeedingData;

        /// <summary>True once CFP committee rankings exist for the header week.</summary>
        public bool HasCfpData   => CfpSeeds.Any();
        public bool HasNoCfpData => !HasCfpData;

        /// <summary>Either the real bracket or the projected seed table has something to show.</summary>
        public bool HasPlayoffOrSeedingData => HasPlayoffData || HasSeedingData;

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

        /// <summary>Sub-tab within Playoffs, only relevant while IsSeedingView (no
        /// real bracket yet) — "Projected Field" or "Bracket".</summary>
        public string SelectedPlayoffSubView
        {
            get => _selectedPlayoffSubView;
            set
            {
                _selectedPlayoffSubView = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsProjectedFieldSubView));
                OnPropertyChanged(nameof(IsBracketSubView));
                RaisePlayoffDisplayProperties();
            }
        }

        public bool IsProjectedFieldSubView => _selectedPlayoffSubView == "Projected Field";
        public bool IsBracketSubView         => _selectedPlayoffSubView == "Bracket";

        /// <summary>"Projections" or "CFP" — header radio selection.</summary>
        public string PlayoffSource
        {
            get => _playoffSource;
            set
            {
                if (_playoffSource == value) return;
                _playoffSource = value;
                OnPropertyChanged();
                RaisePlayoffDisplayProperties();
            }
        }

        public bool IsProjectionsSource => _playoffSource == PlayoffSourceProjections;
        public bool IsCfpSource         => _playoffSource == PlayoffSourceCfp;

        /// <summary>First sub-tab label — "Field" in CFP mode (committee's, not projected).</summary>
        public string FieldSubTabLabel => IsCfpSource ? "Field" : "Projected Field";

        // Sub-tab × source — one of these four content blocks is visible.
        public bool ShowProjectedField   => IsProjectedFieldSubView && IsProjectionsSource;
        public bool ShowCfpField         => IsProjectedFieldSubView && IsCfpSource;
        public bool ShowProjectedBracket => IsBracketSubView        && IsProjectionsSource;
        public bool ShowCfpBracket       => IsBracketSubView        && IsCfpSource;

        public string CfpFootnote => $"*CFP Committee Rankings, Week {_cfpWeek} release";

        public string CfpEmptyMessage => _cfpWeek > 0
            ? $"No CFP Rankings for Week {_cfpWeek}"
            : "No CFP Rankings for this view";

        private void RaisePlayoffDisplayProperties()
        {
            OnPropertyChanged(nameof(IsProjectionsSource));
            OnPropertyChanged(nameof(IsCfpSource));
            OnPropertyChanged(nameof(FieldSubTabLabel));
            OnPropertyChanged(nameof(ShowProjectedField));
            OnPropertyChanged(nameof(ShowCfpField));
            OnPropertyChanged(nameof(ShowProjectedBracket));
            OnPropertyChanged(nameof(ShowCfpBracket));
        }

        private void RaiseCfpProperties()
        {
            OnPropertyChanged(nameof(HasCfpData));
            OnPropertyChanged(nameof(HasNoCfpData));
            OnPropertyChanged(nameof(CfpFootnote));
            OnPropertyChanged(nameof(CfpEmptyMessage));
        }

        // ── Commands ──────────────────────────────────────────────────────

        public ICommand LoadDataCommand               { get; }
        public ICommand RefreshCommand                { get; }
        public ICommand SelectViewCommand             { get; }
        public ICommand SelectPlayoffSubViewCommand   { get; }
        public ICommand SelectPlayoffSourceCommand    { get; }
        public ICommand ToggleMatchupExpandCommand    { get; }
        public ICommand ToggleContendersExpandCommand { get; }
        public ICommand ToggleDetailsCommand          { get; }
        public ICommand ToggleCardTiebreakerCommand   { get; }
        public ICommand ToggleCardContendersCommand   { get; }
        public ICommand ToggleRoundExpandCommand      { get; }
        public ICommand ToggleBracketRoundExpandCommand { get; }
        public ICommand ToggleCfpBracketRoundExpandCommand { get; }
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

                // Real bracket takes precedence — only fetch/show the projected
                // seed table (and bracket) when the committee hasn't set the field yet.
                if (!HasPlayoffData)
                {
                    await LoadPlayoffSeedingAsync();
                    await LoadPlayoffBracketAsync();
                    await LoadCfpFieldAsync();
                }
                else
                {
                    ClearPlayoffSeeding();
                    ClearPlayoffBracket();
                    ClearCfpField();
                }

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

        // ── Load projected playoff seed table ─────────────────────────────

        private async Task LoadPlayoffSeedingAsync()
        {
            var field = await Task.Run(async () =>
                await _apiService.GetPlayoffSeedingAsync(_navState.SelectedYear, _navState.SelectedWeek));

            PlayoffSeeds.Clear();
            if (field?.Field != null)
            {
                foreach (var seed in field.Field)
                    PlayoffSeeds.Add(seed);
            }

            OnPropertyChanged(nameof(HasSeedingData));
            OnPropertyChanged(nameof(IsSeedingView));
            OnPropertyChanged(nameof(HasPlayoffOrSeedingData));
        }

        private void ClearPlayoffSeeding()
        {
            if (PlayoffSeeds.Count == 0) return;

            PlayoffSeeds.Clear();
            OnPropertyChanged(nameof(HasSeedingData));
            OnPropertyChanged(nameof(IsSeedingView));
            OnPropertyChanged(nameof(HasPlayoffOrSeedingData));
        }

        // ── Load projected bracket ─────────────────────────────────────────

        private async Task LoadPlayoffBracketAsync()
        {
            var bracket = await Task.Run(async () =>
                await _apiService.GetPlayoffBracketAsync(_navState.SelectedYear, _navState.SelectedWeek));

            BracketRounds.Clear();
            if (bracket?.Rounds != null)
            {
                foreach (var round in bracket.Rounds)
                {
                    for (int i = 0; i < round.Matchups.Count; i++)
                        round.Matchups[i].IsOddRow = i % 2 == 1;

                    BracketRounds.Add(new ProjectedBracketRound(round.RoundLabel, round.Matchups));
                }
            }

            OnPropertyChanged(nameof(HasBracketData));
        }

        private void ClearPlayoffBracket()
        {
            if (BracketRounds.Count == 0) return;

            BracketRounds.Clear();
            OnPropertyChanged(nameof(HasBracketData));
        }

        // ── Load CFP committee field + bracket ────────────────────────────

        /// <summary>
        /// CFP committee rankings for the header (year, week) via the polls
        /// endpoint. Exact week match — a week before the first release, or the
        /// Week 0 "Final" view, leaves CfpSeeds empty. Rank order is the seed
        /// order; only the top 12 are kept.
        /// </summary>
        private async Task LoadCfpFieldAsync()
        {
            var year = _navState.SelectedYear;
            var week = _navState.SelectedWeek;

            PollWeekResponse? polls = null;
            if (week > 0)
            {
                polls = await Task.Run(async () =>
                    await _apiService.GetWeeklyPollsAsync(year, week));
            }

            var rows = (polls?.CFPRankings ?? new List<PollRankingDto>())
                .OrderBy(r => r.Rank)
                .Take(12)
                .ToList();

            _cfpWeek = week;

            CfpSeeds.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                CfpSeeds.Add(new CfpSeedRow(i + 1, r.TeamId, r.School ?? string.Empty, r.Conference));
            }

            BuildCfpBracket();
            RaiseCfpProperties();
        }

        private void ClearCfpField()
        {
            if (CfpSeeds.Count == 0 && CfpBracketRounds.Count == 0) return;

            CfpSeeds.Clear();
            CfpBracketRounds.Clear();
            RaiseCfpProperties();
        }

        /// <summary>
        /// 12-team bracket from the committee seeds — only what rank decides:
        /// First Round 5v12, 6v11, 7v10, 8v9; byes for 1–4 into the
        /// Quarterfinals (1 v 8/9, 2 v 7/10, 3 v 6/11, 4 v 5/12); Semifinals
        /// pair the 1 and 4 paths, and the 2 and 3 paths. No scores or winners.
        /// </summary>
        private void BuildCfpBracket()
        {
            CfpBracketRounds.Clear();
            if (CfpSeeds.Count < 12) return;

            CfpSeedRow Seed(int seed) => CfpSeeds[seed - 1];

            static List<CfpBracketMatchup> Alternate(List<CfpBracketMatchup> games)
            {
                for (int i = 0; i < games.Count; i++)
                    games[i].IsOddRow = i % 2 == 1;
                return games;
            }

            CfpBracketMatchup SeedVsSeed(int high, int low) =>
                CfpBracketMatchup.Seeded(Seed(high), Seed(low));

            CfpBracketMatchup SeedVsWinner(int high, string winnerOf) =>
                CfpBracketMatchup.SeedVsSlot(Seed(high), $"Winner {winnerOf}");

            var firstRound = Alternate(new List<CfpBracketMatchup>
            {
                SeedVsSeed(5, 12),
                SeedVsSeed(6, 11),
                SeedVsSeed(7, 10),
                SeedVsSeed(8, 9)
            });

            var quarterfinals = Alternate(new List<CfpBracketMatchup>
            {
                SeedVsWinner(1, "8/9"),
                SeedVsWinner(2, "7/10"),
                SeedVsWinner(3, "6/11"),
                SeedVsWinner(4, "5/12")
            });

            var semifinals = Alternate(new List<CfpBracketMatchup>
            {
                CfpBracketMatchup.SlotVsSlot("Winner 1 v 8/9", "Winner 4 v 5/12"),
                CfpBracketMatchup.SlotVsSlot("Winner 2 v 7/10", "Winner 3 v 6/11")
            });

            var championship = Alternate(new List<CfpBracketMatchup>
            {
                CfpBracketMatchup.SlotVsSlot("Winner Semifinal 1", "Winner Semifinal 2")
            });

            CfpBracketRounds.Add(new CfpBracketRound("First Round", firstRound));
            CfpBracketRounds.Add(new CfpBracketRound("Quarterfinals", quarterfinals));
            CfpBracketRounds.Add(new CfpBracketRound("Semifinals", semifinals));
            CfpBracketRounds.Add(new CfpBracketRound("National Championship", championship));
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
                OnPropertyChanged(nameof(IsSeedingView));
                OnPropertyChanged(nameof(HasPlayoffOrSeedingData));
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
            OnPropertyChanged(nameof(IsSeedingView));
            OnPropertyChanged(nameof(HasPlayoffOrSeedingData));

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

        /// <summary>P4 (SEC, B1G, B12, ACC) first in that order, then G6,
        /// then anything unrecognized last — Charlie, 2026-09-15.</summary>
        private static readonly Dictionary<string, int> ConferenceDisplayOrder =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["SEC"]  = 0,
                ["B1G"]  = 1,
                ["B12"]  = 2,
                ["ACC"]  = 3,
                ["MAC"]  = 4,
                ["CUSA"] = 5,
                ["MWC"]  = 6,
                ["AAC"]  = 7,
                ["PAC"]  = 8,
                ["SBC"]  = 9,
            };

        private static int ConferenceDisplaySortKey(string? conference) =>
            conference != null && ConferenceDisplayOrder.TryGetValue(conference, out var order)
                ? order
                : int.MaxValue;

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

            filteredChamps = filteredChamps
                .OrderBy(c => ConferenceDisplaySortKey(c.Conference))
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
                    // (+ new seed table, if the new year's field isn't set yet)
                    await LoadDataAsync();
                    break;

                case FilterChangeReason.Week:
                    // Week change matters for championship qualifiers and, now,
                    // playoff seeding (Ranking snapshot is as-of-week). Bowls and
                    // the real playoff bracket are schedule-driven, not
                    // week-driven, so neither needs a reload.
                    if (_selectedView != "Bowls" && _selectedView != "Playoffs")
                    {
                        await LoadDataAsync();
                    }
                    else
                    {
                        ApplyConferenceFilter();
                        if (_selectedView == "Playoffs" && !HasPlayoffData)
                        {
                            await LoadPlayoffSeedingAsync();
                            await LoadPlayoffBracketAsync();
                            await LoadCfpFieldAsync();
                        }
                    }
                    break;

                case FilterChangeReason.Conference:
                    // Conference/favorites — refilter cached data only.
                    // Seeding is a national field, not conference-filtered.
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

    /// <summary>One round of the PROJECTED bracket (no real Games rows yet —
    /// matchups are BracketMatchupResult, not GameResult). Collapsible, same
    /// as PlayoffRound.</summary>
    public class ProjectedBracketRound : INotifyPropertyChanged
    {
        public string RoundLabel { get; }
        public List<SaturdayPulse.Models.BracketMatchupResult> Matchups { get; }

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandIcon)); }
        }

        public string ExpandIcon => _isExpanded ? "▼" : "▶";

        public ProjectedBracketRound(string label, List<SaturdayPulse.Models.BracketMatchupResult> matchups)
        {
            RoundLabel = label;
            Matchups   = matchups;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One CFP committee seed row — straight from the committee's
    /// rankings (seed = rank). Seeds 1–4 have first-round byes.</summary>
    public sealed class CfpSeedRow
    {
        public int     Seed       { get; }
        public int     TeamId     { get; }
        public string  TeamName   { get; }
        public string? Conference { get; }

        public bool   HasBye           => Seed <= 4;
        public string ConferenceSuffix => string.IsNullOrWhiteSpace(Conference) ? string.Empty : $" ({Conference})";

        public CfpSeedRow(int seed, int teamId, string teamName, string? conference)
        {
            Seed       = seed;
            TeamId     = teamId;
            TeamName   = teamName;
            Conference = conference;
        }
    }

    /// <summary>One CFP bracket slot pairing. A side is either a seeded team
    /// ("5. Alabama") or an unresolved slot ("Winner 8/9") — no scores.</summary>
    public sealed class CfpBracketMatchup
    {
        public string Team1SeedText { get; }
        public string Team1Name     { get; }
        public string Team2Name     { get; }
        public string Team2SeedText { get; }
        public bool   IsOddRow      { get; set; }

        private CfpBracketMatchup(string team1SeedText, string team1Name, string team2Name, string team2SeedText)
        {
            Team1SeedText = team1SeedText;
            Team1Name     = team1Name;
            Team2Name     = team2Name;
            Team2SeedText = team2SeedText;
        }

        public static CfpBracketMatchup Seeded(CfpSeedRow high, CfpSeedRow low) =>
            new($"{high.Seed}. ", high.TeamName, low.TeamName, $" .{low.Seed}");

        public static CfpBracketMatchup SeedVsSlot(CfpSeedRow high, string slot) =>
            new($"{high.Seed}. ", high.TeamName, slot, string.Empty);

        public static CfpBracketMatchup SlotVsSlot(string slot1, string slot2) =>
            new(string.Empty, slot1, slot2, string.Empty);
    }

    /// <summary>One round of the CFP committee bracket. Collapsible, same as
    /// ProjectedBracketRound.</summary>
    public class CfpBracketRound : INotifyPropertyChanged
    {
        public string RoundLabel { get; }
        public List<CfpBracketMatchup> Matchups { get; }

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandIcon)); }
        }

        public string ExpandIcon => _isExpanded ? "▼" : "▶";

        public CfpBracketRound(string label, List<CfpBracketMatchup> matchups)
        {
            RoundLabel = label;
            Matchups   = matchups;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
