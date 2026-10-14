using System.Text.RegularExpressions;

namespace Antenna.Core.Playlists;

public sealed record M3uPlaylist(IReadOnlyList<MediaItem> Items, string? EpgUrl);

/// <summary>
/// Parses extended M3U (#EXTM3U / #EXTINF) playlists as served by IPTV providers.
/// Tolerant of the common variations: quoted or bare attributes, #EXTGRP lines,
/// #EXTVLCOPT lines, CRLF endings and a missing header.
/// </summary>
public static partial class M3uParser
{
    [GeneratedRegex("""([\w-]+)=(?:"([^"]*)"|([^\s,]+))""")]
    private static partial Regex AttributeRegex();

    public static M3uPlaylist Parse(string content, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(content);

        var items = new List<MediaItem>();
        string? epgUrl = null;
        // Catch-up settings on the #EXTM3U line apply to every channel that doesn't override them.
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string>? attrs = null;
        string? title = null;
        string? extGroup = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                var header = ParseAttributes(line);
                foreach (var key in CatchupKeys)
                    if (header.TryGetValue(key, out var v))
                        defaults[key] = v;
                epgUrl = header.GetValueOrDefault("url-tvg") ?? header.GetValueOrDefault("x-tvg-url");
                // Some providers list several guides separated by commas; keep the first.
                epgUrl = epgUrl?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
            else if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                attrs = ParseAttributes(line);
                title = ExtractTitle(line);
                extGroup = null;
            }
            else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                extGroup = line["#EXTGRP:".Length..].Trim();
            }
            else if (line.StartsWith('#'))
            {
                // #EXTVLCOPT and other directives are not needed for playback via LibVLC.
            }
            else if (attrs is not null)
            {
                items.Add(BuildItem(sourceId, line, WithDefaults(attrs, defaults), title, extGroup, items.Count));
                attrs = null;
                title = null;
                extGroup = null;
            }
            else
            {
                // Bare URL without #EXTINF: still playable.
                items.Add(BuildItem(sourceId, line, WithDefaults(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), defaults), null, null, items.Count));
            }
        }

        return new M3uPlaylist(items, epgUrl);
    }

    private static readonly string[] CatchupKeys = ["catchup", "catchup-type", "catchup-source", "catchup-days"];

    private static Dictionary<string, string> WithDefaults(Dictionary<string, string> attrs, Dictionary<string, string> defaults)
    {
        foreach (var (key, value) in defaults)
            attrs.TryAdd(key, value);
        return attrs;
    }

    private static MediaItem BuildItem(
        string sourceId, string url, Dictionary<string, string> attrs, string? title, string? extGroup, int index)
    {
        var name = FirstNonEmpty(title, attrs.GetValueOrDefault("tvg-name")) ?? url;
        var group = FirstNonEmpty(attrs.GetValueOrDefault("group-title"), extGroup) ?? "";
        _ = int.TryParse(FirstNonEmpty(attrs.GetValueOrDefault("catchup-days"), attrs.GetValueOrDefault("tvg-rec")), out var catchupDays);

        return new MediaItem
        {
            SourceId = sourceId,
            ExternalId = url,
            Kind = ClassifyKind(url),
            Title = name,
            Group = group,
            LogoUrl = FirstNonEmpty(attrs.GetValueOrDefault("tvg-logo")),
            StreamUrl = url,
            EpgChannelId = FirstNonEmpty(attrs.GetValueOrDefault("tvg-id")),
            CatchupDays = Math.Max(0, catchupDays),
            CatchupMode = FirstNonEmpty(attrs.GetValueOrDefault("catchup"), attrs.GetValueOrDefault("catchup-type"))?.ToLowerInvariant(),
            CatchupSource = FirstNonEmpty(attrs.GetValueOrDefault("catchup-source")),
            SortIndex = index,
        };
    }

    /// <summary>
    /// Xtream-backed M3U exports put VOD under /movie/ and /series/ paths; everything else is treated as live.
    /// </summary>
    internal static MediaKind ClassifyKind(string url)
    {
        if (url.Contains("/movie/", StringComparison.OrdinalIgnoreCase))
            return MediaKind.Movie;
        if (url.Contains("/series/", StringComparison.OrdinalIgnoreCase))
            return MediaKind.Movie; // An M3U lists individual episodes, which play like movies.
        return MediaKind.Live;
    }

    private static Dictionary<string, string> ParseAttributes(string line)
    {
        // Only look at the part before the title separator so commas in titles don't confuse us.
        var attrPart = line;
        var titleComma = FindTitleComma(line);
        if (titleComma >= 0)
            attrPart = line[..titleComma];

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributeRegex().Matches(attrPart))
        {
            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            result[m.Groups[1].Value] = value.Trim();
        }
        return result;
    }

    private static string? ExtractTitle(string line)
    {
        var comma = FindTitleComma(line);
        return comma >= 0 ? FirstNonEmpty(line[(comma + 1)..]) : null;
    }

    /// <summary>Finds the comma that separates attributes from the title, skipping commas inside quotes.</summary>
    private static int FindTitleComma(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes)
                return i;
        }
        return -1;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
