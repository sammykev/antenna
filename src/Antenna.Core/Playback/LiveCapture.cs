using System.Net.Http.Headers;

namespace Antenna.Core.Playback;

/// <summary>
/// Downloads a live MPEG-TS stream into a <see cref="TimeshiftBuffer"/>, reconnecting when the
/// provider drops the connection. HLS playlists can't be buffered this way and are reported as unsupported.
/// </summary>
public sealed class LiveCapture
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly IReadOnlyList<string> _options;
    private readonly TimeshiftBuffer _buffer;

    public LiveCapture(HttpClient http, string url, IReadOnlyList<string> playerOptions, TimeshiftBuffer buffer)
    {
        _http = http;
        _url = url;
        _options = playerOptions;
        _buffer = buffer;
    }

    /// <summary>Set when the first connection worked and data is flowing.</summary>
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Why the capture stopped, if it failed.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether a URL looks like something this can buffer (not an HLS playlist, not a local file).</summary>
    public static bool CanBuffer(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && !uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);

    /// <summary>HTTP headers from LibVLC options like ":http-user-agent=…" and ":http-referrer=…".</summary>
    public static IReadOnlyList<(string Name, string Value)> HeadersFrom(IEnumerable<string> options)
    {
        var headers = new List<(string, string)>();
        foreach (var option in options)
        {
            var o = option.TrimStart(':');
            if (o.StartsWith("http-user-agent=", StringComparison.OrdinalIgnoreCase))
                headers.Add(("User-Agent", o["http-user-agent=".Length..]));
            else if (o.StartsWith("http-referrer=", StringComparison.OrdinalIgnoreCase) || o.StartsWith("http-referer=", StringComparison.OrdinalIgnoreCase))
                headers.Add(("Referer", o[(o.IndexOf('=') + 1)..]));
        }
        return headers;
    }

    public async Task RunAsync(CancellationToken cancel)
    {
        var failures = 0;
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                    foreach (var (name, value) in HeadersFrom(_options))
                        request.Headers.TryAddWithoutValidation(name, value);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                    response.EnsureSuccessStatusCode();
                    if (IsPlaylist(response.Content.Headers.ContentType))
                    {
                        Fail("the stream is an HLS playlist");
                        return;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancel);
                    while (true)
                    {
                        var n = await ReadWithTimeoutAsync(stream, buffer, cancel);
                        if (n <= 0)
                            break;
                        _buffer.Append(buffer.AsSpan(0, n));
                        failures = 0;
                        Started.TrySetResult(true);
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
                {
                    Error = ex.Message;
                }

                // Dropped or refused: reconnect with backoff; give up after a run of failures.
                if (failures >= Backoff.Length)
                {
                    Fail(Error ?? "the stream kept dropping");
                    return;
                }
                await Task.Delay(Backoff[failures++], cancel);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Started.TrySetResult(false);
            _buffer.Complete();
        }
    }

    private void Fail(string reason)
    {
        Error = reason;
        Started.TrySetResult(false);
    }

    private static bool IsPlaylist(MediaTypeHeaderValue? type) =>
        type?.MediaType is { } t && (t.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || t.Contains("dash+xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>A stalled connection (no data for 15 s) counts as dropped.</summary>
    private static async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await stream.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("no data for 15 seconds");
        }
    }
}
