using System.Text.Json;
using Antenna.Core.Epg;
using Antenna.Core.Playlists;

namespace Antenna.Core.Tests;

public class CatchupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 22, 0, 0, TimeSpan.Zero);

    // 20:00–21:00 UTC; epoch 1792008000.
    private static readonly Programme Show = new()
    {
        ChannelId = "c",
        Start = new DateTimeOffset(2026, 10, 14, 20, 0, 0, TimeSpan.Zero),
        Stop = new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero),
        Title = "Show",
    };

    private static MediaItem Channel(string url, string? mode = null, string? source = null, int days = 7) => new()
    {
        SourceId = "s", ExternalId = url, Kind = MediaKind.Live, Title = "C", StreamUrl = url,
        CatchupDays = days, CatchupMode = mode, CatchupSource = source,
    };

    private static readonly long Start = Show.Start.ToUnixTimeSeconds();
    private static readonly long End = Show.Stop.ToUnixTimeSeconds();
    private static readonly long NowTs = Now.ToUnixTimeSeconds();

    [Fact]
    public void Availability_respects_archive_window_and_start()
    {
        Assert.True(Catchup.IsAvailable(Channel("u"), Show, Now));
        Assert.False(Catchup.IsAvailable(Channel("u", days: 0), Show, Now));
        Assert.False(Catchup.IsAvailable(Channel("u"), Show, Show.Start.AddMinutes(-1)));
        Assert.False(Catchup.IsAvailable(Channel("u", days: 1), Show, Now.AddDays(2)));
    }

    [Fact]
    public void Default_mode_fills_template()
    {
        var url = Catchup.BuildM3uUrl(Channel("http://h/live.m3u8", "default", "http://h/arch/{Y}/{m}/{d}/{H}-{M}.m3u8?d={duration}&s=${start}&e={utcend}"), Show, Now);
        Assert.Equal($"http://h/arch/2026/10/14/20-00.m3u8?d=3600&s={Start}&e={End}", url);
    }

    [Fact]
    public void Append_mode_appends_template_and_supports_formats()
    {
        var url = Catchup.BuildM3uUrl(Channel("http://h/c.m3u8", "append", "?start={utc:YmdHMS}&now={lutc}&off={offset}"), Show, Now);
        Assert.Equal($"http://h/c.m3u8?start=20261014200000&now={NowTs}&off=7200", url);
    }

    [Theory]
    [InlineData("http://h/c.m3u8", "?")]
    [InlineData("http://h/c.m3u8?token=1", "&")]
    public void Shift_mode_adds_utc_and_lutc(string stream, string sep) =>
        Assert.Equal($"{stream}{sep}utc={Start}&lutc={NowTs}", Catchup.BuildM3uUrl(Channel(stream, "shift"), Show, Now));

    [Fact]
    public void Flussonic_rewrites_hls_and_ts_paths()
    {
        Assert.Equal($"http://h/ch1/index-{Start}-3600.m3u8?t=1", Catchup.BuildM3uUrl(Channel("http://h/ch1/index.m3u8?t=1", "flussonic"), Show, Now));
        Assert.Equal($"http://h/ch1/timeshift_abs/{Start}.ts", Catchup.BuildM3uUrl(Channel("http://h/ch1/mpegts", "fs"), Show, Now));
    }

    [Fact]
    public void Xc_mode_builds_xtream_timeshift_url_and_is_inferred()
    {
        var item = Channel("http://h:8080/live/user/pass/101.ts");
        Assert.Equal("xc", Catchup.InferMode(item));
        Assert.Equal("http://h:8080/timeshift/user/pass/60/2026-10-14:20-00/101.ts", Catchup.BuildM3uUrl(item, Show, Now));
    }

    [Fact]
    public void Infers_default_or_append_from_template_and_shift_otherwise()
    {
        Assert.Equal("default", Catchup.InferMode(Channel("http://h/a", source: "http://h/arch?s={utc}")));
        Assert.Equal("append", Catchup.InferMode(Channel("http://h/a", source: "?s={utc}")));
        Assert.Equal("shift", Catchup.InferMode(Channel("http://h/a")));
    }

    [Fact]
    public void Unknown_mode_or_missing_template_yields_null()
    {
        Assert.Null(Catchup.BuildM3uUrl(Channel("http://h/a", "default"), Show, Now));
        Assert.Null(Catchup.BuildM3uUrl(Channel("http://h/a", "vod"), Show, Now));
    }

    [Fact]
    public void M3u_parser_reads_catchup_attributes_with_header_defaults()
    {
        const string m3u = """
            #EXTM3U catchup="append" catchup-days="3" catchup-source="?utc={utc}"
            #EXTINF:-1 tvg-id="a",Inherits
            http://h/a.m3u8
            #EXTINF:-1 catchup="flussonic" catchup-days="5",Overrides
            http://h/b/index.m3u8
            """;

        var items = M3uParser.Parse(m3u, "s").Items;

        Assert.Equal(("append", "?utc={utc}", 3), (items[0].CatchupMode, items[0].CatchupSource, items[0].CatchupDays));
        Assert.Equal(("flussonic", 5), (items[1].CatchupMode, items[1].CatchupDays));
    }

    [Theory]
    [InlineData("""{"timestamp_now":1792008000,"time_now":"2026-10-14 21:00:00"}""", 60)]
    [InlineData("""{"timestamp_now":"1792008000","time_now":"2026-10-14 15:00:07"}""", -300)]
    [InlineData("""{"timezone":"UTC"}""", 0)]
    [InlineData("""{"timezone":"Not/AZone"}""", 0)]
    public void Detects_xtream_server_offset(string json, int minutes)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(TimeSpan.FromMinutes(minutes), XtreamClient.ServerOffset(doc.RootElement));
    }

    [Fact]
    public void Xtream_catchup_url_uses_server_local_time()
    {
        var client = new XtreamClient(new HttpClient(), "http://h", "u", "p");
        Assert.Equal("http://h/timeshift/u/p/60/2026-10-14:21-00/9.ts", client.CatchupUrl("9", Show.Start, Show.Duration, TimeSpan.FromHours(1)));
    }
}
