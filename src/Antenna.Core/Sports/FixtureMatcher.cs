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

    /// <summary>What each channel's guide says it's showing at the time, keyed by <see cref="Key"/>.</summary>
    public IReadOnlyDictionary<string, Programme> ChannelProgrammes { get; init; } = new Dictionary<string, Programme>();

    public Programme? ProgrammeOn(LibraryEntry channel) => ChannelProgrammes.GetValueOrDefault(Key(channel));

    public static string Key(LibraryEntry channel) => channel.Item.SourceId + "|" + channel.Item.ExternalId;
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
            // Guides abbreviate: "Man City", "Man Utd", "Spurs", "Nott'm Forest".
            var hs = h.Select(Abbreviate).ToList();
            var aws = a.Select(Abbreviate).ToList();
            if (!hs.SequenceEqual(h) || !aws.SequenceEqual(a))
                queries.Add($"{string.Join(' ', hs)} {string.Join(' ', aws)}");
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
        // Every search's listings of this match, so channels that spell it differently ("Man City", "Manchester City")
        // are all found. A programme must actually be on around kick-off (not one that ended an hour before), and must
        // be this match: searches by team find other games too ("manchester" finds Man Utd and Man City).
        var exact = Queries(f)
            .SelectMany(query => store.SearchEvents(query, from, to, 20))
            .Where(h => h.Programme.Stop > f.Start.AddMinutes(10) && h.Programme.Start < f.Start.AddMinutes(30))
            .Where(h => IsThisMatch(f, h.Programme.Title))
            .ToList();
        if (exact.Count > 0)
            return FromHits(f, exact, coverage: false);

        // Per-event channels ("EPL 03: Nott'm Forest v Ipswich 20:00") aren't in the guide.
        if (f.Home is not null && f.Away is not null)
        {
            foreach (var query in Queries(f).Skip(1))
            {
                var channels = store.Search(query, MediaKind.Live, 8).Where(c => IsThisMatch(f, c.Item.Title)).ToList();
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
                .Where(h => h.Programme.Stop > f.Start.AddMinutes(10) && h.Programme.Start < f.Start.AddMinutes(30))
                // A listing naming a different match isn't coverage of this one.
                .Where(h => !NamesAnotherMatch(f, h.Programme.Title))
                .ToList();
            if (onAir.Count > 0)
                return FromHits(f, onAir, coverage: true);
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

    /// <summary>Channels from the hits, live programmes first, each with what its guide says it's showing.</summary>
    private static FixtureMatch FromHits(Fixture f, IReadOnlyList<Events.EventHit> hits, bool coverage)
    {
        var programmes = new Dictionary<string, Programme>();
        var channels = new List<LibraryEntry>();
        foreach (var hit in hits.OrderByDescending(h => h.Programme.IsOnAt(f.Start.AddMinutes(5))))
        {
            foreach (var channel in hit.Channels)
            {
                if (programmes.TryAdd(FixtureMatch.Key(channel), hit.Programme) && channels.Count < 8)
                    channels.Add(channel);
            }
        }
        return new FixtureMatch(f, channels, programmes[FixtureMatch.Key(channels[0])], coverage) { ChannelProgrammes = programmes };
    }

    /// <summary>What tells clubs from the same city apart.</summary>
    private static readonly string[] CityRivals = ["city", "united", "utd"];

    /// <summary>A word as guides usually shorten it ("manchester" → "man"), or as it is.</summary>
    private static string Abbreviate(string word) => word switch
    {
        "manchester" => "man",
        "tottenham" => "spurs",
        "wolverhampton" => "wolves",
        "nottingham" => "nott",
        "middlesbrough" => "boro",
        _ => word,
    };

    /// <summary>Short forms guides use for words in team names.</summary>
    private static readonly Dictionary<string, string[]> ShortForms = new()
    {
        ["man"] = ["manchester"], ["utd"] = ["united"], ["spurs"] = ["tottenham"], ["wolves"] = ["wolverhampton"],
        ["boro"] = ["middlesbrough"], ["psg"] = ["paris"], ["inter"] = ["internazionale"], ["atleti"] = ["atletico"],
        ["nottm"] = ["nottingham"], ["qpr"] = ["queens"], ["mufc"] = ["manchester", "united"], ["mcfc"] = ["manchester", "city"],
    };

    /// <summary>
    /// Whether a programme or channel title is this match: it names both sides, or names one and no other
    /// opponent ("Arsenal: Live"). "Man Utd v Liverpool" isn't Man City v Liverpool. Events without sides pass.
    /// </summary>
    public static bool IsThisMatch(Fixture f, string title)
    {
        if (f.Home is null || f.Away is null)
            return true;
        var words = Words(title).ToList();
        var home = Names(words, f.Home);
        var away = Names(words, f.Away);
        if (home && away)
            return true;
        return (home || away) && !HasVersus(words);
    }

    /// <summary>A "X v Y" listing that doesn't name both of this fixture's sides.</summary>
    internal static bool NamesAnotherMatch(Fixture f, string title) =>
        f.Home is not null && f.Away is not null && HasVersus(Words(title).ToList()) && !IsThisMatch(f, title);

    private static bool HasVersus(List<string> words) => words.Any(w => w is "v" or "vs" or "versus");

    /// <summary>
    /// Whether the title names a side: each of its distinctive words appears, whole, abbreviated ("Nott'm",
    /// "Man") or in a short form ("Utd"); for two-word distinctive names (people) the surname is enough.
    /// "Manchester City" needs "city" too when the title says which Manchester.
    /// </summary>
    private static bool Names(List<string> titleWords, string side)
    {
        var sideWords = Words(side).Where(w => w.Length >= 2).ToList();
        var key = Distinctive(side);
        if (key.Count == 0 || !titleWords.Any(t => Matches(t, key[0])) && !(key.Count > 1 && titleWords.Any(t => Matches(t, key[1]))))
            return false;
        // Clubs sharing a city are told apart by the generic word: "City" vs "United" after "Manchester".
        var qualifiers = sideWords.Where(w => w is "city" or "united" or "utd" or "athletic" or "county" or "rovers" or "wanderers" or "albion").ToList();
        var rivals = CityRivals.Except(qualifiers.Select(q => q == "utd" ? "united" : q)).ToList();
        if (qualifiers.Count > 0 && !qualifiers.Any(q => titleWords.Any(t => Matches(t, q)))
            && rivals.Any(r => titleWords.Any(t => Matches(t, r))))
            return false;
        return true;
    }

    private static bool Matches(string titleWord, string sideWord) =>
        titleWord == sideWord
        || titleWord.Length >= 3 && sideWord.StartsWith(titleWord, StringComparison.Ordinal)
        || sideWord.Length >= 4 && titleWord.StartsWith(sideWord, StringComparison.Ordinal)
        || ShortForms.TryGetValue(titleWord, out var forms) && forms.Contains(sideWord)
        || sideWord == "united" && titleWord == "utd";

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
