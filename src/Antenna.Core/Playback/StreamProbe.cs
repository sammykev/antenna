using System.Text;

namespace Antenna.Core.Playback;

public enum ProbeResult
{
    /// <summary>The stream answered and sent video data (or an HLS playlist with segments).</summary>
    OnAir,

    /// <summary>The server refused, sent nothing, or didn't answer in time.</summary>
    OffAir,

    /// <summary>Not something that can be checked over HTTP (rtmp, udp, a local file): assume it plays.</summary>
    Unknown,
}

/// <summary>
/// Checks a live channel is actually broadcasting before offering it for a match: guides often list a
/// match on a channel that's dark, geo-blocked or showing a slate. It connects, reads a little of the
/// stream and lets go, so it holds a provider connection for a second or two at most.
/// </summary>
public static class StreamProbe
{
    /// <summary>A few MPEG-TS packets: enough to know the server is really sending video.</summary>
    private const int EnoughBytes = 188 * 32;

    public static async Task<ProbeResult> CheckAsync(HttpClient http, string? url, IEnumerable<string> options, TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return ProbeResult.Unknown;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        var headers = LiveCapture.HeadersFrom(options);
        try
        {
            return await CheckUrlAsync(http, uri, headers, depth: 0, timer.Token) ? ProbeResult.OnAir : ProbeResult.OffAir;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return ProbeResult.OffAir;
        }
    }

    private static async Task<bool> CheckUrlAsync(HttpClient http, Uri uri, IReadOnlyList<(string Name, string Value)> headers, int depth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return false;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[EnoughBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                break;
            read += n;
            // A playlist is short: look at it as soon as it's clearly one.
            if (read >= 7 && IsPlaylist(buffer, read))
                break;
        }

        if (IsPlaylist(buffer, read))
        {
            // HLS: a full playlist is needed to see segments; it's small, so read the rest.
            var text = Encoding.UTF8.GetString(buffer, 0, read) + await new StreamReader(stream).ReadToEndAsync(ct);
            return depth < 2 && await CheckPlaylistAsync(http, uri, text, headers, depth, ct);
        }
        return read >= EnoughBytes;
    }

    /// <summary>
    /// The first of <paramref name="channels"/> (in order, up to <paramref name="max"/>) that's broadcasting.
    /// Returns null when none answered, with the ones found dark in <c>Dark</c>.
    /// </summary>
    public static async Task<(LibraryEntry? OnAir, IReadOnlyList<LibraryEntry> Dark)> FirstOnAirAsync(HttpClient http,
        IReadOnlyList<LibraryEntry> channels, TimeSpan timeout, int max = 4, CancellationToken ct = default)
    {
        var dark = new List<LibraryEntry>();
        foreach (var channel in channels.Take(max))
        {
            if (await CheckAsync(http, channel.Item.StreamUrl, channel.Item.Options, timeout, ct) != ProbeResult.OffAir)
                return (channel, dark);
            dark.Add(channel);
        }
        return (null, dark);
    }

    /// <summary>A master playlist is checked through its first variant; a media playlist must list segments.</summary>
    private static async Task<bool> CheckPlaylistAsync(HttpClient http, Uri playlist, string text, IReadOnlyList<(string, string)> headers, int depth,
        CancellationToken ct)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                var variant = lines.Skip(i + 1).FirstOrDefault(l => !l.StartsWith('#'));
                return variant is not null && Uri.TryCreate(playlist, variant, out var next)
                    && await CheckUrlAsync(http, next, headers, depth + 1, ct);
            }
        }
        return lines.Any(l => l.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlaylist(byte[] data, int length)
    {
        var start = length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        return length - start >= 7 && Encoding.ASCII.GetString(data, start, 7) == "#EXTM3U";
    }
}
