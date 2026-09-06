using HtmlAgilityPack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SaturdayPulse.Contracts.Responses;
using SaturdayPulse.Contracts;
using SaturdayPulse.Interfaces;
using SaturdayPulse.Models;
using SaturdayPulse.ModelViews;
using SaturdayPulse.Utilities;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaturdayPulse.Api.Contracts.Responses;
using SaturdayPulse.Core.Progress;

namespace SaturdayPulse.Services
{
    public class GameDataService(
        IUnitOfWork _uow,
        IHttpClientFactory _httpClientFactory) : IGameDataService
    {
        // Resolved once and reused — named client carries the bearer token
        private HttpClient CfbdClient => _httpClientFactory.CreateClient("cfbd");

        /// <summary>
        /// Builds a (Name, Classification) → ConferenceId lookup from the
        /// Conferences table. Name alone isn't a safe key — several conference
        /// names are reused across eras/levels (e.g. "Southern" fbs vs fcs,
        /// "Western Athletic" fbs vs fcs, "Southland" fbs vs fcs, "Missouri
        /// Valley" fbs/fbs) — so a plain ToDictionary(c => c.Name) throws
        /// ArgumentException on the second matching row. Uses TryAdd instead
        /// so an unexpected future collision logs instead of crashing whoever
        /// called this. Used by LoadTeamsAsync for CFBD-conference-name →
        /// ConferenceId resolution.
        ///
        /// BuildTeamsConferenceHistoryAsync no longer uses this — it now reads
        /// ConferenceId directly from CFBD's /conferences/affiliations response
        /// (see that method's remarks).
        /// </summary>
        private async Task<Dictionary<(string Name, string Classification), int>> BuildConferenceLookupAsync(
            string callerLabel, CancellationToken token)
        {
            var lookup = new Dictionary<(string Name, string Classification), int>(
                new NameClassificationComparer());

            foreach (var c in await _uow.Conferences.GetAllAsync(token))
            {
                var key = (c.Name ?? string.Empty, c.Classification ?? string.Empty);
                if (!lookup.TryAdd(key, c.ConferenceId))
                {
                    Console.WriteLine($"{callerLabel}: conference lookup collision on Name='{key.Item1}', Classification='{key.Item2}' — ConferenceId {c.ConferenceId} ignored in favor of {lookup[key]}");
                }
            }

            return lookup;
        }

        /// <summary>
        /// Resolves a CFBD-reported conference name + classification against a
        /// lookup built by <see cref="BuildConferenceLookupAsync"/>.
        /// </summary>
        private static bool TryResolveConferenceId(
            Dictionary<(string Name, string Classification), int> lookup,
            string? conferenceName,
            string? classification,
            out int confId)
        {
            confId = 0;
            return conferenceName != null &&
                   lookup.TryGetValue((conferenceName, classification ?? string.Empty), out confId);
        }

        #region CFBD V2 — Load Methods


        /// <summary>
        /// Loads transfer portal entries for a single season from CFBD.
        /// Filters out Withdrawn entries before persisting.
        /// Only FBS-relevant transfers are stored — destination or origin must be an FBS team.
        /// </summary>
        public async Task<int> LoadPortalAsync(int season, CancellationToken token = default)
        {
            // CFBD endpoint: GET /transferPortal?year={season}
            var response = await CfbdClient.GetAsync($"player/portal?year={season}", token);
            response.EnsureSuccessStatusCode();

            var entries = await System.Text.Json.JsonSerializer.DeserializeAsync<List<CfbdPortalEntry>>(
                await response.Content.ReadAsStreamAsync(token),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                token);
            if (entries == null || entries.Count == 0) return 0;

            // Load FBS team names for filtering.
            var fbsTeams = await _uow.Teams.GetAllAsync(token);
            var fbsNames = fbsTeams
                .Where(t => string.Equals(t.Division, "fbs", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.TeamName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Also include known aliases.
            var aliasNames = fbsTeams
                .Where(t => t.Alias != null &&
                            string.Equals(t.Division, "fbs", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Alias!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var allFbsNames = fbsNames.Concat(aliasNames).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Map to model — only include transfers involving at least one FBS team.
            var portalEntries = entries
                .Where(e => e.Eligibility != "Withdrawn" &&
                            (allFbsNames.Contains(e.Origin ?? "") ||
                             allFbsNames.Contains(e.Destination ?? "")))
                .Select(e => new PortalEntry
                {
                    Season = season,
                    FirstName = e.FirstName,
                    LastName = e.LastName,
                    Position = e.Position,
                    Origin = e.Origin,
                    Destination = e.Destination,
                    TransferDate = e.TransferDate,
                    Rating = e.Rating,
                    Stars = e.Stars,
                    Eligibility = e.Eligibility
                })
                .ToList();

            await _uow.Portal.UpsertSeasonAsync(season, portalEntries, token);
            await _uow.SaveChangesAsync(token);

            return portalEntries.Count;
        }

        /// <summary>
        /// Loads portal entries for every season from startSeason to current.
        /// Portal data is only reliable from 2021 onward.
        /// </summary>
        public async Task<int> LoadPortalBulkAsync(int startSeason, CancellationToken token = default)
        {
            var currentSeason = DateTime.Now.Year;
            var total = 0;

            for (var season = startSeason; season <= currentSeason; season++)
            {
                token.ThrowIfCancellationRequested();
                var count = await LoadPortalAsync(season, token);
                total += count;
                await Task.Delay(300, token); // rate limit
            }

            return total;
        }

        /// <summary>
        /// Read-only coverage check — reports which seasons since portal data became
        /// available (2021) have zero PortalEntries rows. Doesn't touch anything;
        /// safe to call any time to sanity-check whether LoadPortalBulk needs a run.
        /// </summary>
        public async Task<PortalCoverageResult> GetPortalCoverageAsync(CancellationToken token = default)
        {
            const int firstPortalYear = 2021;
            var currentYear = DateTime.Now.Year;

            var seasonsWithData = (await _uow.Portal.GetDistinctSeasonsAsync(token))
                .Select(s => (int)s)
                .ToHashSet();

            var seasons = new List<PortalSeasonCoverage>();
            for (var year = firstPortalYear; year <= currentYear; year++)
            {
                var count = seasonsWithData.Contains(year)
                    ? (await _uow.Portal.GetBySeasonAsync(year, token)).Count
                    : 0;
                seasons.Add(new PortalSeasonCoverage(year, count));
            }

            var missing = seasons.Where(s => s.EntryCount == 0).Select(s => s.Year).ToList();

            var message = missing.Count == 0
                ? $"Portal data present for all seasons {firstPortalYear}-{currentYear}."
                : $"Missing portal data for {missing.Count} season(s): {string.Join(", ", missing)}.";

            return new PortalCoverageResult(message, seasons, missing);
        }
        /// <summary>
        /// Bulk load — fetches teams for every year from startYear to current.
        /// Teams change conference each year so we refresh annually.
        /// </summary>
        public async Task<int> LoadTeamsBulkAsync(int startYear, CancellationToken token = default)
        {
            var currentYear = DateTime.Now.Month < 8 ? DateTime.Now.Year - 1 : DateTime.Now.Year;
            var total = 0;

            for (var year = startYear; year <= currentYear; year++)
            {
                total += await LoadTeamsAsync(year, token);
                await Task.Delay(300, token);
            }

            Console.WriteLine($"LoadTeamsBulkAsync: {total} total team upserts from {startYear} to {currentYear}");
            return total;
        }

        /// <summary>Streaming version — yields one ProgressUpdate per year as it completes.</summary>
        public async IAsyncEnumerable<ProgressUpdate> LoadTeamsBulkStreamAsync(
            int startYear, [EnumeratorCancellation] CancellationToken token = default)
        {
            var currentYear = DateTime.Now.Month < 8 ? DateTime.Now.Year - 1 : DateTime.Now.Year;

            for (var year = startYear; year <= currentYear; year++)
            {
                token.ThrowIfCancellationRequested();

                bool success; string message;
                try
                {
                    var count = await LoadTeamsAsync(year, token);
                    success = true;
                    message = $"{count} teams upserted";
                }
                catch (Exception ex)
                {
                    success = false;
                    message = ex.Message;
                }

                yield return new ProgressUpdate(year.ToString(), success, message);
                await Task.Delay(300, token);
            }
        }

        public async Task<int> BuildAvgScoreDifferentialsAsync(int startYear, CancellationToken token = default)
        {
            // Clear existing V2 data
            await _uow.Lookups.ClearAvgScoreDifferentialsAsync(token);

            // Historical played games
            var games = await _uow.Games
                .GetPlayedGamesSinceYearAsync(startYear, token);

            // FBS teams only
            var teams = await _uow.Teams.GetDictionaryByTeamIdAsync(token);

            // Differential bucket storage
            var buckets = new Dictionary<double, List<(double Margin, double Total)>>();

            foreach (var game in games)
            {
                if (!game.HomeId.HasValue || !game.AwayId.HasValue)
                    continue;

                if (!teams.TryGetValue(game.HomeId.Value, out var homeTeam) ||
                    !teams.TryGetValue(game.AwayId.Value, out var awayTeam))
                    continue;

                // FBS only for now
                if (!string.Equals(homeTeam.Division, "fbs", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(awayTeam.Division, "fbs", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Pregame records through previous week
                var priorWeek = Math.Max(game.Week - 1, 0);

                var records = await _uow.WeeklyRankings.GetByTeamsAndYearAndWeekAsync(
                    new[] { homeTeam.TeamId, awayTeam.TeamId },
                    game.Year,
                    priorWeek,
                    token);

                if (!records.TryGetValue(homeTeam.TeamId, out var homeRecord) ||
                    !records.TryGetValue(awayTeam.TeamId, out var awayRecord))
                    continue;

                // Strengths
                // Strengths
                var homeGamesPlayed = homeRecord.Wins + homeRecord.Losses;
                var awayGamesPlayed = awayRecord.Wins + awayRecord.Losses;

                // Existing normalized rankings
                var homeWinPct = RatingCalculator.BucketWinPct(
                    homeRecord.Wins,
                    homeGamesPlayed);

                var awayWinPct = RatingCalculator.BucketWinPct(
                    awayRecord.Wins,
                    awayGamesPlayed);

                // Expanded superiority space
                var homeStrength = RatingCalculator.ExpandStrength(homeRecord.Ranking ?? 0m);
                var awayStrength = RatingCalculator.ExpandStrength(awayRecord.Ranking ?? 0m);

                // Differential
                var rawDifferential = homeStrength - awayStrength;

                // Collapse sparse tails into stable cohorts
                if (rawDifferential > 2.75m) 
                    rawDifferential = 3.0m;
                else if (rawDifferential > 2.5m) 
                    rawDifferential = 2.75m;
                else if (rawDifferential < -2.75m) 
                    rawDifferential = -3.0m;
                else if (rawDifferential < -2.5m) 
                    rawDifferential = -2.75m;

                var differential =
                    Math.Round(
                        (double)(rawDifferential / 0.05m),
                        MidpointRounding.AwayFromZero) * 0.05;

                // Observed values
                var margin = (double)((game.HomePoints ?? 0) - (game.AwayPoints ?? 0));
                var total = (double)((game.HomePoints ?? 0) + (game.AwayPoints ?? 0));

                // Ensure bucket exists
                if (!buckets.ContainsKey(differential))
                    buckets[differential] = new List<(double, double)>();

                if (!buckets.ContainsKey(-differential))
                    buckets[-differential] = new List<(double, double)>();

                // Store BOTH perspectives
                buckets[differential].Add((margin, total));
                buckets[-differential].Add((-margin, total));
            }

            // Aggregate results
            var differentials = new List<AvgScoreDifferential>();

            foreach (var bucket in buckets.OrderBy(b => b.Key))
            {
                var differential = bucket.Key;
                var samples = bucket.Value;

                if (samples.Count == 0)
                    continue;

                var margins = samples.Select(s => s.Margin).ToList();
                var totals = samples.Select(s => s.Total).ToList();

                var avgMargin = margins.Average();

                var variance = margins
                    .Select(m => Math.Pow(m - avgMargin, 2))
                    .Average();

                var stdDev = Math.Sqrt(variance);

                differentials.Add(new AvgScoreDifferential
                {
                    StrengthDifferential = (decimal)differential,
                    AverageMargin = (decimal)avgMargin,
                    StdDevMargin = (decimal)stdDev,
                    AverageTotalPoints = (decimal)totals.Average(),
                    SampleSize = samples.Count,
                    LastUpdatedUtc = DateTime.UtcNow
                });
            }

            await _uow.Lookups.AddAvgScoreDifferentialsAsync(
                differentials,
                token);

            await _uow.SaveChangesAsync(token);

            return differentials.Count();
        }

        /// <summary>
        /// Rebuilds TeamsConferenceHistory from CFBD's /conferences/affiliations
        /// endpoint. TeamId/ConferenceId are taken directly from CFBD — no
        /// name-based resolution against our Teams/Conferences tables, since
        /// those are sourced from CFBD too and the ids match directly.
        ///
        /// `startYear` is passed through to CFBD as `minYear`, and only rows
        /// with StartYear >= startYear are cleared before reinserting — NOT the
        /// whole table. Existing TeamsConferenceHistory data is corrupted
        /// (2026-08-15 handoff), so a full rebuild means calling this with
        /// startYear=1965; calling it with a later year does a scoped
        /// reload/refresh of just that range instead of destroying everything
        /// before it.
        ///
        /// Caveat: if CFBD's minYear filter excludes an affiliation row whose
        /// StartYear predates minYear even though it's still open
        /// (EndYear == null — e.g. Oregon State's Pac-12 row starts 2022), a
        /// scoped call with startYear set after that row's StartYear won't see
        /// or touch it, which is correct (it's out of scope) but means partial
        /// reloads can't be used to "fix" an ongoing row that started earlier
        /// than the requested range. Full 1965 rebuilds aren't affected.
        /// Example: POST /api/developer/buildTeamsConferenceHistory?startYear=1965
        /// </summary>
        public async Task<int> BuildTeamsConferenceHistoryAsync(int startYear, CancellationToken token = default)
        {
            var response = await CfbdClient.GetAsync($"/conferences/affiliations?minYear={startYear}", token);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<CfbdConferenceAffiliationDto>>(cancellationToken: token) ?? [];

            var records = dtos
                .Where(d => string.Equals(d.Classification, "fbs", StringComparison.OrdinalIgnoreCase))
                .Select(d => new TeamsConferenceHistory
                {
                    TeamId = d.TeamId,
                    ConferenceId = d.ConferenceId,
                    StartYear = d.StartYear,
                    EndYear = d.EndYear
                })
                // Defensive: unique index is (TeamId, ConferenceId, StartYear) —
                // collapse any duplicate rows CFBD returns before AddRange throws
                // on the constraint.
                .GroupBy(r => (r.TeamId, r.ConferenceId, r.StartYear))
                .Select(g => g.First())
                .ToList();

            // Scoped delete — only wipes what we're about to reinsert, not the
            // whole table. See doc comment above re: partial-range caveat.
            await _uow.TeamsConferenceHistory.ClearAsync(token);
            await _uow.TeamsConferenceHistory.AddRangeAsync(records, token);

            Console.WriteLine($"BuildTeamsConferenceHistoryAsync: {records.Count} rows inserted (StartYear >= {startYear}) from {dtos.Count} total affiliations ({dtos.Count - records.Count} non-FBS/duplicate skipped)");

            return records.Count;
        }

        /// <summary>
        /// Assigns correct week numbers (17, 18, 19...) to postseason games for a given year.
        /// CFBD returns week=1 for all postseason games; this fixes that by bucketing on
        /// game date (Thursday-anchored weeks) and assigning sequential weeks from 17.
        /// Example: POST /api/developer/assignPostseasonWeeks?year=2024
        /// </summary>
        public async Task<int> AssignPostseasonWeeksAsync(int year, CancellationToken token = default)
        {
            var games = await _uow.Games.GetByYearAsync(year, token);
            var postseason = games.Where(g => g.SeasonType == "postseason"
                                           && g.GameDate != null
                                           && DateTime.TryParse(g.GameDate, out _)).ToList();

            if (postseason.Count == 0)
            {
                Console.WriteLine($"AssignPostseasonWeeksAsync: no postseason games found for {year}");
                return 0;
            }

            var regularGames = games.Where(g => g.SeasonType == "regular" && g.GameDate != null).ToList();

            var maxRegularWeek = regularGames.Max(g => g.Week);

            var regularWeekByTuesdayStart = regularGames
                .Where(g => DateTime.TryParse(g.GameDate, out _))
                .GroupBy(g =>
                {
                    DateTime.TryParse(g.GameDate, out var dt);
                    var daysFromTuesday = ((int)dt.DayOfWeek + 6) % 7;
                    return dt.Date.AddDays(-daysFromTuesday);
                })
                .ToDictionary(grp => grp.Key, grp => grp.First().Week);

            var weekMap = BuildPostseasonWeekMapFromGames(postseason, regularWeekByTuesdayStart, maxRegularWeek);
            foreach (var game in postseason)
                if (weekMap.TryGetValue(game.GameId, out var pw))
                    game.Week = pw;

            await _uow.SaveChangesAsync(token);
            Console.WriteLine($"AssignPostseasonWeeksAsync: assigned postseason weeks for {postseason.Count} games in {year}");
            return postseason.Count;
        }

        /// <summary>
        /// Bulk version — runs AssignPostseasonWeeksAsync for every year from startYear to current.
        /// Example: POST /api/developer/assignPostseasonWeeksBulk?startYear=1963
        /// </summary>
        public async Task<int> AssignPostseasonWeeksBulkAsync(int startYear, CancellationToken token = default)
        {
            var currentYear = DateTime.Now.Month < 8 ? DateTime.Now.Year - 1 : DateTime.Now.Year;
            var total = 0;

            for (var year = startYear; year <= currentYear; year++)
            {
                total += await AssignPostseasonWeeksAsync(year, token);
                await Task.Delay(50, token); // no HTTP calls, just DB — short delay is fine
            }

            Console.WriteLine($"AssignPostseasonWeeksBulkAsync: {total} total postseason games updated from {startYear} to {currentYear}");
            return total;
        }

        /// <summary>
        /// Buckets Games entities by Thursday-anchored calendar week, assigns week 17, 18, 19...
        /// </summary>
        private static Dictionary<int, int> BuildPostseasonWeekMapFromGames(
            List<Games> postseason,
            Dictionary<DateTime, int> regularWeekByTuesdayStart,
            int maxRegularWeek)
        {
            var parsed = postseason
                .Select(g =>
                {
                    DateTime.TryParse(g.GameDate, out var dt);
                    var daysFromTuesday = ((int)dt.DayOfWeek + 6) % 7; // Tue=0 ... Mon=6
                    var weekStart = dt.Date.AddDays(-daysFromTuesday);
                    return (g.GameId, weekStart);
                })
                .ToList();

            var distinctBuckets = parsed
                .Select(x => x.weekStart)
                .Distinct()
                .OrderBy(w => w)
                .ToList();

            var weekLabels = new Dictionary<DateTime, int>();
            var nextFallback = maxRegularWeek + 1;

            foreach (var bucket in distinctBuckets)
            {
                if (regularWeekByTuesdayStart.TryGetValue(bucket, out var existingWeek))
                    weekLabels[bucket] = existingWeek;
                else
                    weekLabels[bucket] = nextFallback++;
            }

            return parsed.ToDictionary(x => x.GameId, x => weekLabels[x.weekStart]);
        }

        /// <summary>
        /// Bulk load — fetches all games for every year from startYear to current.
        /// Sequential with 300ms delay per CFBD request guidelines.
        /// </summary>
        public async Task<int> LoadGamesBulkAsync(int startYear, CancellationToken token = default)
        {
            var currentYear = DateTime.Now.Month < 8 ? DateTime.Now.Year - 1 : DateTime.Now.Year;
            var total = 0;

            for (var year = startYear; year <= currentYear; year++)
            {
                total += await LoadGamesAsync(year, week: null, token);
                await Task.Delay(300, token);
            }

            Console.WriteLine($"LoadGamesBulkAsync: {total} total game upserts from {startYear} to {currentYear}");
            return total;
        }

        /// <summary>Streaming version — yields one ProgressUpdate per year as it completes.</summary>
        public async IAsyncEnumerable<ProgressUpdate> LoadGamesBulkStreamAsync(
            int startYear, [EnumeratorCancellation] CancellationToken token = default)
        {
            var currentYear = DateTime.Now.Month < 8 ? DateTime.Now.Year - 1 : DateTime.Now.Year;

            for (var year = startYear; year <= currentYear; year++)
            {
                token.ThrowIfCancellationRequested();

                bool success; string message;
                try
                {
                    var count = await LoadGamesAsync(year, week: null, token);
                    success = true;
                    message = $"{count} games upserted";
                }
                catch (Exception ex)
                {
                    success = false;
                    message = ex.Message;
                }

                yield return new ProgressUpdate(year.ToString(), success, message);
                await Task.Delay(300, token);
            }
        }

        /// <summary>
        /// Bulk load — fetches lines for every week of every year from startYear to current.
        /// Two delays: 300ms between weeks, 500ms between years.
        /// Lines only exist from ~2013 forward so early years will return empty gracefully.
        /// </summary>
        public async Task<int> LoadLinesBulkAsync(int startYear, CancellationToken token = default)
        {
            var currentYear = DateTime.Now.Month < 8 ? DateTime.Now.Year - 1 : DateTime.Now.Year;
            var total = 0;

            for (var year = startYear; year <= currentYear; year++)
            {
                // Fetch week range from Games table so we only request weeks that exist
                var weeks = (await _uow.Games.GetByYearAsync(year, token))
                    .Select(g => g.Week)
                    .Distinct()
                    .OrderBy(w => w)
                    .ToList();

                foreach (var week in weeks)
                {
                    total += await LoadLinesAsync(year, week, token);
                    await Task.Delay(300, token);
                }

                Console.WriteLine($"LoadLinesBulkAsync: completed {year}");
                await Task.Delay(500, token);
            }

            Console.WriteLine($"LoadLinesBulkAsync: {total} total lines from {startYear} to {currentYear}");
            return total;
        }

        /// <summary>Streaming version — yields one ProgressUpdate per year as it completes.</summary>
        public async IAsyncEnumerable<ProgressUpdate> LoadLinesBulkStreamAsync(
            int startYear, [EnumeratorCancellation] CancellationToken token = default)
        {
            var currentYear = DateTime.Now.Month < 8 ? DateTime.Now.Year - 1 : DateTime.Now.Year;

            for (var year = startYear; year <= currentYear; year++)
            {
                token.ThrowIfCancellationRequested();

                bool success; string message;
                try
                {
                    var weeks = (await _uow.Games.GetByYearAsync(year, token))
                        .Select(g => g.Week)
                        .Distinct()
                        .OrderBy(w => w)
                        .ToList();

                    var yearTotal = 0;
                    foreach (var week in weeks)
                    {
                        yearTotal += await LoadLinesAsync(year, week, token);
                        await Task.Delay(300, token);
                    }

                    success = true;
                    message = $"{yearTotal} lines across {weeks.Count} weeks";
                }
                catch (Exception ex)
                {
                    success = false;
                    message = ex.Message;
                }

                yield return new ProgressUpdate(year.ToString(), success, message);
                await Task.Delay(500, token);
            }
        }

        /// <summary>
        /// Fetches all conferences from CFBD and upserts into Conferences table.
        /// </summary>
        public async Task<int> LoadConferencesAsync(CancellationToken token = default)
        {
            var response = await CfbdClient.GetAsync("/conferences", token);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<CfbdConferenceDto>>(cancellationToken: token) ?? [];

            var conferences = dtos.Select(d => new Conference
            {
                ConferenceId   = d.Id,
                Name           = d.Name,
                ShortName      = d.ShortName,
                Abbreviation   = d.Abbreviation,
                Classification = d.Classification
            }).ToList();

            await _uow.Conferences.UpsertRangeAsync(conferences, token);
            await _uow.SaveChangesAsync(token);

            Console.WriteLine($"LoadConferencesAsync: upserted {conferences.Count} conferences");
            return conferences.Count;
        }

        /// <summary>
        /// Fetches all teams for a given year from CFBD and upserts into Teams table.
        /// Resolves ConferenceId by matching conference name against Conferences table.
        /// </summary>
        public async Task<int> LoadTeamsAsync(int? year = null, CancellationToken token = default)
        {
            var targetYear = year ?? (DateTime.Now.Month < 8 ? DateTime.Now.Year - 1 : DateTime.Now.Year);

            var response = await CfbdClient.GetAsync($"/teams?year={targetYear}", token);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<CfbdTeamV2Dto>>(cancellationToken: token) ?? [];

            // See BuildConferenceLookupAsync — Name alone isn't a safe key for
            // conference resolution, several names are reused across eras/levels.
            var conferenceLookup = await BuildConferenceLookupAsync(nameof(LoadTeamsAsync), token);

            var teams = dtos.Select(d => new Teams
            {
                TeamId       = d.Id,
                TeamName     = d.School,
                Mascot       = d.Mascot,
                Abbreviation = d.Abbreviation,
                Alias        = d.AlternateNames != null ? string.Join(",", d.AlternateNames) : null,
                Division     = d.Classification,
                ConferenceId = TryResolveConferenceId(conferenceLookup, d.Conference, d.Classification, out var confId)
                               ? confId
                               : null,
                ShortName    = null  // not in /teams endpoint
            }).ToList();

            await _uow.Teams.UpsertRangeAsync(teams, token);
            await _uow.SaveChangesAsync(token);

            Console.WriteLine($"LoadTeamsAsync: upserted {teams.Count} teams for {targetYear}");
            return teams.Count;
        }

        // Must stay identical to GameScorePollingService.KickoffTimeFormat —
        // that's the only other place this column gets parsed.
        private const string KickoffTimeFormat = "HH:mm:ss";
        private static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        /// <summary>
        /// Fetches games for a given year (and optionally week) from CFBD and upserts into Games table.
        /// Pass week=null to load a full season sequentially with delay to avoid rate limiting.
        /// </summary>
        public async Task<int> LoadGamesAsync(int year, int? week = null, CancellationToken token = default)
        {
            var url = $"/games?year={year}&seasonType=both&classification=fbs";
            if (week.HasValue)
                url += $"&week={week.Value}";

            var response = await CfbdClient.GetAsync(url, token);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<CfbdGameV2Dto>>(cancellationToken: token) ?? [];

            var games = dtos.Select(d =>
            {
                // Parsed once and reused for GameDate/GameDay/KickoffTime — previously
                // this parsed the same string twice (once per field) and discarded the
                // time-of-day entirely. KickoffTime now keeps it (as a fixed-format
                // string — SQLite/matches GameDate/GameDay convention) for the
                // game-day score poller's on/off window (see GameScorePollingService).
                var parsed = d.StartDate != null && DateTime.TryParse(
                                d.StartDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                out var utcDt) ? TimeZoneInfo.ConvertTimeFromUtc(utcDt, EasternTimeZone)
                                : (DateTime?)null;

                return new Games
                {
                    GameId         = d.Id,
                    Year           = d.Season,
                    Week           = d.Week,
                    SeasonType     = d.SeasonType,
                    GameDate       = parsed?.ToString("yyyy-MM-dd") ?? d.StartDate,
                    GameDay        = parsed != null ? parsed.Value.DayOfWeek.ToString()[..3].ToUpper() : null,
                    KickoffTime    = parsed?.ToString(KickoffTimeFormat, CultureInfo.InvariantCulture),
                    HomeId         = d.HomeId,
                    HomeName       = d.HomeTeam,
                    HomePoints     = d.HomePoints,
                    AwayId         = d.AwayId,
                    AwayName       = d.AwayTeam,
                    AwayPoints     = d.AwayPoints,
                    NeutralSite    = d.NeutralSite,
                    ConferenceGame = d.ConferenceGame,
                    Attendance     = d.Attendance,
                    Venue          = d.Venue
                };
            }).ToList();

            await _uow.Games.UpsertRangeAsync(games, token);
            var result = await _uow.SaveChangesAsync(token);

            Console.WriteLine($"LoadGamesAsync: upserted {games.Count} games for {year}" +
                              (week.HasValue ? $" week {week}" : " (full season)"));
            return games.Count;
        }

        /// <summary>
        /// Fetches Vegas lines for a given year and week from CFBD.
        /// Deletes existing lines for each game before inserting fresh ones
        /// so each weekly refresh gets clean data.
        /// </summary>
        public async Task<int> LoadLinesAsync(int year, int week, CancellationToken token = default)
        {
            var url = $"/lines?year={year}&week={week}&seasonType=both";

            var response = await CfbdClient.GetAsync(url, token);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<CfbdLinesGameDto>>(cancellationToken: token) ?? [];

            var allLines = new List<Lines>();

            foreach (var gameDto in dtos)
            {
                // Delete existing lines for this game so refresh is always clean
                await _uow.Lines.DeleteByGameIdAsync(gameDto.Id, token);

                foreach (var line in gameDto.Lines)
                {
                    // Normalize provider name — handle "Draft Kings" / "DraftKings" typo
                    var provider = line.Provider.Replace(" ", string.Empty);

                    allLines.Add(new Lines
                    {
                        GameId          = gameDto.Id,
                        Provider        = provider,
                        Spread          = line.Spread,
                        SpreadOpen      = line.SpreadOpen,
                        FormattedSpread = line.FormattedSpread,
                        OverUnder       = line.OverUnder,
                        OverUnderOpen   = line.OverUnderOpen,
                        HomeMoneyline   = line.HomeMoneyline,
                        AwayMoneyline   = line.AwayMoneyline
                    });
                }
            }

            await _uow.Lines.AddRangeAsync(allLines, token);
            await _uow.SaveChangesAsync(token);

            Console.WriteLine($"LoadLinesAsync: inserted {allLines.Count} lines across {dtos.Count} games for {year} week {week}");
            return allLines.Count;
        }

        /// <summary>
        /// Sunday / Wednesday refresh — loads games and lines for the given week.
        /// Conferences and Teams are stable enough to load on demand or at season start.
        /// </summary>
        public async Task<int> WeeklyRefreshAsync(int year, int week, CancellationToken token = default)
        {
            var gamesLoaded = await LoadGamesAsync(year, week, token);
            var linesLoaded = await LoadLinesAsync(year, week, token);
            Console.WriteLine($"WeeklyRefreshAsync: {year} week {week} — {gamesLoaded} games, {linesLoaded} lines");
            return gamesLoaded + linesLoaded;
        }

        /// <summary>
        /// Manual single-game refresh — backs ProductionGameDataService.GetGameAsync
        /// (the mobile ⟳ icon). Switched 2026-09 from CFBD's /lines?gameId=X (which
        /// bundled score + odds in one call) to /live/plays?gameId=X — /lines
        /// doesn't update mid-game the way /live/plays does, and the whole point
        /// of the manual refresh icon is getting a current score during a live
        /// game. Score-only by design: /live/plays returns no betting-line data
        /// at all, so this no longer touches Lines. If odds ever need refreshing
        /// on demand again, that has to be a separate call back to /lines, not
        /// folded into this one.
        /// </summary>
        public async Task<int> RefreshGameAsync(int gameId, CancellationToken token = default)
        {
            var url = $"/live/plays?gameId={gameId}";

            var response = await CfbdClient.GetAsync(url, token);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content
                .ReadFromJsonAsync<CfbdLivePlaysDto>(cancellationToken: token);

            if (dto == null || dto.Id != gameId)
            {
                Console.WriteLine($"RefreshGameAsync: CFBD returned no live data for gameId={gameId}");
                return 0;
            }

            var homeTeam = dto.Teams.FirstOrDefault(t => t.HomeAway == "home");
            var awayTeam = dto.Teams.FirstOrDefault(t => t.HomeAway == "away");

            if (homeTeam == null || awayTeam == null)
            {
                Console.WriteLine($"RefreshGameAsync: gameId={gameId} live response missing home/away team");
                return 0;
            }

            var existing = await _uow.Games.GetByGameIdAsync(gameId, token);
            if (existing == null)
            {
                Console.WriteLine($"RefreshGameAsync: no Games row found for gameId={gameId}");
                return 0;
            }

            existing.HomePoints = homeTeam.Points;
            existing.AwayPoints = awayTeam.Points;

            await _uow.SaveChangesAsync(token);

            Console.WriteLine($"RefreshGameAsync: gameId={gameId} → " +
                $"{homeTeam.Points}-{awayTeam.Points} (status: {dto.Status})");

            return 1;
        }
        /// <summary>
        /// Loads current-season roster for all teams from CFBD. Call once with the
        /// current year (T) and again with the prior year (T-1) — RosterCapacityService
        /// needs both snapshots to diff retained/departed/inflow players.
        /// FBS-filtered, same pattern as LoadPortalAsync.
        /// </summary>
        public async Task<int> LoadRosterCapacityRosterAsync(int season, CancellationToken token = default)
        {
            var response = await CfbdClient.GetAsync($"roster?year={season}", token);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<CfbdRosterEntryDto>>(cancellationToken: token) ?? [];

            if (dtos.Count == 0) return 0;

            var fbsTeams = await _uow.Teams.GetAllAsync(token);
            var fbsNames = fbsTeams
                .Where(t => string.Equals(t.Division, "fbs", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.TeamName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rosterPlayers = dtos
                .Where(d => fbsNames.Contains(d.Team))
                .Select(d => new RosterPlayer
                {
                    PlayerId = d.Id,
                    Season = season,
                    Team = d.Team,
                    Position = d.Position ?? "UNK",
                    ClassYear = d.ClassYear,
                    RecruitId = d.RecruitIds?.FirstOrDefault(),
                    // RecruitRating populated separately via LoadRecruitingRatingsAsync
                    // once that endpoint's shape is confirmed.
                    RecruitRating = null,
                    TransferRating = null,
                    FirstName = d.FirstName,
                    LastName = d.LastName
                })
                .ToList();

            // CFBD returned real data, but none of it matched an FBS team name — most
            // likely Teams.Division wasn't reliably "fbs" for the full team set at this
            // moment, or a CFBD team-name string drifted. Refuse rather than wipe the
            // existing season's rows with nothing to replace them.
            if (rosterPlayers.Count == 0)
            {
                Console.WriteLine($"LoadRosterCapacityRosterAsync: CFBD returned {dtos.Count} rows for {season} " +
                                   $"but 0 matched an FBS team name — refusing to overwrite existing data.");
                return 0;
            }

            var duplicates = rosterPlayers
                .GroupBy(r => r.PlayerId)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g)
                .ToList();

            await _uow.RosterPlayers.UpsertSeasonAsync(season, rosterPlayers, token);
            var writes = await _uow.SaveChangesAsync(token);

            Console.WriteLine($"LoadRosterCapacityRosterAsync: upserted {rosterPlayers.Count} roster rows for {season}");
            return rosterPlayers.Count;
        }

        /// <summary>
        /// Loads player season stats for all teams from CFBD — a single bulk pull,
        /// no team filter needed (confirmed against the live API). Used for T-1 only,
        /// to compute departed-player production shares.
        /// </summary>
        public async Task<int> LoadRosterCapacityStatsAsync(int season, CancellationToken token = default)
        {
            var response = await CfbdClient.GetAsync($"stats/player/season?year={season}", token);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<CfbdPlayerSeasonStatDto>>(cancellationToken: token) ?? [];

            if (dtos.Count == 0) return 0;

            var fbsTeams = await _uow.Teams.GetAllAsync(token);
            var fbsNames = fbsTeams
                .Where(t => string.Equals(t.Division, "fbs", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.TeamName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var playerStats = dtos
                .Where(d => fbsNames.Contains(d.Team))
                .Select(d => new PlayerStat
                {
                    PlayerId = d.PlayerId,
                    Season = d.Season,
                    Team = d.Team,
                    Position = d.Position ?? "UNK",
                    Category = d.Category,
                    StatType = d.StatType,
                    StatValue = d.Stat
                })
                .ToList();

            await _uow.PlayerStats.UpsertSeasonAsync(season, playerStats, token);
            await _uow.SaveChangesAsync(token);

            Console.WriteLine($"LoadRosterCapacityStatsAsync: upserted {playerStats.Count} stat rows for {season}");
            return playerStats.Count;
        }

        /// <summary>
        /// Loads head coaches from CFBD and flattens each coach's Seasons[] array into
        /// one CoachRecord row per (school, year) they coached, filtered to the requested
        /// year. Used to detect year-over-year HC turnover for the coaching penalty.
        /// </summary>
        public async Task<int> LoadRosterCapacityCoachesAsync(int year, CancellationToken token = default)
        {
            var response = await CfbdClient.GetAsync($"coaches?year={year}", token);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<CfbdCoachDto>>(cancellationToken: token) ?? [];

            if (dtos.Count == 0) return 0;

            var fbsTeams = await _uow.Teams.GetAllAsync(token);
            var fbsNames = fbsTeams
                .Where(t => string.Equals(t.Division, "fbs", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.TeamName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var flattened = new Dictionary<(string Team, int Year), string>();
            foreach (var coach in dtos)
            {
                var fullName = $"{coach.FirstName} {coach.LastName}".Trim();
                foreach (var season in coach.Seasons)
                {
                    if (season.Year != year) continue;
                    if (string.IsNullOrWhiteSpace(season.School)) continue;
                    if (!fbsNames.Contains(season.School)) continue;
                    flattened[(season.School, season.Year)] = fullName;
                }
            }

            var coachRecords = flattened
                .Select(kvp => new CoachRecord
                {
                    Team = kvp.Key.Team,
                    Year = kvp.Key.Year,
                    CoachName = kvp.Value
                })
                .ToList();

            await _uow.CoachRecords.UpsertYearAsync(year, coachRecords, token);
            await _uow.SaveChangesAsync(token);

            Console.WriteLine($"LoadRosterCapacityCoachesAsync: upserted {coachRecords.Count} coach records for {year}");
            return coachRecords.Count;
        }

        /// <summary>
        /// Loads the recruiting class for a single year from CFBD and upserts into
        /// RecruitPlayers. Only used for the target Z_roster year's incoming freshman
        /// class — players already on a prior roster get their RecruitRating fallback
        /// from real PlayerStat history instead, so no year-over-year pull is needed here.
        /// Filters out uncommitted recruits (no CommittedTo) since they can't map to a team.
        /// </summary>
        public async Task<int> LoadRosterCapacityRecruitingAsync(int year, CancellationToken token = default)
        {
            var response = await CfbdClient.GetAsync($"recruiting/players?year={year}", token);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var dtos = await response.Content
                .ReadFromJsonAsync<List<CfbdRecruitPlayerDto>>(cancellationToken: token) ?? [];

            if (dtos.Count == 0) return 0;

            var recruitPlayers = dtos
                .Where(d => !string.IsNullOrWhiteSpace(d.CommittedTo))
                .Select(d => new RecruitPlayer
                {
                    Id = d.Id,
                    AthleteId = d.AthleteId,
                    RecruitType = d.RecruitType,
                    Year = d.Year,
                    Ranking = d.Ranking,
                    Name = d.Name,
                    School = d.School,
                    CommittedTo = d.CommittedTo,
                    Position = d.Position ?? "UNK",
                    Height = d.Height,
                    Weight = d.Weight,
                    Stars = d.Stars ?? 0,
                    Rating = d.Rating ?? 0.0,
                    City = d.City,
                    StateProvince = d.StateProvince,
                    Country = d.Country,
                    Latitude = d.HometownInfo?.Latitude,
                    Longitude = d.HometownInfo?.Longitude,
                    FipsCode = d.HometownInfo?.FipsCode
                })
                .ToList();

            await _uow.RecruitPlayers.UpsertYearAsync(year, recruitPlayers, token);
            await _uow.SaveChangesAsync(token);

            Console.WriteLine($"LoadRosterCapacityRecruitingAsync: upserted {recruitPlayers.Count} recruit rows for {year}");
            return recruitPlayers.Count;
        }

        /// <summary>
        /// Convenience wrapper: loads the recruiting class for a year, then immediately
        /// joins it into RosterPlayers.RecruitRating for that same year. Requires the
        /// target year's roster to already be loaded (LoadRosterCapacityRosterAsync) —
        /// this only updates existing RosterPlayer rows, it doesn't create any.
        /// </summary>
        public async Task<(int RecruitsLoaded, int RatingsApplied)> LoadAndApplyRosterCapacityRecruitingAsync(
            int year, CancellationToken token = default)
        {
            var loaded = await LoadRosterCapacityRecruitingAsync(year, token);
            var applied = await _uow.RosterPlayers.ApplyRecruitRatingsAsync(year, token);
            return (loaded, applied);
        }

        public async Task<(int PortalLoaded, int RatingsApplied)> LoadAndApplyPortalRatingsAsync(
            int season, CancellationToken token = default)
        {
            var loaded = await LoadPortalAsync(season, token);
            var applied = await _uow.RosterPlayers.ApplyPortalRatingsAsync(season, token);
            return (loaded, applied);
        }

        #endregion

        public async Task<int> SetSeasonTypeAsync(List<int> gameIds, string seasonType, CancellationToken token = default)
        {
            var games = await _uow.Games.GetByIds(gameIds, token);

            foreach (var game in games)
                game.SeasonType = seasonType;

            await _uow.SaveChangesAsync(token);
            return games.Count;
        }


        public async Task UpdateTeamRecordsAsync(int? targetYear = null, CancellationToken token = default)
        {
            try
            {
                await _uow.TeamRecords.UpsertFromGamesAsync(targetYear, token);
                await _uow.SaveChangesAsync(token);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx)
            {
                // This gives you the specific SQLite error code (787 for Foreign Key)
                Console.WriteLine($"SQLite Error Code: {sqliteEx.SqliteErrorCode}");
                Console.WriteLine($"SQLite Extended Error Code: {sqliteEx.SqliteExtendedErrorCode}");
                Console.WriteLine($"Message: {sqliteEx.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating team records: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                throw;
            }
        }

        /// <summary>
        /// Case-insensitive comparer for the (Name, Classification) composite key
        /// used in LoadTeamsAsync to resolve a team's conference. Name alone
        /// isn't unique — several conference names are reused across eras/levels
        /// (e.g. "Southern" fbs vs fcs, "Western Athletic" fbs vs fcs,
        /// "Southland" fbs vs fcs) — but Name + Classification is unique for
        /// every row in Conferences once entirely-pre-1965 duplicate entries
        /// (MVIAA, Big 6, Big 7, Pacific Coast Conference, Border, Skyline,
        /// Mountain State) have been removed.
        /// </summary>
        private sealed class NameClassificationComparer : IEqualityComparer<(string Name, string Classification)>
        {
            public bool Equals((string Name, string Classification) x, (string Name, string Classification) y) =>
                string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Classification, y.Classification, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Name, string Classification) obj) =>
                HashCode.Combine(
                    obj.Name?.ToUpperInvariant(),
                    obj.Classification?.ToUpperInvariant());
        }
    }
}
