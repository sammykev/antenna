using System.Text.Json;

namespace Antenna.App.Services;

/// <summary>A website the user saved; it opens in their browser. Antenna ships with none.</summary>
internal sealed record UserLink(string Name, string Url);

/// <summary>The user's own links (Matches page → Links), stored in settings.</summary>
internal static class UserLinks
{
    public static IReadOnlyList<UserLink> Load()
    {
        try
        {
            return AppSettings.Links is { Length: > 0 } json ? JsonSerializer.Deserialize<List<UserLink>>(json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Save(IReadOnlyList<UserLink> links) => AppSettings.Links = JsonSerializer.Serialize(links);

    /// <summary>A usable http(s) address, adding "https://" when it was typed without one.</summary>
    public static Uri? Normalise(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
            return null;
        if (!t.Contains("://", StringComparison.Ordinal))
            t = "https://" + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }
}
