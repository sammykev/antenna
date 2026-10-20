namespace Antenna.Core.Sports;

public enum FixtureState
{
    Scheduled,
    Live,
    Finished,
}

/// <summary>One match (or race, fight night, tennis match) from a fixtures feed.</summary>
public sealed record Fixture
{
    public required string Id { get; init; }

    /// <summary>"Football", "Basketball", …</summary>
    public required string Sport { get; init; }
    public required string League { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required FixtureState State { get; init; }

    /// <summary>"FT", "65'", "Q3 4:12", "7:00 PM EDT": the feed's short status.</summary>
    public string Status { get; init; } = "";

    /// <summary>Teams or players; null for events without sides (a Grand Prix, a UFC card).</summary>
    public string? Home { get; init; }
    public string? Away { get; init; }
    public string? HomeLogo { get; init; }
    public string? AwayLogo { get; init; }
    public string? HomeScore { get; init; }
    public string? AwayScore { get; init; }

    /// <summary>The event's own name, used when there are no sides ("United States Grand Prix").</summary>
    public required string Name { get; init; }

    /// <summary>TV networks the feed lists (often US-centric).</summary>
    public IReadOnlyList<string> Broadcasts { get; init; } = [];

    public string Title => Home is not null && Away is not null ? $"{Home} vs {Away}" : Name;
}

/// <summary>A competition in the feed: its path ("soccer/eng.1") and how to label it.</summary>
public sealed record FixtureFeed(string Path, string Sport, string League);
