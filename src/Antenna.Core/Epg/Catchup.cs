using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Antenna.Core.Epg;

/// <summary>
/// Builds catch-up (archive) URLs for past programmes. M3U playlists describe their
/// archive with the de-facto catchup / catchup-source attributes (as used by Kodi's
/// IPTV Simple client); Xtream accounts use the panel's /timeshift/ endpoint.
/// </summary>
public static partial class Catchup
{
    [GeneratedRegex(@"\$?\{(utc|start|utcend|end|lutc|now|timestamp|duration|offset)(?:[:\-]([^}]+))?\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\{([YmdHMS])\}")]
    private static partial Regex DatePartRegex();

    [GeneratedRegex(@"^(?<base>https?://[^/]+)/(?:live/)?(?<user>[^/]+)/(?<pass>[^/]+)/(?<id>\d+)(?:\.\w+)?$")]
    private static partial Regex XtreamLiveRegex();

    [GeneratedRegex(@"/(?<name>index|video|mono)\.m3u8(?<query>\?.*)?$")]
    private static partial Regex FlussonicHlsRegex();

    [GeneratedRegex(@"/mpegts(?<query>\?.*)?$")]
    private static partial Regex FlussonicTsRegex();

    /// <summary>A programme can be replayed once it has started and while it is inside the archive window.</summary>
    public static bool IsAvailable(MediaItem item, Programme programme, DateTimeOffset now) =>
        item.CatchupDays > 0 &&
        programme.Start < now &&
        programme.Start >= now - TimeSpan.FromDays(item.CatchupDays);

    /// <summary>Catch-up URL for an M3U channel, or null if the playlist gives no way to build one.</summary>
    public static string? BuildM3uUrl(MediaItem item, Programme programme, DateTimeOffset now)
    {
        var url = item.StreamUrl;
        if (url is null)
            return null;

        var mode = item.CatchupMode ?? InferMode(item);
        switch (mode)
        {
            case "default":
                return item.CatchupSource is { Length: > 0 } template ? Fill(template, programme, now) : null;

            case "append":
                return item.CatchupSource is { Length: > 0 } suffix ? url + Fill(suffix, programme, now) : null;

            case "shift":
            case "timeshift":
                return url + (url.Contains('?') ? '&' : '?') + Fill("utc={utc}&lutc={lutc}", programme, now);

            case "flussonic":
            case "flussonic-hls":
            case "flussonic-ts":
            case "fs":
            {
                var start = programme.Start.ToUnixTimeSeconds();
                var duration = (long)programme.Duration.TotalSeconds;
                var hls = FlussonicHlsRegex().Match(url);
                if (hls.Success)
                    return url[..hls.Index] + $"/{hls.Groups["name"].Value}-{start}-{duration}.m3u8" + hls.Groups["query"].Value;
                var ts = FlussonicTsRegex().Match(url);
                if (ts.Success)
                    return url[..ts.Index] + $"/timeshift_abs/{start}.ts" + ts.Groups["query"].Value;
                return null;
            }

            case "xc":
            {
                var m = XtreamLiveRegex().Match(url);
                if (!m.Success)
                    return null;
                var minutes = (int)Math.Ceiling(programme.Duration.TotalMinutes);
                var start = programme.Start.UtcDateTime.ToString("yyyy-MM-dd:HH-mm", CultureInfo.InvariantCulture);
                return $"{m.Groups["base"].Value}/timeshift/{m.Groups["user"].Value}/{m.Groups["pass"].Value}/{minutes}/{start}/{m.Groups["id"].Value}.ts";
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Without an explicit mode: Xtream-style URLs use the panel's timeshift API,
    /// a template means "default", and anything else gets the common utc/lutc shift.
    /// </summary>
    internal static string InferMode(MediaItem item)
    {
        if (item.CatchupSource is { Length: > 0 } src)
            return src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "default" : "append";
        if (item.StreamUrl is { } url && url.Contains("/live/", StringComparison.OrdinalIgnoreCase) && XtreamLiveRegex().IsMatch(url))
            return "xc";
        return "shift";
    }

    /// <summary>Substitutes {utc}, {utcend}, {lutc}, {duration}, {offset}, {Y}{m}{d}{H}{M}{S} and formatted variants like {utc:Y-m-d}.</summary>
    internal static string Fill(string template, Programme programme, DateTimeOffset now)
    {
        var start = programme.Start.ToUniversalTime();
        var end = programme.Stop.ToUniversalTime();

        var result = PlaceholderRegex().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;
            var time = name switch
            {
                "utcend" or "end" => end,
                "lutc" or "now" or "timestamp" => now.ToUniversalTime(),
                _ => start,
            };

            return name switch
            {
                "duration" => ((long)(end - start).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                "offset" => ((long)(now - start).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                _ when format is not null => FormatTime(time, format),
                _ => time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            };
        });

        return DatePartRegex().Replace(result, m => FormatTime(start, m.Groups[1].Value));
    }

    /// <summary>Formats with strftime-like letters: Y m d H M S.</summary>
    private static string FormatTime(DateTimeOffset time, string format)
    {
        var sb = new StringBuilder();
        foreach (var ch in format)
        {
            sb.Append(ch switch
            {
                'Y' => time.Year.ToString("0000", CultureInfo.InvariantCulture),
                'm' => time.Month.ToString("00", CultureInfo.InvariantCulture),
                'd' => time.Day.ToString("00", CultureInfo.InvariantCulture),
                'H' => time.Hour.ToString("00", CultureInfo.InvariantCulture),
                'M' => time.Minute.ToString("00", CultureInfo.InvariantCulture),
                'S' => time.Second.ToString("00", CultureInfo.InvariantCulture),
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }
}
