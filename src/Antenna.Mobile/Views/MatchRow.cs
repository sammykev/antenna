using System.ComponentModel;
using System.Runtime.CompilerServices;
using Antenna.Core.Sports;

namespace Antenna.Mobile.Views;

/// <summary>A fixture on the Matches page. Scores and where it's on fill in after the row is shown.</summary>
public sealed class MatchRow(Fixture fixture) : INotifyPropertyChanged
{
    private static readonly Color LiveColour = Color.FromArgb("#E5484D");

    public event PropertyChangedEventHandler? PropertyChanged;

    public Fixture Fixture { get; private set; } = fixture;

    public FixtureMatch? Match { get; private set; }

    public bool HasTeams => Fixture.Home is not null && Fixture.Away is not null;

    public bool HasNoTeams => !HasTeams;

    public string Name => Fixture.Name;

    public string? Home => Fixture.Home;

    public string? Away => Fixture.Away;

    public string? HomeLogo => Fixture.HomeLogo;

    public string? AwayLogo => Fixture.AwayLogo;

    public string HomeScore => Fixture.HomeScore ?? "";

    public string AwayScore => Fixture.AwayScore ?? "";

    public string Time => Fixture.State switch
    {
        FixtureState.Live => $"● {Fixture.Status}",
        FixtureState.Finished => Fixture.Status.Length > 0 ? Fixture.Status : "FT",
        _ => Fixture.Start.LocalDateTime.ToString("HH:mm"),
    };

    public Color TimeColour => Fixture.State == FixtureState.Live ? LiveColour : Colors.White;

    public bool OnYourChannels => Match?.OnYourChannels == true;

    /// <summary>Live, or starting within 20 minutes.</summary>
    public bool IsOnSoon => Fixture.State == FixtureState.Live
        || Fixture.State == FixtureState.Scheduled && Fixture.Start - DateTimeOffset.Now < TimeSpan.FromMinutes(20);

    public string Channel => Match switch
    {
        null => "Looking on your channels…",
        { OnYourChannels: false } => Fixture.Broadcasts.Count > 0
            ? $"Not on your channels (TV: {string.Join(", ", Fixture.Broadcasts.Take(2))})"
            : "Not on your channels",
        { Channels: var channels } => $"▶ {channels[0].Item.Title}{(channels.Count > 1 ? $" +{channels.Count - 1}" : "")}",
    };

    public Color ChannelColour => OnYourChannels ? Color.FromArgb("#8FA9FF") : Color.FromArgb("#9AA3B2");

    public void Update(Fixture fixture)
    {
        Fixture = fixture;
        if (Match is not null)
            Match = Match with { Fixture = fixture };
        Changed(nameof(Time), nameof(TimeColour), nameof(HomeScore), nameof(AwayScore));
    }

    public void Found(FixtureMatch match)
    {
        Match = match with { Fixture = Fixture };
        Changed(nameof(Channel), nameof(ChannelColour), nameof(OnYourChannels));
    }

    private void Changed(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One competition's fixtures, for the grouped list.</summary>
public sealed class MatchGroup(string league, IEnumerable<MatchRow> rows) : List<MatchRow>(rows)
{
    public string League => league;
}
