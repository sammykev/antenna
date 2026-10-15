using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Antenna.Core.Library;

/// <summary>
/// Recognises the same channel across playlists despite provider naming:
/// "SKYGO: SKY SPORT TENNIS HD" and "UK: SKY SPORTS TENNIS ᴴᴰ ◉" both become "sky sport tennis".
/// </summary>
public static partial class ChannelMatch
{
    /// <summary>A leading provider or country tag: "UK:", "SKYGO:", "[DE]", "|NL|".</summary>
    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\|[^|]*\||[A-Za-z0-9]{2,8}\s*[:|])\s*")]
    private static partial Regex PrefixRegex();

    private static readonly HashSet<string> Noise =
    [
        "uhd", "fhd", "hd", "sd", "4k", "8k", "hevc", "h265", "h264", "1080p", "1080", "720p", "720", "2160p",
        "50fps", "60fps", "fps", "backup", "vip", "raw", "multi",
    ];

    /// <summary>Lower-case words without tags, quality markers, symbols, accents or plural "s".</summary>
    public static string Key(string title)
    {
        var s = PrefixRegex().Replace(title, "");
        // Compatibility decomposition turns superscript "ᴴᴰ" into "HD" and splits accents off letters.
        s = s.Normalize(NormalizationForm.FormKD).ToLowerInvariant();

        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var ch in s.Append(' '))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
            {
                word.Append(ch);
                continue;
            }
            if (word.Length > 0)
            {
                var w = word.ToString();
                word.Clear();
                if (Noise.Contains(w))
                    continue;
                // "sports" -> "sport", but not "tennis", "news", "bus" or "class".
                if (w.Length > 4 && w[^1] == 's' && w[^2] is not ('s' or 'i' or 'u'))
                    w = w[..^1];
                words.Add(w);
            }
        }
        return string.Join(' ', words);
    }
}
