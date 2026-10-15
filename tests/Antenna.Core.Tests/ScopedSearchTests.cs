using Antenna.Core.Library;

namespace Antenna.Core.Tests;

public sealed class ScopedSearchTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"antenna-ss-{Guid.NewGuid():N}.db");
    private readonly LibraryStore _store;

    public ScopedSearchTests()
    {
        _store = new LibraryStore(_path);
        foreach (var id in new[] { "a", "b" })
            _store.SaveSource(new PlaylistSource { Id = id, Name = id.ToUpperInvariant(), Kind = SourceKind.M3u, Location = id });
        MediaItem Item(string source, string id, string title, string group, MediaKind kind = MediaKind.Live) => new()
        {
            SourceId = source, ExternalId = id, Kind = kind, Title = title, Group = group, StreamUrl = $"http://{source}/{id}",
        };
        _store.ReplaceItems("a",
        [
            Item("a", "1", "Sky Sports Football", "UK | SPORTS"),
            Item("a", "2", "Sky News", "UK | NEWS"),
            Item("a", "3", "Football Focus", "Movies: Documentary", MediaKind.Movie),
            Item("a", "4", "Télé Sport", "FR | Télé"),
        ]);
        _store.ReplaceItems("b", [Item("b", "1", "BT Sport Football", "UK | SPORTS")]);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(f);
    }

    private static string[] Ids(IEnumerable<LibraryEntry> entries) => entries.Select(e => $"{e.Item.SourceId}:{e.Item.ExternalId}").Order().ToArray();

    [Fact]
    public void Searches_one_type_one_playlist_or_one_category()
    {
        Assert.Equal(["a:1", "a:3", "b:1"], Ids(_store.Search("football")));
        Assert.Equal(["a:1", "b:1"], Ids(_store.Search("football", MediaKind.Live)));
        Assert.Equal(["a:1"], Ids(_store.Search("football", MediaKind.Live, sourceId: "a")));
        Assert.Equal(["a:1", "b:1"], Ids(_store.Search("football", MediaKind.Live, group: "UK | SPORTS")));
        Assert.Empty(_store.Search("football", MediaKind.Live, group: "UK | NEWS"));
    }

    [Fact]
    public void Finds_categories_ignoring_case_accents_and_punctuation()
    {
        Assert.Equal([(MediaKind.Live, "UK | SPORTS")], _store.SearchGroups("sports"));
        Assert.Equal([(MediaKind.Live, "FR | Télé")], _store.SearchGroups("tele"));
        Assert.Equal([(MediaKind.Movie, "Movies: Documentary")], _store.SearchGroups("documentary movies"));
        Assert.Empty(_store.SearchGroups("   "));
        Assert.True(TextMatch.ContainsAll("UK | SPORTS", "uk sport"));
        Assert.False(TextMatch.ContainsAll("UK | NEWS", "sport"));
    }

    [Fact]
    public void Replacing_one_type_keeps_the_others()
    {
        _store.ReplaceItems("a", [new MediaItem { SourceId = "a", ExternalId = "9", Kind = MediaKind.Live, Title = "New Channel", Group = "UK | NEWS", StreamUrl = "http://a/9" }], MediaKind.Live);

        Assert.Equal(["a:9"], Ids(_store.GetItems(MediaKind.Live, "a")));
        Assert.Equal(["a:3"], Ids(_store.GetItems(MediaKind.Movie, "a"))); // Movies untouched.
        Assert.Equal(["a:9"], Ids(_store.Search("new channel")));
        Assert.Empty(_store.Search("sky")); // Old live channels gone from search too.
    }
}
