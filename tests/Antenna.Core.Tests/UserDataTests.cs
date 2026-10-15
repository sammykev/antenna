using System.Net;
using System.Text.Json;
using Antenna.Core.Backup;
using Antenna.Core.Library;
using Antenna.Core.Playlists;
using Antenna.Core.Recordings;
using Antenna.Core.Security;
using Antenna.Core.Subtitles;

namespace Antenna.Core.Tests;

public sealed class UserDataTests : IDisposable
{
    /// <summary>Reversible stand-in for DPAPI that makes "encrypted" values easy to spot.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x" + plaintext));
        public string Unprotect(string ciphertext) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext))[1..];
    }

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"antenna-ud-{Guid.NewGuid():N}.db");
    private readonly LibraryStore _store;

    public UserDataTests() => _store = new LibraryStore(_path, new FakeProtector());

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(f);
    }

    private static PlaylistSource Xtream(string id = "x") => new()
    {
        Id = id, Name = "Xtream", Kind = SourceKind.Xtream, Location = "http://h", Username = "me", Password = "p@ss",
    };

    private static MediaItem Item(string source, string id, MediaKind kind = MediaKind.Live, string group = "G", string? url = null) => new()
    {
        SourceId = source, ExternalId = id, Kind = kind, Title = $"Title {id}", Group = group, StreamUrl = url ?? $"http://h/{id}",
    };

    private string RawScalar(string sql)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    [Fact]
    public void Passwords_are_encrypted_at_rest_and_urls_hold_no_credentials()
    {
        _store.SaveSource(Xtream());
        _store.ReplaceItems("x", [Item("x", "1", url: "http://h/live/me/p%40ss/1.ts")]);

        Assert.StartsWith("enc:", RawScalar("SELECT password FROM sources"));
        Assert.DoesNotContain("p%40ss", RawScalar("SELECT stream_url FROM items"));
        Assert.Equal("p@ss", _store.GetSources()[0].Password);
        Assert.Equal("http://h/live/me/p%40ss/1.ts", _store.GetItems(MediaKind.Live)[0].Item.StreamUrl);
    }

    [Fact]
    public void Legacy_plaintext_passwords_are_encrypted_on_open()
    {
        using (var plain = new LibraryStore(_path + ".legacy"))
            plain.SaveSource(Xtream());
        using var upgraded = new LibraryStore(_path + ".legacy", new FakeProtector());

        Assert.Equal("p@ss", upgraded.GetSources()[0].Password);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { "", "-wal", "-shm" })
            File.Delete(_path + ".legacy" + f);
    }

    [Fact]
    public void Player_options_round_trip()
    {
        _store.SaveSource(Xtream());
        _store.ReplaceItems("x", [Item("x", "1") with { PlayerOptions = ":http-user-agent=A\n:http-referrer=B" }]);

        Assert.Equal([":http-user-agent=A", ":http-referrer=B"], _store.GetItems(MediaKind.Live)[0].Item.Options);
    }

    [Fact]
    public void Favourites_survive_refresh_and_follow_items()
    {
        _store.SaveSource(Xtream());
        _store.ReplaceItems("x", [Item("x", "1"), Item("x", "2", MediaKind.Movie)]);
        _store.SetFavourite(Item("x", "2", MediaKind.Movie), true);
        _store.SetFavourite(Item("x", "1"), true);

        _store.ReplaceItems("x", [Item("x", "1"), Item("x", "2", MediaKind.Movie)]);

        Assert.Equal(["1", "2"], _store.GetFavourites().Select(f => f.Item.ExternalId));
        Assert.Equal(["2"], _store.GetFavourites(MediaKind.Movie).Select(f => f.Item.ExternalId));
        Assert.True(_store.IsFavourite(Item("x", "1")));

        _store.SetFavourite(Item("x", "1"), false);
        Assert.False(_store.IsFavourite(Item("x", "1")));
    }

    [Fact]
    public void History_tracks_resume_points_newest_first()
    {
        _store.SaveSource(Xtream());
        var t0 = DateTimeOffset.Now.AddHours(-1);
        _store.RecordWatch(new WatchRecord { SourceId = "x", ExternalId = "m", Kind = MediaKind.Movie, Title = "Film", StreamUrl = "http://h/movie/me/p%40ss/m.mp4", PositionMs = 600_000, LengthMs = 6_000_000, WatchedAt = t0 });
        _store.RecordWatch(new WatchRecord { SourceId = "x", ExternalId = "c", Kind = MediaKind.Live, Title = "Chan", StreamUrl = "http://h/c", WatchedAt = t0.AddMinutes(5) });

        var history = _store.GetHistory();
        Assert.Equal(["c", "m"], history.Select(h => h.ExternalId));
        Assert.True(history[1].IsResumable);
        Assert.False(history[0].IsResumable);
        Assert.Equal("http://h/movie/me/p%40ss/m.mp4", _store.GetWatch("x", "m")!.StreamUrl);
        Assert.DoesNotContain("p%40ss", RawScalar("SELECT stream_url FROM history WHERE external_id = 'm'"));

        _store.RemoveWatch("x", "m");
        Assert.Null(_store.GetWatch("x", "m"));
    }

    [Theory]
    [InlineData(30_000, 100_000, false)]
    [InlineData(96_000, 100_000, false)]
    [InlineData(600_000, 1_000_000, true)]
    public void Resumable_needs_a_real_start_and_not_the_credits(long pos, long len, bool expected) =>
        Assert.Equal(expected, new WatchRecord { SourceId = "s", ExternalId = "e", Kind = MediaKind.Movie, Title = "t", StreamUrl = "u", PositionMs = pos, LengthMs = len }.IsResumable);

    [Fact]
    public void Settings_round_trip_and_delete()
    {
        _store.SetSetting("theme", "dark");
        _store.SetSetting("theme", "light");
        Assert.Equal("light", _store.GetSetting("theme"));
        _store.SetSetting("theme", null);
        Assert.Null(_store.GetSetting("theme"));
    }

    [Fact]
    public void Hidden_groups_disappear_and_locked_ones_follow_the_parental_lock()
    {
        _store.SaveSource(Xtream());
        _store.ReplaceItems("x", [Item("x", "1", group: "News"), Item("x", "2", group: "Adult") with { SortIndex = 1 }, Item("x", "3", group: "Shopping") with { SortIndex = 2 }]);
        _store.SetFavourite(Item("x", "2"), true);
        _store.SetGroupFlag(MediaKind.Live, "Shopping", GroupFlag.Hidden, true);
        _store.SetGroupFlag(MediaKind.Live, "Adult", GroupFlag.Locked, true);

        Assert.Equal(["News", "Adult"], _store.GetGroups(MediaKind.Live));
        Assert.Equal(["1", "2"], _store.GetItems(MediaKind.Live).Select(e => e.Item.ExternalId));

        _store.ParentalLockActive = true;
        Assert.Equal(["1"], _store.GetItems(MediaKind.Live).Select(e => e.Item.ExternalId));
        Assert.Empty(_store.Search("title 2"));
        Assert.Empty(_store.GetFavourites());
        Assert.Null(_store.GetItem("x", "2", MediaKind.Live));

        _store.SetGroupFlag(MediaKind.Live, "Shopping", GroupFlag.Hidden, false);
        Assert.Equal([(MediaKind.Live, "Adult")], _store.GetFlaggedGroups(GroupFlag.Locked));
        Assert.Contains("Shopping", _store.GetGroups(MediaKind.Live));
    }

    [Fact]
    public void Locked_history_is_hidden_while_locked()
    {
        _store.SaveSource(Xtream());
        _store.ReplaceItems("x", [Item("x", "s", MediaKind.Series, group: "Adult")]);
        _store.SetGroupFlag(MediaKind.Series, "Adult", GroupFlag.Locked, true);
        _store.RecordWatch(new WatchRecord { SourceId = "x", ExternalId = "ep:s", Kind = MediaKind.Series, Title = "Ep", StreamUrl = "u" });

        Assert.Single(_store.GetHistory());
        _store.ParentalLockActive = true;
        Assert.Empty(_store.GetHistory());
    }

    [Fact]
    public void Sources_can_be_reordered()
    {
        foreach (var id in new[] { "a", "b", "c" })
            _store.SaveSource(Xtream(id) with { Name = id });

        _store.MoveSource("c", -1);
        Assert.Equal(["a", "c", "b"], _store.GetSources().Select(s => s.Id));
        _store.MoveSource("a", -1);
        Assert.Equal(["a", "c", "b"], _store.GetSources().Select(s => s.Id));
    }

    [Fact]
    public void Recordings_round_trip()
    {
        var rec = new Recording { Title = "News", Url = "http://h/1.ts", IsDownload = false, Start = DateTimeOffset.Now, Stop = DateTimeOffset.Now.AddHours(1), Path = "C:\\r\\news.ts" };
        _store.SaveRecording(rec);
        _store.SaveRecording(rec with { Status = RecordingStatus.Completed, BytesWritten = 42 });

        var stored = Assert.Single(_store.GetRecordings());
        Assert.Equal((RecordingStatus.Completed, 42L), (stored.Status, stored.BytesWritten));
        _store.DeleteRecording(rec.Id);
        Assert.Empty(_store.GetRecordings());
    }

    [Fact]
    public void Backup_round_trips_into_a_fresh_store()
    {
        _store.SaveSource(Xtream());
        _store.ReplaceItems("x", [Item("x", "1")]);
        _store.SetFavourite(Item("x", "1"), true);
        _store.SetGroupFlag(MediaKind.Live, "Adult", GroupFlag.Locked, true);
        _store.SetSetting("theme", "dark");
        _store.SetSetting("parental.pin", "secret-hash");

        var withoutPasswords = BackupService.Export(_store, includePasswords: false);
        Assert.DoesNotContain("p@ss", withoutPasswords);
        Assert.DoesNotContain("secret-hash", withoutPasswords);

        var json = BackupService.Export(_store, includePasswords: true);
        var otherPath = _path + ".restore";
        using (var other = new LibraryStore(otherPath, new FakeProtector()))
        {
            var restored = BackupService.Import(other, json);
            Assert.Equal("p@ss", Assert.Single(restored).Password);
            other.ReplaceItems("x", [Item("x", "1")]);
            Assert.Single(other.GetFavourites());
            Assert.Equal("dark", other.GetSetting("theme"));
            Assert.Null(other.GetSetting("parental.pin"));
            Assert.Single(other.GetFlaggedGroups(GroupFlag.Locked));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { "", "-wal", "-shm" })
            File.Delete(otherPath + f);
    }

    [Fact]
    public void Backup_rejects_newer_versions() =>
        Assert.Throws<InvalidDataException>(() => BackupService.Import(_store, """{"version": 99}"""));

    [Fact]
    public void Pin_hashes_verify_and_reject()
    {
        var hash = PinHasher.Hash("1234");
        Assert.DoesNotContain("1234", hash);
        Assert.True(PinHasher.Verify("1234", hash));
        Assert.False(PinHasher.Verify("4321", hash));
        Assert.False(PinHasher.Verify("1234", null));
        Assert.False(PinHasher.Verify("1234", "garbage"));
        Assert.True(PinHasher.IsValidPin("0000"));
        Assert.False(PinHasher.IsValidPin("12a4"));
        Assert.False(PinHasher.IsValidPin("123"));
    }

    [Fact]
    public void M3u_player_options_from_vlcopt_pipe_and_attributes()
    {
        const string m3u = """
            #EXTM3U
            #EXTINF:-1 user-agent="AttrAgent",One
            #EXTVLCOPT:http-referrer=https://ref.example/
            http://h/1.m3u8
            #EXTINF:-1,Two
            http://h/2.m3u8|User-Agent=Pipe%20Agent&Referer=https://r2/&X-Other=1
            #EXTINF:-1,Three
            http://h/3.m3u8
            """;

        var items = M3uParser.Parse(m3u, "s").Items;

        Assert.Equal([":http-referrer=https://ref.example/", ":http-user-agent=AttrAgent"], items[0].Options);
        Assert.Equal("http://h/2.m3u8", items[1].StreamUrl);
        Assert.Equal([":http-user-agent=Pipe Agent", ":http-referrer=https://r2/"], items[1].Options);
        Assert.Empty(items[2].Options);
        Assert.Null(items[2].PlayerOptions);
    }

    [Fact]
    public void Xtream_details_parse_loose_fields()
    {
        using var doc = JsonDocument.Parse("""
            {"movie_image":"http://p.jpg","backdrop_path":["","http://b.jpg"],"plot":"Story","cast":"A, B","rating":"7.5","releasedate":"2020-01-01","duration":"01:45:00"}
            """);
        var d = XtreamClient.ReadDetails(doc.RootElement, "movie_image");

        Assert.Equal(("http://p.jpg", "http://b.jpg", "Story", "7.5"), (d.PosterUrl, d.BackdropUrl, d.Plot, d.Rating));
        Assert.Equal("2020-01-01", d.Released);
    }

    [Fact]
    public async Task Xtream_series_info_returns_details_and_episodes()
    {
        var handler = new StubHandler(_ => """{"info":{"cover":"http://c.jpg","plot":"P","rating":"0"},"episodes":{"1":[{"id":"5","episode_num":1,"season":1,"title":"E1","container_extension":"mkv"}]}}""");
        var client = new XtreamClient(new HttpClient(handler), "http://h", "u", "p");

        var (details, episodes) = await client.GetSeriesInfoAsync("9");

        Assert.Equal("http://c.jpg", details.PosterUrl);
        Assert.Null(details.Rating);
        Assert.Equal("5", Assert.Single(episodes).Id);
    }

    [Fact]
    public async Task OpenSubtitles_search_and_download()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/subtitles" => """{"data":[{"attributes":{"release":"Low","language":"en","download_count":5,"files":[{"file_id":1}]}},{"attributes":{"release":"High","language":"en","download_count":50,"hearing_impaired":true,"files":[{"file_id":2,"file_name":"x.srt"}]}}]}""",
            "/api/v1/download" => """{"link":"https://dl.example/file","file_name":"film.srt"}""",
            _ => "1\n00:00:01,000 --> 00:00:02,000\nHello\n",
        });
        var client = new OpenSubtitlesClient(new HttpClient(handler), "key");

        var results = await client.SearchAsync("The Film", "EN", season: 1, episode: 2);
        Assert.Equal(["High", "Low"], results.Select(r => r.Release));
        Assert.True(results[0].HearingImpaired);
        var search = handler.Requests[0];
        Assert.Equal("?episode_number=2&languages=en&query=the%20film&season_number=1", search.RequestUri!.Query);
        Assert.Equal("key", search.Headers.GetValues("Api-Key").Single());

        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = await client.DownloadAsync(2, dir);
        Assert.Equal("film.srt", Path.GetFileName(path));
        Assert.Contains("Hello", await File.ReadAllTextAsync(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task OpenSubtitles_errors_are_friendly()
    {
        var handler = new StubHandler(_ => """{"message":"Invalid API key"}""", HttpStatusCode.Unauthorized);
        var client = new OpenSubtitlesClient(new HttpClient(handler), "bad");

        var ex = await Assert.ThrowsAsync<OpenSubtitlesException>(() => client.SearchAsync("x", "en"));
        Assert.Contains("Invalid API key", ex.Message);
    }

    [Fact]
    public void Planner_finds_due_missed_and_finished_recordings()
    {
        var now = DateTimeOffset.Now;
        Recording R(string id, int startMin, int? stopMin, RecordingStatus status = RecordingStatus.Scheduled) => new()
        {
            Id = id, Title = id, Url = "u", IsDownload = false, Path = "p", Status = status,
            Start = now.AddMinutes(startMin), Stop = stopMin is { } s ? now.AddMinutes(s) : null,
        };
        Recording[] all = [R("due", -1, 30), R("future", 10, 40), R("missed", -60, -5), R("open", -1, null), R("running", -30, -1, RecordingStatus.Recording)];

        Assert.Equal(["due", "open"], RecordingPlanner.DueToStart(all, now).Select(r => r.Id));
        Assert.Equal(["missed"], RecordingPlanner.Missed(all, now).Select(r => r.Id));
        Assert.True(RecordingPlanner.ShouldStop(all[4], now));
        Assert.False(RecordingPlanner.ShouldStop(all[3], now));

        var p = new Programme { ChannelId = "c", Title = "T", Start = now, Stop = now.AddHours(1) };
        Assert.Equal((now.AddMinutes(-2), now.AddMinutes(65)), RecordingPlanner.Window(p));
        Assert.Equal("ab 2026-01-02 0304.ts", RecordingPlanner.FileName("a/b", new DateTimeOffset(2026, 1, 2, 3, 4, 0, DateTimeOffset.Now.Offset), "ts"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(respond(request)) });
        }
    }
}
