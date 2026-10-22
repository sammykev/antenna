using System.Net;
using Antenna.Core.Library;
using Antenna.Core.Playback;

namespace Antenna.Core.Tests;

public class StreamProbeTests
{
    /// <summary>Answers each URL from a table; unknown URLs are 404. Records the headers it was sent.</summary>
    private sealed class FakeServer(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (routes.TryGetValue(request.RequestUri!.ToString(), out var respond))
            {
                var response = respond();
                if (response.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Bytes(int count) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Enumerable.Repeat((byte)0x47, count).ToArray()) };

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static async Task<ProbeResult> Check(Dictionary<string, Func<HttpResponseMessage>> routes, string url, params string[] options) =>
        await StreamProbe.CheckAsync(new HttpClient(new FakeServer(routes)), url, options, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task A_stream_sending_video_is_on_air_and_a_dark_one_is_not()
    {
        var routes = new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["http://h/live/1.ts"] = () => Bytes(64 * 1024),
            ["http://h/live/2.ts"] = () => Bytes(0),
            ["http://h/live/3.ts"] = () => new HttpResponseMessage(HttpStatusCode.Forbidden),
            ["http://h/live/4.ts"] = () => new HttpResponseMessage(HttpStatusCode.RequestTimeout),
        };
        Assert.Equal(ProbeResult.OnAir, await Check(routes, "http://h/live/1.ts"));
        Assert.Equal(ProbeResult.OffAir, await Check(routes, "http://h/live/2.ts"));
        Assert.Equal(ProbeResult.OffAir, await Check(routes, "http://h/live/3.ts"));
        Assert.Equal(ProbeResult.OffAir, await Check(routes, "http://h/live/4.ts")); // Never answers: times out.
        Assert.Equal(ProbeResult.Unknown, await Check(routes, "rtmp://h/live/5"));
    }

    [Fact]
    public async Task Hls_follows_the_first_variant_and_needs_segments()
    {
        var routes = new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["http://h/a/master.m3u8"] = () => Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nlow/index.m3u8\n"),
            ["http://h/a/low/index.m3u8"] = () => Text("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg1.ts\n"),
            ["http://h/b/master.m3u8"] = () => Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nlow/index.m3u8\n"),
            ["http://h/b/low/index.m3u8"] = () => Text("#EXTM3U\n#EXT-X-TARGETDURATION:6\n"),
        };
        Assert.Equal(ProbeResult.OnAir, await Check(routes, "http://h/a/master.m3u8"));
        Assert.Equal(ProbeResult.OffAir, await Check(routes, "http://h/b/master.m3u8"));
    }

    [Fact]
    public async Task Sends_the_headers_the_stream_needs()
    {
        var server = new FakeServer(new() { ["http://h/live/1.ts"] = () => Bytes(64 * 1024) });
        await StreamProbe.CheckAsync(new HttpClient(server), "http://h/live/1.ts", [":http-user-agent=MyBox/1.0", ":http-referrer=http://site/"], TimeSpan.FromSeconds(1));
        var sent = Assert.Single(server.Requests);
        Assert.Equal("MyBox/1.0", string.Join(' ', sent.Headers.UserAgent));
        Assert.Equal("http://site/", sent.Headers.Referrer?.ToString());
    }

    [Fact]
    public async Task Skips_dark_channels_for_the_first_one_broadcasting()
    {
        var http = new HttpClient(new FakeServer(new() { ["http://h/live/2.ts"] = () => Bytes(64 * 1024) }));
        LibraryEntry Channel(string id) => new(new MediaItem { SourceId = "s", ExternalId = id, Kind = MediaKind.Live, Title = $"Ch {id}", StreamUrl = $"http://h/live/{id}.ts" }, "P", "#fff", "");

        var (onAir, dark) = await StreamProbe.FirstOnAirAsync(http, [Channel("1"), Channel("2"), Channel("3")], TimeSpan.FromSeconds(1));
        Assert.Equal("2", onAir?.Item.ExternalId);
        Assert.Equal("1", Assert.Single(dark).Item.ExternalId);

        var (none, allDark) = await StreamProbe.FirstOnAirAsync(http, [Channel("1"), Channel("3")], TimeSpan.FromSeconds(1));
        Assert.Null(none);
        Assert.Equal(2, allDark.Count);
    }
}
