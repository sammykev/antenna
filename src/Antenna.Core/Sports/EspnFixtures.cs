using System.Globalization;
using System.Text.Json;

namespace Antenna.Core.Sports;

/// <summary>
/// Fixtures, live scores and results from ESPN's public scoreboard feed (no key needed). It isn't an
/// officially documented API, so everything here is defensive: a feed that fails or changes shape is
/// skipped, not fatal.
/// </summary>
public sealed class EspnFixtures(HttpClient http)
{
    private const string BaseUrl = "https://site.api.espn.com/apis/site/v2/sports/";

    /// <summary>The competitions shown on the Matches page.</summary>
    public static readonly IReadOnlyList<FixtureFeed> Feeds =
    [
        new("soccer/eng.1", "Football", "Premier League"),
        new("soccer/uefa.champions", "Football", "Champions League"),
        new("soccer/uefa.europa", "Football", "Europa League"),
        new("soccer/uefa.europa.conf", "Football", "Conference League"),
        new("soccer/eng.2", "Football", "Championship"),
        new("soccer/eng.fa", "Football", "FA Cup"),
        new("soccer/eng.league_cup", "Football", "League Cup"),
        new("soccer/esp.1", "Football", "La Liga"),
        new("soccer/ger.1", "Football", "Bundesliga"),
        new("soccer/ita.1", "Football", "Serie A"),
        new("soccer/fra.1", "Football", "Ligue 1"),
        new("soccer/sco.1", "Football", "Scottish Premiership"),
        new("soccer/usa.1", "Football", "MLS"),
        new("basketball/nba", "Basketball", "NBA"),
        new("football/nfl", "American football", "NFL"),
        new("hockey/nhl", "Ice hockey", "NHL"),
        new("baseball/mlb", "Baseball", "MLB"),
        new("tennis/atp", "Tennis", "ATP"),
        new("tennis/wta", "Tennis", "WTA"),
        new("racing/f1", "Motorsport", "Formula 1"),
        new("mma/ufc", "MMA", "UFC"),
    ];

    /// <summary>Every feed's fixtures on one (local) day, in start order. Failed feeds are left out.</summary>
    public async Task<IReadOnlyList<Fixture>> GetDayAsync(DateOnly day, IEnumerable<FixtureFeed>? feeds = null, CancellationToken ct = default)
    {
        var date = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        using var throttle = new SemaphoreSlim(6);
        var tasks = (feeds ?? Feeds).Select(async feed =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                using var response = await http.GetAsync($"{BaseUrl}{feed.Path}/scoreboard?dates={date}", ct);
                if (!response.IsSuccessStatusCode)
                    return (IReadOnlyList<Fixture>)[];
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return Parse(doc.RootElement, feed);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return [];
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();
        var all = (await Task.WhenAll(tasks)).SelectMany(f => f);
        // The feed's "day" is US time; keep what starts on this local day.
        return all.Where(f => DateOnly.FromDateTime(f.Start.LocalDateTime) == day)
                  .DistinctBy(f => f.Id)
                  .OrderBy(f => f.Start).ThenBy(f => f.League, StringComparer.Ordinal)
                  .ToList();
    }

    internal static IReadOnlyList<Fixture> Parse(JsonElement root, FixtureFeed feed)
    {
        var list = new List<Fixture>();
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in events.EnumerateArray())
        {
            var name = Str(e, "name") ?? "";
            // Tennis: an event is a tournament, its matches are in groupings.
            if (e.TryGetProperty("groupings", out var groupings) && groupings.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in groupings.EnumerateArray())
                {
                    if (!g.TryGetProperty("competitions", out var comps))
                        continue;
                    foreach (var c in comps.EnumerateArray())
                    {
                        if (FromCompetition(c, c, feed, $"{feed.League} · {name}", name) is { } match)
                            list.Add(match);
                    }
                }
                continue;
            }
            if (e.TryGetProperty("competitions", out var competitions) && competitions.ValueKind == JsonValueKind.Array
                && competitions.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first
                && FromCompetition(e, first, feed, feed.League, name) is { } fixture)
                list.Add(fixture);
        }
        return list;
    }

    /// <param name="timed">Where id, date and status are (the event, or a tennis match).</param>
    private static Fixture? FromCompetition(JsonElement timed, JsonElement competition, FixtureFeed feed, string league, string name)
    {
        if (Str(timed, "id") is not { } id || Str(timed, "date") is not { } date
            || !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start))
            return null;

        var (state, status) = Status(timed.TryGetProperty("status", out var s) ? s : competition.TryGetProperty("status", out var cs) ? cs : default);
        string? home = null, away = null, homeLogo = null, awayLogo = null, homeScore = null, awayScore = null;
        if (competition.TryGetProperty("competitors", out var competitors) && competitors.ValueKind == JsonValueKind.Array)
        {
            var sides = competitors.EnumerateArray().ToList();
            if (sides.Count == 2)
            {
                // Team sports say which side is home; tennis just lists two players.
                var h = sides.FirstOrDefault(x => Str(x, "homeAway") == "home");
                var a = sides.FirstOrDefault(x => Str(x, "homeAway") == "away");
                if (h.ValueKind != JsonValueKind.Object || a.ValueKind != JsonValueKind.Object)
                    (h, a) = (sides[0], sides[1]);
                (home, homeLogo) = Side(h);
                (away, awayLogo) = Side(a);
                if (state != FixtureState.Scheduled)
                {
                    homeScore = Score(h);
                    awayScore = Score(a);
                }
            }
        }
        // Undecided draw slots ("TBD vs TBD") and tennis entries without two players aren't matches yet.
        if (IsPlaceholder(home) && IsPlaceholder(away) || feed.Sport == "Tennis" && (home is null || away is null))
            return null;

        var broadcasts = new List<string>();
        if (competition.TryGetProperty("broadcasts", out var b) && b.ValueKind == JsonValueKind.Array)
            foreach (var x in b.EnumerateArray())
                if (x.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
                    broadcasts.AddRange(names.EnumerateArray().Select(n => n.GetString()).OfType<string>());

        return new Fixture
        {
            Id = $"{feed.Path}/{id}",
            Sport = feed.Sport,
            League = league,
            Start = start,
            State = state,
            Status = status,
            Home = home,
            Away = away,
            HomeLogo = homeLogo,
            AwayLogo = awayLogo,
            HomeScore = homeScore,
            AwayScore = awayScore,
            Name = name,
            Broadcasts = broadcasts.Distinct().ToList(),
        };
    }

    private static (string? Name, string? Logo) Side(JsonElement side)
    {
        if (side.TryGetProperty("team", out var team))
            return (Str(team, "displayName") ?? Str(team, "name"), Str(team, "logo"));
        if (side.TryGetProperty("athlete", out var athlete))
            return (Str(athlete, "displayName"), athlete.TryGetProperty("flag", out var flag) ? Str(flag, "href") : null);
        return (null, null);
    }

    private static bool IsPlaceholder(string? side) => side is not null && side.Equals("TBD", StringComparison.OrdinalIgnoreCase);

    /// <summary>A side's score; for tennis (no overall score) its games in each set, "6 3 7".</summary>
    private static string? Score(JsonElement side)
    {
        if (side.TryGetProperty("score", out var s))
        {
            var score = s.ValueKind switch
            {
                JsonValueKind.String => s.GetString(),
                JsonValueKind.Number => s.GetRawText(),
                JsonValueKind.Object => Str(s, "displayValue"),
                _ => null,
            };
            if (!string.IsNullOrEmpty(score))
                return score;
        }
        if (side.TryGetProperty("linescores", out var sets) && sets.ValueKind == JsonValueKind.Array)
        {
            var games = sets.EnumerateArray()
                .Select(x => x.TryGetProperty("value", out var v) && v.TryGetDouble(out var d) ? d.ToString("0", CultureInfo.InvariantCulture) : null)
                .OfType<string>().ToList();
            if (games.Count > 0)
                return string.Join(' ', games);
        }
        return null;
    }

    private static (FixtureState, string) Status(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("type", out var type))
            return (FixtureState.Scheduled, "");
        var state = Str(type, "state") switch
        {
            "in" => FixtureState.Live,
            "post" => FixtureState.Finished,
            _ => FixtureState.Scheduled,
        };
        return (state, Str(type, "shortDetail") ?? Str(type, "description") ?? "");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
