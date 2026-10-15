using System.Net;
using Antenna.Core.Playback;

namespace Antenna.Core.Tests;

public sealed class TimeshiftTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"antenna-ts-{Guid.NewGuid():N}");
    private DateTimeOffset _now = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private TimeshiftBuffer Buffer(TimeSpan? keep = null, long fallback = 1 << 20, int chunk = 1000) =>
        new(_folder, keep ?? TimeSpan.FromMinutes(30), fallback, chunk, () => _now);

    private static byte[] Bytes(int count, int seed = 0) => Enumerable.Range(seed, count).Select(i => (byte)i).ToArray();

    [Fact]
    public void Reads_back_what_was_appended_across_chunks()
    {
        using var buffer = Buffer(chunk: 100);
        var data = Bytes(350);
        buffer.Append(data);

        var read = new List<byte>();
        var chunk = new byte[64];
        long offset = 0;
        while (offset < buffer.End)
        {
            var n = buffer.Read(offset, chunk, CancellationToken.None);
            read.AddRange(chunk.AsSpan(0, n).ToArray());
            offset += n;
        }

        Assert.Equal(data, read);
    }

    [Fact]
    public async Task A_reader_waits_for_data_that_has_not_arrived()
    {
        using var buffer = Buffer();
        buffer.Append(Bytes(10));
        var target = new byte[10];

        var reading = Task.Run(() => buffer.Read(10, target, CancellationToken.None));
        await Task.Delay(100);
        Assert.False(reading.IsCompleted);

        buffer.Append(Bytes(5, seed: 10));
        Assert.Equal(5, await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Bytes(5, seed: 10), target[..5]);
    }

    [Fact]
    public async Task Completing_or_cancelling_releases_waiting_readers()
    {
        using var buffer = Buffer();
        buffer.Append(Bytes(10));
        var target = new byte[10];

        using var cancel = new CancellationTokenSource();
        var cancelled = Task.Run(() => buffer.Read(10, target, cancel.Token));
        cancel.Cancel();
        Assert.Equal(-1, await cancelled.WaitAsync(TimeSpan.FromSeconds(5)));

        var ended = Task.Run(() => buffer.Read(10, target, CancellationToken.None));
        buffer.Complete();
        Assert.Equal(0, await ended.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Drops_the_oldest_chunks_beyond_the_size_limit()
    {
        using var buffer = Buffer(fallback: 2000, chunk: 1000);
        for (var i = 0; i < 6; i++)
            buffer.Append(Bytes(1000));

        Assert.Equal(6000, buffer.End);
        Assert.True(buffer.Start >= 3000, $"start was {buffer.Start}");
        Assert.Equal(-1, buffer.Read(0, new byte[10], CancellationToken.None)); // Fell behind: gone.
        Assert.Equal(6 - buffer.Start / 1000, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void Measures_bitrate_and_finds_offsets_behind_live_on_packet_boundaries()
    {
        using var buffer = Buffer(chunk: 1 << 20);
        // 10 seconds of a 188 kB/s stream.
        for (var second = 0; second <= 10; second++)
        {
            buffer.Append(new byte[188_000]);
            _now = _now.AddSeconds(1);
        }

        Assert.InRange(buffer.BytesPerSecond, 170_000, 210_000);
        var fiveBack = buffer.OffsetBehindLive(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fiveBack % TimeshiftBuffer.PacketSize);
        Assert.InRange(buffer.BehindLive(fiveBack).TotalSeconds, 4.5, 5.5);
        Assert.Equal(0, buffer.OffsetBehindLive(TimeSpan.FromHours(1))); // Clamped to the oldest held.
    }

    [Fact]
    public void Keeps_roughly_the_requested_minutes_once_the_bitrate_is_known()
    {
        using var buffer = Buffer(keep: TimeSpan.FromSeconds(20), fallback: long.MaxValue, chunk: 10_000);
        for (var second = 0; second < 60; second++)
        {
            buffer.Append(new byte[10_000]);
            _now = _now.AddSeconds(1);
        }

        var held = TimeSpan.FromSeconds((buffer.End - buffer.Start) / buffer.BytesPerSecond);
        Assert.InRange(held.TotalSeconds, 20, 25);
    }

    [Theory]
    [InlineData("http://host/live/u/p/123.ts", true)]
    [InlineData("http://host/live/u/p/123", true)]
    [InlineData("https://host/stream/index.m3u8", false)]
    [InlineData("https://host/manifest.mpd", false)]
    [InlineData("file:///C:/video.ts", false)]
    public void Buffers_plain_http_streams_but_not_playlists(string url, bool expected) =>
        Assert.Equal(expected, LiveCapture.CanBuffer(url));

    [Fact]
    public void Turns_player_options_into_http_headers() =>
        Assert.Equal(
            [("User-Agent", "VLC/3.0"), ("Referer", "http://site/")],
            LiveCapture.HeadersFrom([":http-user-agent=VLC/3.0", ":http-referrer=http://site/", ":network-caching=1000"]));

    [Fact]
    public async Task Captures_a_stream_and_reconnects_after_a_drop()
    {
        var handler = new ScriptedHandler(Bytes(500), Bytes(300, seed: 1));
        using var buffer = Buffer();
        var capture = new LiveCapture(new HttpClient(handler), "http://host/live/1.ts", [":http-user-agent=Test/1"], buffer);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = capture.RunAsync(cancel.Token);
        Assert.True(await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        while (buffer.End < 800 && !cancel.IsCancellationRequested)
            await Task.Delay(20);
        cancel.Cancel();
        await run;

        Assert.Equal(800, buffer.End);
        Assert.True(handler.Requests >= 2);
        Assert.Equal("Test/1", handler.UserAgent);
    }

    [Fact]
    public async Task Reports_hls_as_unsupported()
    {
        var handler = new ScriptedHandler(contentType: "application/vnd.apple.mpegurl", bodies: Bytes(10));
        using var buffer = Buffer();
        var capture = new LiveCapture(new HttpClient(handler), "http://host/live/1", [], buffer);

        await capture.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(await capture.Started.Task);
        Assert.Contains("HLS", capture.Error);
    }

    /// <summary>Serves each body once in turn (the connection then "drops"), then hangs.</summary>
    private sealed class ScriptedHandler(params byte[][] bodies) : HttpMessageHandler
    {
        private readonly string _contentType = "video/mp2t";

        public ScriptedHandler(string contentType, params byte[][] bodies) : this(bodies) => _contentType = contentType;

        public int Requests;
        public string? UserAgent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            var index = Interlocked.Increment(ref Requests) - 1;
            if (index >= bodies.Length)
                await Task.Delay(Timeout.Infinite, cancel);
            var content = new ByteArrayContent(bodies[index]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
