using System.Text.Json;

namespace Antenna.Mobile.Services;

/// <summary>Looks for a newer APK on the GitHub releases page and offers to download it.</summary>
public static class Updates
{
    private const string LatestRelease = "https://api.github.com/repos/sammykev/antenna/releases/latest";

    private static bool _checked;

    public static async Task CheckOnceAsync(Page page)
    {
        if (_checked)
            return;
        _checked = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await MobileServices.Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var tag = json.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var apk = json.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                ? assets.EnumerateArray()
                    .Where(a => a.TryGetProperty("name", out var n) && n.GetString()?.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(a => a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null)
                    .FirstOrDefault(u => u is not null)
                : null;
            if (tag is null || apk is null || !Version.TryParse(tag.TrimStart('v'), out var latest))
                return;
            if (latest <= new Version(AppInfo.Version.Major, AppInfo.Version.Minor, Math.Max(AppInfo.Version.Build, 0)))
                return;

            Log.Info($"Update available: {latest}");
            if (await page.DisplayAlertAsync("Update available", $"Antenna {latest} is out. Download and install it now?", "Download", "Later"))
                await Launcher.OpenAsync(apk);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Info($"Update check failed: {ex.Message}");
        }
    }
}
