using System.IO.Compression;
using System.Text;
using Antenna.Core.Epg;
using Antenna.Core.Library;

namespace Antenna.Core.Tests;

public sealed class EpgTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("antenna-epg-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 20, 30, 0, TimeSpan.Zero);

    private const string Guide = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE tv SYSTEM "xmltv.dtd">
        <tv generator-info-name="test">
          <channel id="bbc1.uk"><display-name>BBC One</display-name><display-name>BBC 1</display-name><icon src="x"/></channel>
          <channel id="itv.uk"><display-name lang="en">ITV</display-name></channel>
          <programme start="20261014190000 +0000" stop="20261014200000 +0000" channel="bbc1.uk"><title>Old News</title></programme>
          <programme start="20261014200000 +0000" stop="20261014210000 +0000" channel="bbc1.uk">
            <title lang="en">Evening News</title><title lang="cy">Newyddion</title>
            <sub-title>Part 1</sub-title>
            <desc>Headlines</desc>
          </programme>
          <programme start="20261014213000 +0100" stop="20261014223000 +0100" channel="bbc1.uk"><title>Drama</title></programme>
          <programme start="20261014200000 +0000" stop="20261014210000 +0000" channel="other"><title>Unmatched</title></programme>
          <programme start="20261014200000" stop="20261014203000" channel="itv.uk"><title>Quiz</title></programme>
          <programme start="20261020200000 +0000" stop="20261020210000 +0000" channel="itv.uk"><title>Too Far Ahead</title></programme>
        </tv>
        """;

    private static async Task<(List<Programme> Programmes, IReadOnlyList<XmltvChannel> Channels)> Parse(string xml, Func<string, bool>? filter = null)
    {
        var programmes = new List<Programme>();
        IReadOnlyList<XmltvChannel> channels = [];
        await XmltvParser.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)),
            c => { channels = c.ToList(); return filter ?? (_ => true); },
            programmes.Add,
            Now.AddHours(-1),
            Now.AddDays(1));
        return (programmes, channels);
    }

    [Fact]
    public async Task Parses_channels_and_consecutive_programmes_within_window()
    {
        var (programmes, channels) = await Parse(Guide);

        Assert.Equal(["bbc1.uk", "itv.uk"], channels.Select(c => c.Id));
        Assert.Equal(["BBC One", "BBC 1"], channels[0].DisplayNames);
        // "Old News" ends exactly at the window start, so it overlaps; the far-future one is dropped.
        Assert.Equal(["Old News", "Evening News", "Drama", "Unmatched", "Quiz"], programmes.Select(p => p.Title));

        var news = programmes[1];
        Assert.Equal("Headlines", news.Description);
        Assert.Equal(TimeSpan.FromHours(1), news.Duration);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 20, 30, 0, TimeSpan.Zero), programmes[2].Start.ToUniversalTime());
    }

    [Fact]
    public async Task Channel_filter_skips_programmes_without_losing_neighbours()
    {
        var (programmes, _) = await Parse(Guide, id => id != "other");

        Assert.DoesNotContain(programmes, p => p.Title == "Unmatched");
        Assert.Contains(programmes, p => p.Title == "Quiz");
    }

    [Theory]
    [InlineData("20261014200000 +0130", 2026, 10, 14, 18, 30)]
    [InlineData("20261014200000 -0500", 2026, 10, 15, 1, 0)]
    [InlineData("202610142000", 2026, 10, 14, 20, 0)]
    public void Parses_xmltv_times(string value, int y, int mo, int d, int h, int mi) =>
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero), XmltvParser.ParseTime(value)!.Value.ToUniversalTime());

    [Theory]
    [InlineData("")]
    [InlineData("tomorrow")]
    [InlineData("2026")]
    public void Rejects_bad_times(string value) => Assert.Null(XmltvParser.ParseTime(value));

    [Fact]
    public async Task Opens_gzipped_and_plain_files()
    {
        var plain = Path.Combine(_dir, "guide.xml");
        var gz = Path.Combine(_dir, "guide.xml.gz");
        await File.WriteAllTextAsync(plain, Guide);
        await using (var f = File.Create(gz))
        await using (var z = new GZipStream(f, CompressionLevel.Fastest))
            await z.WriteAsync(Encoding.UTF8.GetBytes(Guide));

        foreach (var path in new[] { plain, gz })
        {
            await using var s = await XmltvParser.OpenAsync(new HttpClient(), path);
            using var reader = new StreamReader(s);
            Assert.Contains("<tv", await reader.ReadToEndAsync());
        }
    }

    [Theory]
    [InlineData("UK: BBC One HD", "bbcone")]
    [InlineData("[FR] Télé-Matin FHD", "telematin")]
    [InlineData("|US| CNN 4K", "cnn")]
    [InlineData("Sky Sports Main Event", "skysportsmainevent")]
    public void Normalizes_channel_names(string input, string expected) =>
        Assert.Equal(expected, EpgMatcher.Normalize(input));

    [Fact]
    public void Matches_by_id_then_by_name()
    {
        MediaItem Live(string title, string? epg) => new() { SourceId = "s", ExternalId = title, Kind = MediaKind.Live, Title = title, EpgChannelId = epg };
        var channels = new[] { new XmltvChannel("bbc1.uk", ["BBC One"]), new XmltvChannel("itv.uk", ["ITV"]) };

        var map = EpgMatcher.Match([Live("x", "BBC1.UK"), Live("UK: ITV HD", "wrong.id"), Live("Nope", null)], channels);

        Assert.Equal("bbc1.uk", map["x"]);
        Assert.Equal("itv.uk", map["UK: ITV HD"]);
        Assert.False(map.ContainsKey("Nope"));
    }

    [Fact]
    public async Task Importer_stores_matched_programmes_and_epg_ids()
    {
        var guidePath = Path.Combine(_dir, "guide.xml");
        await File.WriteAllTextAsync(guidePath, Guide.Replace("20261014", DateTime.UtcNow.ToString("yyyyMMdd")));
        using var store = new LibraryStore(Path.Combine(_dir, "lib.db"));
        var source = new PlaylistSource { Id = "s", Name = "S", Kind = SourceKind.M3u, Location = "x", EpgUrl = guidePath };
        store.SaveSource(source);
        MediaItem[] items =
        [
            new() { SourceId = "s", ExternalId = "1", Kind = MediaKind.Live, Title = "UK: BBC One HD", StreamUrl = "http://a/1" },
            new() { SourceId = "s", ExternalId = "2", Kind = MediaKind.Live, Title = "Itv", EpgChannelId = "itv.uk", StreamUrl = "http://a/2" },
        ];
        store.ReplaceItems("s", items);

        var count = await new EpgImporter(new HttpClient(), store).ImportAsync(source, items);

        Assert.True(count >= 3);
        var bbc = store.GetItems(MediaKind.Live).Single(e => e.Item.ExternalId == "1").Item;
        Assert.Equal("bbc1.uk", bbc.EpgChannelId);
        var today = DateTimeOffset.UtcNow.Date;
        var progs = store.GetProgrammes("s", "bbc1.uk", today, today.AddDays(1));
        Assert.Contains(progs, p => p.Title == "Evening News");
        Assert.DoesNotContain(progs, p => p.Title == "Unmatched");

        var nowPlaying = store.GetNowPlaying(new DateTimeOffset(today.AddHours(20).AddMinutes(10), TimeSpan.Zero));
        Assert.Equal("Evening News", nowPlaying[("s", "bbc1.uk")].Title);
    }

    [Fact]
    public void Uncommitted_guide_import_keeps_previous_guide()
    {
        using var store = new LibraryStore(Path.Combine(_dir, "lib.db"));
        store.SaveSource(new PlaylistSource { Id = "s", Name = "S", Kind = SourceKind.M3u, Location = "x" });
        var p = new Programme { ChannelId = "c", Start = Now, Stop = Now.AddHours(1), Title = "Kept" };
        using (var w = store.BeginGuideImport("s"))
        {
            w.Add(p);
            w.Commit();
        }
        using (var w = store.BeginGuideImport("s"))
            w.Add(p with { Title = "Discarded" });

        Assert.Equal(["Kept"], store.GetProgrammes("s", "c", Now, Now.AddHours(1)).Select(x => x.Title));
    }
}
