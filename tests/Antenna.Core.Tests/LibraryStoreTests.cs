using Antenna.Core.Library;

namespace Antenna.Core.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"antenna-test-{Guid.NewGuid():N}.db");
    private readonly LibraryStore _store;

    public LibraryStoreTests() => _store = new LibraryStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(f);
    }

    private static PlaylistSource Source(string id, string name) =>
        new() { Id = id, Name = name, Kind = SourceKind.M3u, Location = $"http://{id}/list.m3u", Color = "#FF0000" };

    private static MediaItem Item(string source, string title, MediaKind kind = MediaKind.Live, string group = "", int sort = 0) =>
        new() { SourceId = source, ExternalId = title, Kind = kind, Title = title, Group = group, StreamUrl = $"http://{source}/{title}", SortIndex = sort };

    [Fact]
    public void Sources_round_trip_in_insertion_order()
    {
        var a = Source("a", "First") with { Username = "u", Password = "p", LastRefreshed = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) };
        _store.SaveSource(a);
        _store.SaveSource(Source("b", "Second"));
        _store.SaveSource(a with { Name = "First renamed" });

        var sources = _store.GetSources();

        Assert.Equal(["First renamed", "Second"], sources.Select(s => s.Name));
        Assert.Equal(a.LastRefreshed, sources[0].LastRefreshed);
        Assert.Equal("p", sources[0].Password);
    }

    [Fact]
    public void Search_spans_sources_with_prefixes_and_ignores_accents()
    {
        _store.SaveSource(Source("a", "Provider A"));
        _store.SaveSource(Source("b", "Provider B"));
        _store.ReplaceItems("a", [Item("a", "BBC News HD"), Item("a", "Sky Sports")]);
        _store.ReplaceItems("b", [Item("b", "BBC News", sort: 0), Item("b", "Télé Matin", MediaKind.Movie, sort: 1)]);

        var hits = _store.Search("bbc new");
        Assert.Equal(2, hits.Count);
        Assert.Equal(["Provider A", "Provider B"], hits.Select(h => h.SourceName).Order());
        Assert.Equal("#FF0000", hits[0].SourceColor);

        Assert.Equal("Télé Matin", Assert.Single(_store.Search("tele")).Item.Title);
        Assert.Empty(_store.Search("tele", MediaKind.Live));
    }

    [Fact]
    public void Search_treats_fts_syntax_as_plain_text()
    {
        _store.SaveSource(Source("a", "A"));
        _store.ReplaceItems("a", [Item("a", "CNN International")]);

        Assert.Single(_store.Search("\"cnn\" (inter* -"));
        Assert.Empty(_store.Search("  -- ** "));
    }

    [Fact]
    public void Replace_items_removes_old_rows_from_search()
    {
        _store.SaveSource(Source("a", "A"));
        _store.ReplaceItems("a", [Item("a", "Old Channel")]);
        _store.ReplaceItems("a", [Item("a", "New Channel")]);

        Assert.Empty(_store.Search("old"));
        Assert.Single(_store.Search("new"));
    }

    [Fact]
    public void Delete_source_removes_its_items()
    {
        _store.SaveSource(Source("a", "A"));
        _store.ReplaceItems("a", [Item("a", "Gone")]);

        _store.DeleteSource("a");

        Assert.Empty(_store.GetSources());
        Assert.Empty(_store.Search("gone"));
        Assert.Empty(_store.GetItems(MediaKind.Live));
    }

    [Fact]
    public void Browse_returns_groups_in_provider_order_and_filters()
    {
        _store.SaveSource(Source("a", "A"));
        _store.ReplaceItems("a",
        [
            Item("a", "Z News", group: "News", sort: 0),
            Item("a", "Football", group: "Sports", sort: 1),
            Item("a", "A News", group: "News", sort: 2),
            Item("a", "Film", MediaKind.Movie, group: "Action", sort: 3),
        ]);

        Assert.Equal(["News", "Sports"], _store.GetGroups(MediaKind.Live));
        Assert.Equal(["Z News", "A News"], _store.GetItems(MediaKind.Live, group: "News").Select(e => e.Item.Title));
        Assert.Equal(["Film"], _store.GetItems(MediaKind.Movie, "a").Select(e => e.Item.Title));
    }

    [Theory]
    [InlineData("bbc news", "\"bbc\"* \"news\"*")]
    [InlineData("  \"x\" ", "\"x\"*")]
    [InlineData("!!", null)]
    public void Builds_safe_match_expression(string input, string? expected) =>
        Assert.Equal(expected, LibraryStore.BuildMatchExpression(input));
}
