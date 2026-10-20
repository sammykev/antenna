using System.Text.Json;
using Antenna.Core.Library;
using Antenna.Core.Sports;

namespace Antenna.Core.Tests;

public sealed class FixtureTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"antenna-fx-{Guid.NewGuid():N}.db");
    private readonly LibraryStore _store;
    private static readonly DateTimeOffset Kickoff = new(2026, 10, 20, 19, 0, 0, TimeSpan.Zero);

    public FixtureTests() => _store = new LibraryStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(f);
    }

    private static readonly FixtureFeed Epl = new("soccer/eng.1", "Football", "Premier League");

    [Fact]
    public void Parses_team_fixtures_with_status_and_scores()
    {
        var json = """
            {"events":[
              {"id":"1","date":"2026-10-20T19:00Z","name":"Ipswich Town at Nottingham Forest",
               "status":{"type":{"state":"in","shortDetail":"65'"}},
               "competitions":[{"competitors":[
                  {"homeAway":"home","score":"2","team":{"displayName":"Nottingham Forest","logo":"http://l/nfo.png"}},
                  {"homeAway":"away","score":"1","team":{"displayName":"Ipswich Town","logo":"http://l/ips.png"}}],
                 "broadcasts":[{"names":["Sky Sports"]}]}]},
              {"id":"2","date":"not a date","name":"Broken"}
            ]}
            """;
        var fixtures = EspnFixtures.Parse(JsonDocument.Parse(json).RootElement, Epl);

        var f = Assert.Single(fixtures);
        Assert.Equal("Nottingham Forest vs Ipswich Town", f.Title);
        Assert.Equal(FixtureState.Live, f.State);
        Assert.Equal("65'", f.Status);
        Assert.Equal(("2", "1"), (f.HomeScore, f.AwayScore));
        Assert.Equal(["Sky Sports"], f.Broadcasts);
        Assert.Equal(Kickoff, f.Start);
    }

    [Fact]
    public void Parses_tennis_matches_inside_tournaments_and_events_without_sides()
    {
        var tennis = """
            {"events":[{"id":"t","name":"European Open","groupings":[{"competitions":[
               {"id":"m1","date":"2026-10-20T10:30Z","status":{"type":{"state":"pre","shortDetail":"10:30 AM"}},
                "competitors":[{"athlete":{"displayName":"Federico Cina"}},{"athlete":{"displayName":"Joel Schwaerzler"}}]}]}]}]}
            """;
        var match = Assert.Single(EspnFixtures.Parse(JsonDocument.Parse(tennis).RootElement, new("tennis/atp", "Tennis", "ATP")));
        Assert.Equal("Federico Cina vs Joel Schwaerzler", match.Title);
        Assert.Equal("ATP · European Open", match.League);
        Assert.Null(match.HomeScore); // Not started: no score.

        var finished = """
            {"events":[{"id":"t","name":"Japan Open","groupings":[{"competitions":[
               {"id":"m2","date":"2026-10-20T10:30Z","status":{"type":{"state":"post","shortDetail":"Final"}},
                "competitors":[{"athlete":{"displayName":"Ann Li"},"linescores":[{"value":6},{"value":3},{"value":7}]},
                               {"athlete":{"displayName":"Emerson Jones"},"linescores":[{"value":4},{"value":6},{"value":5}]}]},
               {"id":"m3","date":"2026-10-20T11:30Z","status":{"type":{"state":"pre"}},
                "competitors":[{"athlete":{"displayName":"TBD"}},{"athlete":{"displayName":"TBD"}}]}]}]}]}
            """;
        var done = Assert.Single(EspnFixtures.Parse(JsonDocument.Parse(finished).RootElement, new("tennis/wta", "Tennis", "WTA")));
        Assert.Equal(("6 3 7", "4 6 5"), (done.HomeScore, done.AwayScore));

        var f1 = """{"events":[{"id":"r","date":"2026-10-23T17:30Z","name":"MSC Cruises United States Grand Prix","status":{"type":{"state":"pre"}},"competitions":[{"competitors":[]}]}]}""";
        var race = Assert.Single(EspnFixtures.Parse(JsonDocument.Parse(f1).RootElement, new("racing/f1", "Motorsport", "Formula 1")));
        Assert.Equal("MSC Cruises United States Grand Prix", race.Title);
        Assert.Contains("states grand prix", FixtureMatcher.Queries(race));
    }

    [Fact]
    public void Distinctive_words_skip_generic_ones()
    {
        Assert.Equal(["nottingham", "forest"], FixtureMatcher.Distinctive("Nottingham Forest"));
        Assert.Equal(["ipswich"], FixtureMatcher.Distinctive("Ipswich Town"));
        Assert.Equal(["manchester"], FixtureMatcher.Distinctive("Manchester United"));
    }

    private Fixture Forest(FixtureState state = FixtureState.Scheduled) => new()
    {
        Id = "x", Sport = "Football", League = "Premier League", Start = Kickoff, State = state, Name = "Ipswich Town at Nottingham Forest",
        Home = "Nottingham Forest", Away = "Ipswich Town",
    };

    private void SeedChannels(params (string Id, string Title, string? Epg)[] channels)
    {
        _store.SaveSource(new PlaylistSource { Id = "s", Name = "Provider", Kind = SourceKind.M3u, Location = "x" });
        _store.ReplaceItems("s", channels.Select((c, i) => new MediaItem
        {
            SourceId = "s", ExternalId = c.Id, Kind = MediaKind.Live, Title = c.Title, Group = "Sports", EpgChannelId = c.Epg, StreamUrl = $"http://h/{c.Id}", SortIndex = i,
        }).ToList());
    }

    [Fact]
    public void Finds_a_fixture_in_the_guide_despite_abbreviated_names()
    {
        SeedChannels(("1", "Sky Sports Premier League", "sspl"), ("2", "Sky Sports News", "ssn"));
        using (var w = _store.BeginGuideImport("s"))
        {
            w.Add(new Programme { ChannelId = "sspl", Title = "Live: Nott'm Forest v Ipswich", Start = Kickoff.AddMinutes(-30), Stop = Kickoff.AddHours(2) });
            w.Add(new Programme { ChannelId = "ssn", Title = "Forest and Ipswich: the build-up", Start = Kickoff.AddHours(-3), Stop = Kickoff.AddHours(-2) });
            w.Commit();
        }

        var match = FixtureMatcher.Find(_store, Forest());

        Assert.True(match.OnYourChannels);
        Assert.Equal("Sky Sports Premier League", Assert.Single(match.Channels).Item.Title);
        Assert.Equal("Live: Nott'm Forest v Ipswich", match.Programme?.Title);
    }

    [Fact]
    public void Finds_per_event_channels_and_reports_when_it_is_not_on()
    {
        SeedChannels(("9", "EPL 03: Nott'm Forest v Ipswich 20:00", null), ("10", "BBC One", "bbc1"));
        var match = FixtureMatcher.Find(_store, Forest());
        Assert.Equal("EPL 03: Nott'm Forest v Ipswich 20:00", Assert.Single(match.Channels).Item.Title);
        Assert.Null(match.Programme);

        var other = Forest() with { Home = "Arsenal", Away = "Chelsea", Name = "Chelsea at Arsenal" };
        Assert.False(FixtureMatcher.Find(_store, other).OnYourChannels);
    }
}
