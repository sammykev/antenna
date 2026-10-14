using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Antenna.Core.Epg;

/// <summary>
/// Links playlist channels to guide channels: by tvg-id first, then by name for
/// playlists whose ids are missing or don't match the guide ("UK: BBC One HD" ↔ "BBC One").
/// </summary>
public static partial class EpgMatcher
{
    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\|[^|]*\||[A-Z]{2,4}\s*[:|\-])\s*")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"\b(uhd|fhd|hd|sd|4k|hevc|h265|1080p?|720p?|50fps|backup|vip)\b")]
    private static partial Regex QualityRegex();

    /// <returns>Map from item external id to the guide channel id it should use.</returns>
    public static Dictionary<string, string> Match(IEnumerable<MediaItem> liveItems, IReadOnlyList<XmltvChannel> channels)
    {
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>();
        foreach (var c in channels)
        {
            byId.TryAdd(c.Id, c.Id);
            foreach (var name in c.DisplayNames)
                byName.TryAdd(Normalize(name), c.Id);
            byName.TryAdd(Normalize(c.Id), c.Id);
        }

        var result = new Dictionary<string, string>();
        foreach (var item in liveItems)
        {
            if (item.EpgChannelId is { } id && byId.TryGetValue(id, out var exact))
                result[item.ExternalId] = exact;
            else if (Normalize(item.Title) is { Length: > 0 } key && byName.TryGetValue(key, out var named))
                result[item.ExternalId] = named;
        }
        return result;
    }

    /// <summary>"UK: BBC One HD" → "bbcone"; "Télé-Matin" → "telematin".</summary>
    internal static string Normalize(string name)
    {
        var s = PrefixRegex().Replace(name, "");
        s = s.ToLowerInvariant();
        s = QualityRegex().Replace(s, "");

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        }
        return sb.ToString();
    }
}
