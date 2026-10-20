using Antenna.Core.Library;

namespace Antenna.Core.Sports;

/// <summary>A fixture and where to watch it in your own playlists.</summary>
public sealed record FixtureMatch(Fixture Fixture, IReadOnlyList<LibraryEntry> Channels, Programme? Programme)
{
    public bool OnYourChannels => Channels.Count > 0;
}

/// <summary>
/// Finds a fixture on the user's channels: guide programmes airing around kick-off whose title names
/// both sides, or per-event channels named after the match. Guides abbreviate ("Nott'm Forest", "Man Utd"),
/// so several searches are tried, most specific first.
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
        return new FixtureMatch(f, [], null);
    }

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
