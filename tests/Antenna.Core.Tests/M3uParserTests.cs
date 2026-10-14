using Antenna.Core.Playlists;

namespace Antenna.Core.Tests;

public class M3uParserTests
{
    [Fact]
    public void Parses_attributes_title_group_and_header_epg()
    {
        const string m3u = """
            #EXTM3U url-tvg="http://guide.example/epg.xml.gz,http://backup/epg.xml" x-tvg-url="ignored"
            #EXTINF:-1 tvg-id="bbc1.uk" tvg-name="BBC One" tvg-logo="http://logo/bbc1.png" group-title="UK | News" catchup-days="7",BBC One HD
            http://host/live/u/p/101.ts
            """;

        var result = M3uParser.Parse(m3u, "src");

        Assert.Equal("http://guide.example/epg.xml.gz", result.EpgUrl);
        var item = Assert.Single(result.Items);
        Assert.Equal("BBC One HD", item.Title);
        Assert.Equal("UK | News", item.Group);
        Assert.Equal("bbc1.uk", item.EpgChannelId);
        Assert.Equal("http://logo/bbc1.png", item.LogoUrl);
        Assert.Equal(7, item.CatchupDays);
        Assert.Equal(MediaKind.Live, item.Kind);
        Assert.Equal("http://host/live/u/p/101.ts", item.StreamUrl);
        Assert.Equal("src", item.SourceId);
    }

    [Fact]
    public void Handles_commas_in_quoted_attributes_and_titles()
    {
        const string m3u = "#EXTM3U\r\n#EXTINF:-1 group-title=\"Movies, Action\",Fast, Furious\r\nhttp://host/movie/u/p/5.mkv\r\n";

        var item = Assert.Single(M3uParser.Parse(m3u, "s").Items);

        Assert.Equal("Movies, Action", item.Group);
        Assert.Equal("Fast, Furious", item.Title);
        Assert.Equal(MediaKind.Movie, item.Kind);
    }

    [Fact]
    public void Uses_extgrp_and_tvg_name_fallbacks_and_skips_vlc_options()
    {
        const string m3u = """
            #EXTM3U
            #EXTINF:-1 tvg-name="Channel Four",
            #EXTGRP:Entertainment
            #EXTVLCOPT:http-user-agent=Foo
            http://host/4.m3u8
            """;

        var item = Assert.Single(M3uParser.Parse(m3u, "s").Items);

        Assert.Equal("Channel Four", item.Title);
        Assert.Equal("Entertainment", item.Group);
    }

    [Fact]
    public void Accepts_bare_urls_and_missing_header_and_keeps_order()
    {
        const string m3u = """
            http://a/1.ts
            #EXTINF:0,Second
            http://a/2.ts
            """;

        var items = M3uParser.Parse(m3u, "s").Items;

        Assert.Equal(["http://a/1.ts", "Second"], items.Select(i => i.Title));
        Assert.Equal([0, 1], items.Select(i => i.SortIndex));
    }

    [Theory]
    [InlineData("http://h/movie/u/p/1.mp4", MediaKind.Movie)]
    [InlineData("http://h/series/u/p/1.mkv", MediaKind.Movie)]
    [InlineData("http://h/live/u/p/1.ts", MediaKind.Live)]
    [InlineData("rtsp://h/stream", MediaKind.Live)]
    public void Classifies_kind_from_url(string url, MediaKind expected) =>
        Assert.Equal(expected, M3uParser.ClassifyKind(url));
}
