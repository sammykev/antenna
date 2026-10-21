using Antenna.Core.Library;
using Antenna.Core.Sports;

namespace Antenna.Core.Tests;

public sealed class ScoreAlertTests
{
    private static readonly IReadOnlyList<FollowedTeam> Arsenal = [new("Arsenal", "soccer/eng.1")];

    private static Fixture Match(FixtureState state, string? home = null, string? away = null, string status = "", string sport = "Football") => new()
    {
        Id = "soccer/eng.1/1", Sport = sport, League = "Premier League", Start = DateTimeOffset.Now, State = state, Status = status,
        Home = "Arsenal", Away = "Chelsea", HomeScore = home, AwayScore = away, Name = "Arsenal v Chelsea",
    };

    private static IReadOnlyList<ScoreEvent> Between(Fixture before, Fixture now, IReadOnlyList<FollowedTeam>? teams = null) =>
        ScoreAlerts.Changes(new Dictionary<string, Fixture> { [before.Id] = before }, [now], teams ?? Arsenal);

    [Fact]
    public void Kick_off_goal_and_full_time()
    {
        var kickOff = Assert.Single(Between(Match(FixtureState.Scheduled), Match(FixtureState.Live, "0", "0", "1'")));
        Assert.Equal(ScoreEventKind.KickOff, kickOff.Kind);

        var goal = Assert.Single(Between(Match(FixtureState.Live, "0", "0"), Match(FixtureState.Live, "0", "1", "23'")));
        Assert.Equal("⚽ GOAL! Chelsea", goal.Title);
        Assert.Equal("Arsenal 0–1 Chelsea (23')", goal.Text);

        var fullTime = Assert.Single(Between(Match(FixtureState.Live, "2", "1"), Match(FixtureState.Finished, "2", "1", "FT")));
        Assert.Equal(ScoreEventKind.FullTime, fullTime.Kind);
        Assert.Equal("Arsenal 2–1 Chelsea", fullTime.Text);
    }

    [Fact]
    public void Nothing_for_unfollowed_teams_new_matches_or_basketball()
    {
        Assert.Empty(Between(Match(FixtureState.Live, "0", "0"), Match(FixtureState.Live, "1", "0"), [new("Spurs", "soccer/eng.1")]));
        Assert.Empty(ScoreAlerts.Changes(new Dictionary<string, Fixture>(), [Match(FixtureState.Live, "1", "0")], Arsenal));
        Assert.Empty(Between(Match(FixtureState.Live, "80", "78", sport: "Basketball"), Match(FixtureState.Live, "82", "78", sport: "Basketball")));
    }

    [Fact]
    public void Follows_are_kept_in_the_library()
    {
        var path = Path.Combine(Path.GetTempPath(), $"antenna-follow-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new LibraryStore(path))
            {
                Assert.True(ScoreAlerts.Toggle(store, "Arsenal", "soccer/eng.1"));
                Assert.True(ScoreAlerts.Toggle(store, "Lakers", "basketball/nba"));
                Assert.True(ScoreAlerts.IsFollowed(store, "arsenal"));
                Assert.False(ScoreAlerts.Toggle(store, "Arsenal", "soccer/eng.1"));
                Assert.Equal("Lakers", Assert.Single(ScoreAlerts.Followed(store)).Name);
                Assert.Equal("basketball/nba", ScoreAlerts.Followed(store)[0].FeedPath);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                File.Delete(f);
        }
    }

    [Fact]
    public void Feed_path_comes_from_the_id()
    {
        Assert.Equal("soccer/eng.1", Match(FixtureState.Live).FeedPath);
    }
}
