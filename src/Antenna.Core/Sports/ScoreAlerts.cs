using System.Globalization;
using Antenna.Core.Library;

namespace Antenna.Core.Sports;

/// <summary>A team (or player) the user follows, and the feed its matches are in.</summary>
public sealed record FollowedTeam(string Name, string FeedPath);

public enum ScoreEventKind
{
    KickOff,
    Score,
    FullTime,
}

/// <summary>Something worth a notification in a followed team's match.</summary>
public sealed record ScoreEvent(Fixture Fixture, ScoreEventKind Kind, string Title, string Text);

/// <summary>Followed teams (kept in the library's settings), and what changed in their matches between two updates.</summary>
public static class ScoreAlerts
{
    public const string FollowSetting = "sports.follow";

    /// <summary>Sports where each score is a moment worth an alert (not basketball's every basket, or tennis points).</summary>
    private static readonly HashSet<string> ScoreSports = ["Football", "Ice hockey", "American football", "Baseball"];

    public static IReadOnlyList<FollowedTeam> Followed(LibraryStore store) =>
        (store.GetSetting(FollowSetting) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new FollowedTeam(parts[1], parts[0]))
            .ToList();

    public static bool IsFollowed(LibraryStore store, string team) =>
        Followed(store).Any(t => Same(t.Name, team));

    /// <summary>Follows or unfollows a team; returns whether it's now followed.</summary>
    public static bool Toggle(LibraryStore store, string team, string feedPath)
    {
        var teams = Followed(store).ToList();
        var removed = teams.RemoveAll(t => Same(t.Name, team)) > 0;
        if (!removed)
            teams.Add(new FollowedTeam(team, feedPath));
        store.SetSetting(FollowSetting, teams.Count == 0 ? null : string.Join('\n', teams.Select(t => $"{t.FeedPath}|{t.Name}")));
        return !removed;
    }

    public static bool Involves(Fixture f, IReadOnlyList<FollowedTeam> teams) =>
        teams.Any(t => Same(t.Name, f.Home) || Same(t.Name, f.Away));

    /// <summary>Kick-offs, scores and full times in followed matches since the last update. A match first seen now is the baseline.</summary>
    public static IReadOnlyList<ScoreEvent> Changes(IReadOnlyDictionary<string, Fixture> before, IEnumerable<Fixture> now, IReadOnlyList<FollowedTeam> teams)
    {
        var events = new List<ScoreEvent>();
        foreach (var f in now)
        {
            if (!before.TryGetValue(f.Id, out var was) || !Involves(f, teams))
                continue;
            var score = $"{f.Home} {f.HomeScore ?? "0"}–{f.AwayScore ?? "0"} {f.Away}";
            if (was.State == FixtureState.Scheduled && f.State == FixtureState.Live)
                events.Add(new ScoreEvent(f, ScoreEventKind.KickOff, $"Kick-off: {f.Title}", $"{f.League} has started."));

            if (f.State != FixtureState.Scheduled && ScoreSports.Contains(f.Sport)
                && (Number(was.HomeScore) != Number(f.HomeScore) || Number(was.AwayScore) != Number(f.AwayScore)))
            {
                var scorer = Number(f.HomeScore) > Number(was.HomeScore) ? f.Home
                    : Number(f.AwayScore) > Number(was.AwayScore) ? f.Away : null;
                var clock = f.State == FixtureState.Live && f.Status.Length > 0 ? $" ({f.Status})" : "";
                var title = scorer is null ? $"Score update: {f.Title}"
                    : f.Sport == "Football" ? $"⚽ GOAL! {scorer}" : $"{scorer} score";
                events.Add(new ScoreEvent(f, ScoreEventKind.Score, title, score + clock));
            }

            if (was.State != FixtureState.Finished && f.State == FixtureState.Finished)
                events.Add(new ScoreEvent(f, ScoreEventKind.FullTime, $"Full time: {f.Title}", score));
        }
        return events;
    }

    private static int Number(string? score) =>
        int.TryParse(score, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static bool Same(string a, string? b) => b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
