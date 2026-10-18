using Antenna.Core;
using Microsoft.UI.Dispatching;

namespace Antenna.App.Services;

/// <summary>
/// Notices when the previous start never got going (a crash during start-up) and starts without
/// the optional features most likely to be involved, so a bad setting can't make Antenna crash
/// on every launch.
/// </summary>
internal static class SafeStart
{
    private static readonly string Marker = Path.Combine(AppDataFolder.Path, "starting");
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(15);

    /// <summary>True when this start is recovering from a crashed one.</summary>
    public static bool Recovering { get; private set; }

    /// <summary>Call before the window is created.</summary>
    public static void Begin()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder.Path);
            if (File.Exists(Marker) && !App.SmokeTest)
            {
                Recovering = true;
                Log.Info("safe start: the last start didn't finish; TV mode and background channel buffering are off");
                AppSettings.TvMode = false;
                AppSettings.PreloadNextChannel = false;
            }
            File.WriteAllText(Marker, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "safe start");
        }
    }

    /// <summary>Call once the window is up: after a while running, this start counts as healthy.</summary>
    public static void Running(DispatcherQueue queue)
    {
        var timer = queue.CreateTimer();
        timer.Interval = HealthyAfter;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            try
            {
                File.Delete(Marker);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "safe start");
            }
        };
        timer.Start();
    }
}
