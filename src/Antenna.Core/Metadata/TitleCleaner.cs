using System.Text.RegularExpressions;

namespace Antenna.Core.Metadata;

/// <summary>
/// Turns IPTV catalogue names into something a film database can match:
/// "EN - The Matrix (1999) [4K]" → ("The Matrix", 1999); "|UK| Line of Duty S01 FHD" → ("Line of Duty", null).
/// </summary>
public static partial class TitleCleaner
{
    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\|[^|]*\||[A-Za-z0-9]{2,6}\s*[:|]\s*|[A-Za-z]{2,3}\s+-\s+|(?:4k|uhd|fhd|hd|sd)\s*-\s*)", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();

    [GeneratedRegex(@"[\(\[]\s*((?:19|20)\d{2})\s*[\)\]]|\b((?:19|20)\d{2})\s*$")]
    private static partial Regex Year();

    [GeneratedRegex(@"\b(4k|uhd|fhd|hd|sd|hevc|h\.?26[45]|x26[45]|1080p|720p|2160p|multi|sub|dub|vostfr|s\d{1,2}(e\d{1,3})?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    public static (string Title, int? Year) Parse(string name)
    {
        // "Dune.Part.Two.2024.1080p" style names: dots and underscores are word gaps.
        var s = name.Replace('_', ' ').Replace('.', ' ');
        // Prefixes can stack: "|EN| 4K - Movie".
        for (var i = 0; i < 3; i++)
        {
            var stripped = Prefix().Replace(s, "", 1);
            if (stripped == s)
                break;
            s = stripped;
        }

        // A year in brackets wins ("Blade Runner 2049 (2017)"); otherwise a trailing year once
        // quality tags are gone ("Dune Part Two 2024 1080p").
        int? year = null;
        var match = Year().Match(s);
        if (!match.Success || !match.Groups[1].Success)
        {
            s = Noise().Replace(s, " ").TrimEnd();
            match = Year().Match(s);
        }
        if (match.Success)
        {
            year = int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            s = s.Remove(match.Index, match.Length);
        }

        s = Brackets().Replace(s, " ");
        s = Noise().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim(' ', '-', ':', '|');
        return (s, year);
    }
}
