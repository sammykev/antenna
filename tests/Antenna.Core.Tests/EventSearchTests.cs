using Antenna.Core.Events;
using Antenna.Core.Library;

namespace Antenna.Core.Tests;

public sealed class EventSearchTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"antenna-ev-{Guid.NewGuid():N}.db");
    private readonly LibraryStore _store;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public EventSearchTests() => _store = new LibraryStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(f);
    }

    [Theory]
    [InlineData("man utd vs man city live", "(\"man\"* OR \"manchester\"*) AND (\"utd\"* OR \"united\"*) AND (\"man\"* OR \"manchester\"*) AND \"city\"*")]
    [InlineData("Lakers v Celtics", "(\"lakers\"* AND \"celtics\"*)")]
    [InlineData("ufc OR boxing", "\"ufc\"* OR (\"mma\"*) OR \"boxing\"*")]
    [InlineData("vs live", null)]
    [InlineData("nfl", "(\"nfl\"* OR (\"american\"* AND \"football\"*))")]
    public void Builds_expressions(string query, string? expected)
    {
        var actual = EventSearch.BuildExpression(query);
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }
        // Compare ignoring how single groups are parenthesised.
        Assert.Equal(Normalize(expected), Normalize(actual!));
    }

    private static string Normalize(string s) => s.Replace("(", "").Replace(")", "");

    private void Seed()
    {
        _store.SaveSource(new PlaylistSource { Id = "s", Name = "Provider", Kind = SourceKind.M3u, Location = "x" });
        MediaItem Ch(string id, string title, string group, string? epg) => new()
        {
            SourceId = "s", ExternalId = id, Kind = MediaKind.Live, Title = title, Group = group, EpgChannelId = epg, StreamUrl = $"http://h/{id}", SortIndex = int.Parse(id),
        };
        _store.ReplaceItems("s",
        [
            Ch("1", "Sky Sports Main Event", "Sports", "ssme"),
            Ch("2", "Sky Sports Main Event HD", "Sports", "ssme"),
            Ch("3", "NBA 03: Lakers vs Celtics", "Events", null),
            Ch("4", "Adult Channel", "Adult", "adult"),
        ]);
        using var w = _store.BeginGuideImport("s");
        w.Add(new Programme { ChannelId = "ssme", Title = "Live: Manchester United v Manchester City", Description = "Premier League derby", Start = Now.AddMinutes(-30), Stop = Now.AddMinutes(90) });
        w.Add(new Programme { ChannelId = "ssme", Title = "Manchester City v Arsenal", Start = Now.AddDays(1), Stop = Now.AddDays(1).AddHours(2) });
        w.Add(new Programme { ChannelId = "ssme", Title = "Tennis: Final", Start = Now.AddHours(3), Stop = Now.AddHours(5) });
        w.Add(new Programme { ChannelId = "adult", Title = "Late Tennis Special", Start = Now.AddHours(1), Stop = Now.AddHours(2) });
        w.Commit();
    }

    [Fact]
    public void Finds_matches_by_nickname_with_live_first_and_channels_merged()
    {
        Seed();

        var hits = _store.SearchEvents("man utd vs man city", Now.AddHours(-6), Now.AddDays(7));

        var hit = Assert.Single(hits);
        Assert.True(hit.IsLive(Now));
        Assert.Equal(2, hit.Channels.Count);

        var city = _store.SearchEvents("man city", Now.AddHours(-6), Now.AddDays(7));
        Assert.Equal(["Live: Manchester United v Manchester City", "Manchester City v Arsenal"], city.Select(h => h.Programme.Title));
    }

    [Fact]
    public void Respects_time_window_and_parental_lock()
    {
        Seed();
        _store.SetGroupFlag(MediaKind.Live, "Adult", GroupFlag.Locked, true);

        Assert.Equal(2, _store.SearchEvents("tennis", Now, Now.AddHours(6)).Count);
        _store.ParentalLockActive = true;
        Assert.Equal(["Tennis: Final"], _store.SearchEvents("tennis", Now, Now.AddHours(6)).Select(h => h.Programme.Title));
        Assert.Empty(_store.SearchEvents("arsenal", Now, Now.AddHours(6)));
    }

    [Fact]
    public void Finds_event_channels_by_name()
    {
        Seed();
        Assert.Equal("3", Assert.Single(_store.SearchEventChannels("lakers vs celtics live")).Item.ExternalId);
        Assert.Equal("3", Assert.Single(_store.SearchEventChannels("nba")).Item.ExternalId);
    }

    [Fact]
    public void Pinned_categories_come_first()
    {
        Seed();
        _store.SetGroupFlag(MediaKind.Live, "Events", GroupFlag.Pinned, true);
        Assert.Equal(["Events", "Sports", "Adult"], _store.GetGroups(MediaKind.Live));
    }

    [Fact]
    public void Existing_guides_get_indexed_on_upgrade()
    {
        Seed();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM programmes_fts";
            cmd.ExecuteNonQuery();
        }
        using var reopened = new LibraryStore(_path);
        Assert.NotEmpty(reopened.SearchEvents("tennis", Now, Now.AddHours(6)));
    }
}
