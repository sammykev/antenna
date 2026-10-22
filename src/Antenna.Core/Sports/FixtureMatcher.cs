using Antenna.Core.Library;

namespace Antenna.Core.Sports;

/// <summary>A fixture and where to watch it in your own playlists.</summary>
/// <param name="IsCoverage">
/// The channels show this sport or tournament around kick-off ("ATP Tennis", "NBA TV") but the guide
/// doesn't name this exact match, so it may be another game.
/// </param>
public sealed record FixtureMatch(Fixture Fixture, IReadOnlyList<LibraryEntry> Channels, Programme? Programme, bool IsCoverage = false)
{
    public bool OnYourChannels => Channels.Count > 0;
}

/// <summary>
/// Finds a fixture on the user's channels, most certain first: guide programmes airing around kick-off
/// that name both sides, per-event channels named after the match, channels of the networks the feed
/// says show it, then coverage of the tournament or sport ("ATP Tennis", "NBA") on air at the time, or
/// channels dedicated to it ("Tennis Channel", "NBA TV"). Guides abbreviate ("Nott'm Forest", "Man Utd"),
/// so several searches are tried at each step.
/// </summary>
public static class FixtureMatcher
{
    /// <summary>Words that don't tell clubs apart ("Ipswich Town" is found by "Ipswich").</summary>
    private static readonly HashSet<string> Generic =
    [
        "fc", "afc", "cf", "sc", "ac", "as", "cd", "ud", "sv", "fk", "bk", "club", "de", "la", "the",
        "town", "city", "county", "united", "athletic", "rovers", "wanderers", "albion", "real", "sporting",
    ];

    /// <summary>Searches to try for a fixture, most specific first.</summary>
    public static IReadOnlyList<string> Queries(Fixture f)
    {
        var queries = new List<string>();
        if (f.Home is { } home && f.Away is { } away)
        {
            queries.Add($"{home} {away}");
            var h = Distinctive(home);
            var a = Distinctive(away);
            foreach (var hw in h)
                foreach (var aw in a)
                    queries.Add($"{hw} {aw}");
            // Some guides only name one side ("Arsenal: Live"); the time window keeps this from straying far.
            if (h.Count > 0 && f.Sport == "Football")
                queries.Add($"{h[0]} {f.League.Split(' ')[0]}");
        }
        else
        {
            var words = Words(f.Name).Where(w => !Generic.Contains(w)).ToList();
            if (words.Count > 0)
            {
                queries.Add(string.Join(' ', words.TakeLast(3)));
                if (words.Count > 3)
                    queries.Add(string.Join(' ', words.Take(2)));
            }
        }
        return queries.Distinct().ToList();
    }

    public static FixtureMatch Find(LibraryStore store, Fixture f)
    {
        var from = f.Start.AddMinutes(-20);
        var to = f.Start.AddHours(3);
        foreach (var query in Queries(f))
        {
            var hits = store.SearchEvents(query, from, to, 10);
            // A programme must actually be on around kick-off (not one that ended an hour before).
            var onAir = hits.Where(h => h.Programme.Stop > f.Start.AddMinutes(10) && h.Programme.Start < f.Start.AddMinutes(30)).ToList();
            if (onAir.Count > 0)
            {
                var channels = onAir.SelectMany(h => h.Channels).DistinctBy(c => (c.Item.SourceId, c.Item.ExternalId)).Take(8).ToList();
                return new FixtureMatch(f, channels, onAir[0].Programme);
            }
        }

        // Per-event channels ("EPL 03: Nott'm Forest v Ipswich 20:00") aren't in the guide.
        if (f.Home is not null && f.Away is not null)
        {
            foreach (var query in Queries(f).Skip(1))
            {
                var channels = store.Search(query, MediaKind.Live, 8);
                if (channels.Count > 0)
                    return new FixtureMatch(f, channels, null);
            }
        }

        // The networks the feed lists ("ESPN", "TNT"), when the playlist has them.
        foreach (var network in f.Broadcasts.Where(b => b.Length >= 3).Take(3))
        {
            var channels = store.Search(network, MediaKind.Live, 4)
                .Where(c => TextMatch.Fold(c.Item.Title).Contains(TextMatch.Fold(network), StringComparison.Ordinal)).ToList();
            if (channels.Count > 0)
                return new FixtureMatch(f, channels, null);
        }

        // Coverage of the tournament or sport on air around the start ("ATP Tennis: European Open").
        foreach (var query in CoverageQueries(f))
        {
            var onAir = store.SearchEvents(query, from, to, 20)
                .Where(h => h.Programme.Stop > f.Start.AddMinutes(10) && h.Programme.Start < f.Start.AddMinutes(30)).ToList();
            if (onAir.Count > 0)
            {
                var channels = onAir.SelectMany(h => h.Channels).DistinctBy(c => (c.Item.SourceId, c.Item.ExternalId)).Take(8).ToList();
                return new FixtureMatch(f, channels, onAir[0].Programme, IsCoverage: true);
            }
        }

        // Channels given over to the sport ("Tennis Channel", "NBA TV", "F1 TV").
        foreach (var name in ChannelNames(f))
        {
            var channels = store.Search(name, MediaKind.Live, 6);
            if (channels.Count > 0)
                return new FixtureMatch(f, channels, null, IsCoverage: true);
        }
        return new FixtureMatch(f, [], null);
    }

    /// <summary>
    /// Searches for coverage that may not name this match: the tournament (tennis, races, fight nights)
    /// and then the competition or sport.
    /// </summary>
    public static IReadOnlyList<string> CoverageQueries(Fixture f)
    {
        var queries = new List<string>();
        if (f.Sport is "Tennis" or "Motorsport" or "MMA")
        {
            // "BNP Paribas Fortis European Open" → "european open"; sponsors come first, the name last.
            var words = Words(f.Name).Where(w => !Generic.Contains(w) && w.Length >= 3).ToList();
            if (words.Count >= 2)
                queries.Add(string.Join(' ', words.TakeLast(2)));
        }
        queries.AddRange(f.FeedPath switch
        {
            "tennis/atp" => ["atp tennis", "atp", "tennis"],
            "tennis/wta" => ["wta tennis", "wta", "tennis"],
            "basketball/nba" => ["nba"],
            "football/nfl" => ["nfl"],
            "hockey/nhl" => ["nhl"],
            "baseball/mlb" => ["mlb"],
            "racing/f1" => ["formula 1", "f1", "grand prix"],
            "mma/ufc" => ["ufc"],
            // Football names both teams, found above; the league is the fallback.
            _ => f.Sport == "Football" ? [f.League] : [],
        });
        return queries.Distinct().ToList();
    }

    /// <summary>Names of channels dedicated to the fixture's sport or competition.</summary>
    public static IReadOnlyList<string> ChannelNames(Fixture f) => f.FeedPath switch
    {
        "tennis/atp" or "tennis/wta" => ["tennis"],
        "basketball/nba" => ["nba"],
        "football/nfl" => ["nfl"],
        "hockey/nhl" => ["nhl"],
        "baseball/mlb" => ["mlb"],
        "racing/f1" => ["f1", "formula 1"],
        "mma/ufc" => ["ufc"],
        _ => [],
    };

    /// <summary>Up to two words that identify a team: "Nottingham Forest" → nottingham, forest.</summary>
    internal static IReadOnlyList<string> Distinctive(string team)
    {
        var words = Words(team).Where(w => !Generic.Contains(w) && w.Length >= 3).ToList();
        if (words.Count == 0)
            words = Words(team).Where(w => w.Length >= 2).ToList();
        return words.OrderByDescending(w => w.Length).Take(2).ToList();
    }

    private static IEnumerable<string> Words(string s) =>
        TextMatch.Fold(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.All(char.IsDigit) || w.Length > 2);
}
