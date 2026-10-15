using System.Text;

namespace Antenna.Core.Events;

/// <summary>A programme found by event search, with every channel showing it.</summary>
public sealed record EventHit(Programme Programme, IReadOnlyList<LibraryEntry> Channels)
{
    public bool IsLive(DateTimeOffset now) => Programme.IsOnAt(now);
}

/// <summary>
/// Turns how people type sports searches into a guide query: "man utd vs man city live"
/// becomes Manchester AND United AND City (each also matching its short form), ignoring
/// filler like "vs", "live" or "match". Every word is a prefix, so "lake" finds "Lakers".
/// </summary>
public static class EventSearch
{
    /// <summary>Words that don't help find a programme.</summary>
    private static readonly HashSet<string> StopWords =
    [
        "vs", "v", "versus", "against", "live", "match", "matches", "game", "games", "the", "at", "and",
        "watch", "today", "tonight", "stream", "streaming", "fixture", "fixtures", "free", "hd",
    ];

    /// <summary>Common short forms and nicknames, each with what guides usually spell out.</summary>
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["man"] = ["manchester"],
        ["utd"] = ["united"],
        ["mufc"] = ["manchester", "united"],
        ["mcfc"] = ["manchester", "city"],
        ["spurs"] = ["tottenham"],
        ["wolves"] = ["wolverhampton"],
        ["boro"] = ["middlesbrough"],
        ["forest"] = ["nottingham"],
        ["toon"] = ["newcastle"],
        ["gunners"] = ["arsenal"],
        ["reds"] = ["liverpool"],
        ["blues"] = ["chelsea"],
        ["barca"] = ["barcelona"],
        ["psg"] = ["paris"],
        ["bayern"] = ["munchen", "munich"],
        ["inter"] = ["internazionale"],
        ["juve"] = ["juventus"],
        ["atleti"] = ["atletico"],
        ["epl"] = ["premier"],
        ["prem"] = ["premier"],
        ["ucl"] = ["champions"],
        ["uel"] = ["europa"],
        ["f1"] = ["formula"],
        ["motogp"] = ["moto"],
        ["ufc"] = ["mma"],
        ["nba"] = ["basketball"],
        ["nfl"] = ["american football"],
        ["nhl"] = ["hockey"],
        ["mlb"] = ["baseball"],
        ["atp"] = ["tennis"],
        ["wta"] = ["tennis"],
        ["ipl"] = ["cricket"],
    };

    /// <summary>Ready-made searches offered as chips.</summary>
    public static readonly IReadOnlyList<(string Label, string Query)> Suggestions =
    [
        ("Football", "football OR soccer OR premier OR league OR cup"),
        ("Premier League", "premier league"),
        ("Champions League", "champions league"),
        ("NBA", "nba"),
        ("NFL", "nfl"),
        ("Tennis", "tennis"),
        ("F1", "f1"),
        ("UFC / Boxing", "ufc OR boxing"),
        ("Cricket", "cricket"),
        ("Golf", "golf"),
        ("Rugby", "rugby"),
    ];

    /// <summary>
    /// Builds an FTS5 expression. Terms are ANDed; "OR" between words in the input makes
    /// alternatives ("ufc OR boxing"). Returns null if nothing searchable is left.
    /// </summary>
    public static string? BuildExpression(string query)
    {
        var alternatives = new List<List<string>>();
        var current = new List<string>();
        foreach (var raw in Tokenize(query))
        {
            if (raw == "OR")
            {
                if (current.Count > 0)
                    alternatives.Add(current);
                current = [];
                continue;
            }
            var word = raw.ToLowerInvariant();
            if (StopWords.Contains(word))
                continue;
            current.Add(Term(word));
        }
        if (current.Count > 0)
            alternatives.Add(current);

        var groups = alternatives.Where(g => g.Count > 0).Select(g => g.Count == 1 ? g[0] : "(" + string.Join(" AND ", g) + ")").ToList();
        return groups.Count switch
        {
            0 => null,
            1 => groups[0],
            _ => string.Join(" OR ", groups),
        };
    }

    /// <summary>One word, or (word OR its long form) when we know a synonym.</summary>
    private static string Term(string word)
    {
        var options = new List<string> { Quote(word) };
        if (Synonyms.TryGetValue(word, out var longForms))
        {
            foreach (var form in longForms)
            {
                var parts = form.Split(' ');
                options.Add(parts.Length == 1 ? Quote(form) : "(" + string.Join(" AND ", parts.Select(Quote)) + ")");
            }
        }
        return options.Count == 1 ? options[0] : "(" + string.Join(" OR ", options) + ")";
    }

    private static string Quote(string word) => $"\"{word}\"*";

    /// <summary>Letters/digits only, so input can't inject FTS syntax; "OR" in capitals is kept as an operator.</summary>
    private static IEnumerable<string> Tokenize(string query)
    {
        var sb = new StringBuilder();
        foreach (var ch in query + " ")
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
    }
}
