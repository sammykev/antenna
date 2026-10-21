using Antenna.Core.Library;
using Antenna.Core.Sync;

namespace Antenna.Core.Tests;

public sealed class SyncTests : IDisposable
{
    private sealed class MemoryTarget : ISyncTarget
    {
        public byte[]? Data { get; set; }
        public int Writes { get; private set; }

        public Task<byte[]?> ReadAsync(CancellationToken ct = default) => Task.FromResult(Data);

        public Task WriteAsync(byte[] data, CancellationToken ct = default)
        {
            Data = data;
            Writes++;
            return Task.CompletedTask;
        }
    }

    private const string Pass = "correct horse";
    private readonly List<string> _paths = [];
    private readonly List<LibraryStore> _stores = [];
    private readonly MemoryTarget _file = new();

    private LibraryStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"antenna-sync-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        var store = new LibraryStore(path);
        _stores.Add(store);
        return store;
    }

    public void Dispose()
    {
        foreach (var s in _stores)
            s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var p in _paths)
        {
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
                File.Delete(f);
        }
    }

    private static PlaylistSource Source(string id, string name = "Provider") => new()
    {
        Id = id, Name = name, Kind = SourceKind.Xtream, Location = "http://h:8080", Username = "me", Password = "secret",
    };

    private static MediaItem Item(string source, string id, MediaKind kind = MediaKind.Live) => new()
    {
        SourceId = source, ExternalId = id, Kind = kind, Title = $"Title {id}", StreamUrl = $"http://h:8080/live/me/secret/{id}.ts",
    };

    private Task<SyncOutcome> Sync(LibraryStore store, string device = "test") => SyncService.SyncAsync(store, _file, Pass, device);

    [Fact]
    public async Task Playlists_and_favourites_travel_to_a_new_device()
    {
        var pc = NewStore();
        pc.SaveSource(Source("a"));
        pc.SetFavourite(Item("a", "1"), true);

        await Sync(pc, "pc");
        var phone = NewStore();
        var outcome = await Sync(phone, "phone");

        var source = Assert.Single(phone.GetSources());
        Assert.Equal("secret", source.Password);
        Assert.Equal("a", Assert.Single(outcome.NewSources).Id);
        Assert.Contains(phone.GetFavouriteKeys(), f => f.ExternalId == "1");
    }

    [Fact]
    public async Task Deletions_carry_over_both_ways()
    {
        var pc = NewStore();
        var phone = NewStore();
        pc.SaveSource(Source("a"));
        pc.SaveSource(Source("b"));
        pc.SetFavourite(Item("a", "1"), true);
        await Sync(pc);
        await Sync(phone);

        phone.DeleteSource("b");
        phone.SetFavourite(Item("a", "1"), false);
        await Sync(phone);
        await Sync(pc);

        Assert.Equal(["a"], pc.GetSources().Select(s => s.Id));
        Assert.Empty(pc.GetFavouriteKeys());
    }

    [Fact]
    public async Task Edits_win_over_unchanged_copies()
    {
        var pc = NewStore();
        var phone = NewStore();
        pc.SaveSource(Source("a", "Old name"));
        await Sync(pc);
        await Sync(phone);

        phone.SaveSource(Source("a", "New name"));
        await Sync(phone);
        await Sync(pc);

        Assert.Equal("New name", Assert.Single(pc.GetSources()).Name);
    }

    [Fact]
    public async Task The_latest_resume_point_wins()
    {
        var pc = NewStore();
        var phone = NewStore();
        pc.SaveSource(Source("a"));
        await Sync(pc);
        await Sync(phone);

        var start = DateTimeOffset.Now.AddHours(-1);
        pc.RecordWatch(new WatchRecord { SourceId = "a", ExternalId = "m1", Kind = MediaKind.Movie, Title = "Film", StreamUrl = "http://h:8080/movie/me/secret/m1.mp4", PositionMs = 600_000, LengthMs = 6_000_000, WatchedAt = start });
        phone.RecordWatch(new WatchRecord { SourceId = "a", ExternalId = "m1", Kind = MediaKind.Movie, Title = "Film", StreamUrl = "http://h:8080/movie/me/secret/m1.mp4", PositionMs = 1_800_000, LengthMs = 6_000_000, WatchedAt = start.AddMinutes(30) });
        await Sync(pc);
        await Sync(phone);
        await Sync(pc);

        Assert.Equal(1_800_000, pc.GetWatch("a", "m1")!.PositionMs);
        Assert.Equal(1_800_000, phone.GetWatch("a", "m1")!.PositionMs);
    }

    [Fact]
    public async Task Nothing_is_rewritten_when_nothing_changed()
    {
        var pc = NewStore();
        pc.SaveSource(Source("a"));
        await Sync(pc);
        await Sync(pc);
        Assert.Equal(1, _file.Writes);
    }

    [Fact]
    public async Task The_file_is_encrypted_and_needs_the_passphrase()
    {
        var pc = NewStore();
        pc.SaveSource(Source("a"));
        await Sync(pc);

        Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(_file.Data!));
        await Assert.ThrowsAsync<SyncPassphraseException>(() => SyncService.SyncAsync(NewStore(), _file, "wrong", "x"));
    }
}
