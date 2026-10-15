using System.Globalization;
using System.Text;

namespace Antenna.Core.Library;

/// <summary>Forgiving "does this name match what was typed" checks: case, accents and word order don't matter.</summary>
public static class TextMatch
{
    /// <summary>"Sport" matches "UK | SPORTS", "télé" matches "Tele 5"; every typed word must appear.</summary>
    public static bool ContainsAll(string text, string query)
    {
        var haystack = Fold(text);
        return Words(query).All(w => haystack.Contains(w, StringComparison.Ordinal));
    }

    public static IReadOnlyList<string> Words(string query) =>
        Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Lower case, accents removed, punctuation turned into spaces.</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return sb.ToString();
    }
}
