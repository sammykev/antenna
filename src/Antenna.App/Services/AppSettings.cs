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

    /// <summary>Open on the Home page (counts, continue watching, favourites on now) instead of the start page.</summary>
    public static bool ShowSplash { get => Get("ui.splash") != "0"; set => Set("ui.splash", value ? "1" : "0"); }

    /// <summary>Nav tag of the page shown at start-up.</summary>
    public static string StartPage { get => Get("ui.start") ?? "live"; set => Set("ui.start", value); }

    /// <summary>Saved multi-view presets, as JSON (see MultiViewPresets).</summary>
    public static string? MultiViewPresets { get => Get("multiview.presets"); set => Set("multiview.presets", value); }

    /// <summary>Stream buffer. Lower starts faster; higher survives a shaky connection.</summary>
    public static int NetworkCachingMs { get => Math.Clamp(GetInt("player.caching", 1500), 300, 20000); set => SetInt("player.caching", value); }

    /// <summary>The version that ran last time, to spot the first start after an update.</summary>
    public static string? LastVersion { get => Get("app.lastversion"); set => Set("app.lastversion", value); }

    /// <summary>"version\nnotes" of a downloaded update, shown as What's new after restarting into it.</summary>
    public static string? PendingNotes { get => Get("app.pendingnotes"); set => Set("app.pendingnotes", value); }

    /// <summary>Download new versions in the background and offer a restart (installed copies only).</summary>
    public static bool AutoUpdate { get => Get("app.autoupdate") != "0"; set => Set("app.autoupdate", value ? "1" : "0"); }

    /// <summary>Buffer live TV to disk so it can be paused and rewound.</summary>
    public static bool Timeshift { get => Get("player.timeshift") != "0"; set => Set("player.timeshift", value ? "1" : "0"); }

    /// <summary>How far back live TV can be rewound.</summary>
    public static int TimeshiftMinutes { get => Math.Clamp(GetInt("player.timeshift.minutes", 30), 5, 120); set => SetInt("player.timeshift.minutes", value); }

    public static bool HardwareDecoding { get => Get("player.hw") != "0"; set => Set("player.hw", value ? "1" : "0"); }

    /// <summary>0 = off.</summary>
    public static int AutoRefreshHours { get => GetInt("library.autorefresh", 24); set => SetInt("library.autorefresh", value); }

    public static string SubtitleLanguages { get => Get("subs.lang") ?? "en"; set => Set("subs.lang", value); }

    /// <summary>The Movie Database key (v3 API key or v4 read token) for posters and details playlists lack.</summary>
    public static string? TmdbApiKey { get => Get("tmdb.apikey"); set => Set("tmdb.apikey", Blank(value)); }

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
