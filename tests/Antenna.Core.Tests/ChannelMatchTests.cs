using Antenna.Core.Library;

namespace Antenna.Core.Tests;

public sealed class ChannelMatchTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"antenna-cm-{Guid.NewGuid():N}.db");
    private readonly LibraryStore _store;

    public ChannelMatchTests() => _store = new LibraryStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(f);
    }

    [Theory]
    [InlineData("SKYGO: SKY SPORT TENNIS HD", "sky sport tennis")]
    [InlineData("UK: SKY SPORTS TENNIS ᴴᴰ ◉", "sky sport tennis")]
    [InlineData("[DE] Sky Sport Bundesliga 1 FHD", "sky sport bundesliga 1")]
    [InlineData("Télé-Matin 1080p", "tele matin")]
    [InlineData("Bloomberg News", "bloomberg news")]
    [InlineData("Movies & Series", "movie serie")]
    public void Keys_ignore_tags_quality_and_plurals(string title, string key) =>
        Assert.Equal(key, ChannelMatch.Key(title));

    [Fact]
    public void Finds_the_same_channel_in_other_playlists()
    {
        MediaItem Ch(string source, string id, string title, string? epg = null) => new()
        {
            SourceId = source, ExternalId = id, Kind = MediaKind.Live, Title = title, Group = "Sports", EpgChannelId = epg, StreamUrl = $"http://{source}/{id}",
        };
        foreach (var id in new[] { "a", "b", "c" })
            _store.SaveSource(new PlaylistSource { Id = id, Name = id.ToUpperInvariant(), Kind = SourceKind.M3u, Location = id });
        _store.ReplaceItems("a", [Ch("a", "1", "SKYGO: SKY SPORT TENNIS HD"), Ch("a", "2", "SKYGO: SKY SPORT TENNIS 2")]);
        _store.ReplaceItems("b", [Ch("b", "1", "UK: SKY SPORTS TENNIS ᴴᴰ ◉"), Ch("b", "2", "UK: SKY SPORTS F1")]);
        _store.ReplaceItems("c", [Ch("c", "1", "Tennis Channel", epg: "skytennis.uk"), Ch("c", "2", "Sky Sports Tennis Plus")]);

        var tennis = Ch("a", "1", "SKYGO: SKY SPORT TENNIS HD", epg: "skytennis.uk");
        var found = _store.FindSameChannel(tennis);

        Assert.Equal(["b:1", "c:1"], found.Select(e => $"{e.Item.SourceId}:{e.Item.ExternalId}").Order());
    }
}
