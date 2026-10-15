using System.Globalization;

namespace Antenna.App.Services;

/// <summary>Typed access to settings stored in the library database.</summary>
internal static class AppSettings
{
    public static event Action? Changed;

    private static string? Get(string key) => AppServices.Library.GetSetting(key);

    private static void Set(string key, string? value)
    {
        AppServices.Library.SetSetting(key, value);
        Changed?.Invoke();
    }

    private static int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>"system", "light" or "dark".</summary>
    public static string Theme { get => Get("ui.theme") ?? "system"; set => Set("ui.theme", value); }

    /// <summary>Nav tag of the page shown at start-up.</summary>
    public static string StartPage { get => Get("ui.start") ?? "live"; set => Set("ui.start", value); }

    /// <summary>Stream buffer. Lower starts faster; higher survives a shaky connection.</summary>
    public static int NetworkCachingMs { get => Math.Clamp(GetInt("player.caching", 1500), 300, 20000); set => SetInt("player.caching", value); }

    public static bool HardwareDecoding { get => Get("player.hw") != "0"; set => Set("player.hw", value ? "1" : "0"); }

    /// <summary>0 = off.</summary>
    public static int AutoRefreshHours { get => GetInt("library.autorefresh", 24); set => SetInt("library.autorefresh", value); }

    public static string SubtitleLanguages { get => Get("subs.lang") ?? "en"; set => Set("subs.lang", value); }

    public static string? OpenSubtitlesApiKey { get => Get("subs.apikey"); set => Set("subs.apikey", Blank(value)); }

    public static string? OpenSubtitlesUser { get => Get("subs.user"); set => Set("subs.user", Blank(value)); }

    /// <summary>Stored DPAPI-encrypted.</summary>
    public static string? OpenSubtitlesPassword
    {
        get
        {
            var stored = Get("subs.pass");
            if (stored is null) return null;
            try { return new DpapiProtector().Unprotect(stored); }
            catch (Exception) { return null; }
        }
        set => Set("subs.pass", Blank(value) is { } v ? new DpapiProtector().Protect(v) : null);
    }

    public static string RecordingsFolder
    {
        get => Get("rec.folder") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Antenna");
        set => Set("rec.folder", value);
    }

    public static string? ParentalPinHash { get => Get("parental.pin"); set => Set("parental.pin", value); }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
