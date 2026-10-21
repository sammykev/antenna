using Antenna.Core.Sync;
using Microsoft.UI.Dispatching;

namespace Antenna.App.Services;

/// <summary>
/// Keeps playlists, favourites and resume points in step with the phone app through an encrypted sync file in a
/// folder the user picks (inside Google Drive or OneDrive, so their cloud client carries it to the phone).
/// Syncs shortly after start-up, every 5 minutes, and on demand.
/// </summary>
internal static class CloudSync
{
    private static DispatcherQueueTimer? _timer;
    private static bool _running;

    /// <summary>Raised on the UI thread after each sync: a summary, or the error.</summary>
    public static event Action<string?, string?>? Synced;

    public static bool IsOn => AppSettings.SyncFolder is not null && !string.IsNullOrEmpty(AppSettings.SyncPassphrase);

    public static string? FilePath => AppSettings.SyncFolder is { } folder ? Path.Combine(folder, FileSyncTarget.FileName) : null;

    public static DateTimeOffset? LastSynced { get; private set; }

    public static void Start(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(5);
        _timer.Tick += async (_, _) => await RunAsync();
        _timer.Start();
        queue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            await RunAsync();
        });
    }

    public static async Task RunAsync()
    {
        if (_running || !IsOn || FilePath is not { } path || AppSettings.SyncPassphrase is not { } passphrase)
            return;
        _running = true;
        try
        {
            var outcome = await Task.Run(() => SyncService.SyncAsync(AppServices.Library, new FileSyncTarget(path), passphrase, Environment.MachineName));
            LastSynced = DateTimeOffset.Now;
            if (outcome.Changes > 0 || outcome.Wrote)
                Log.Info($"sync: {outcome.Changes} change(s) from the sync file, {outcome.NewSources.Count} new playlist(s), wrote={outcome.Wrote}");
            GuideService.InvalidateNowPlaying();
            Synced?.Invoke(outcome.Changes == 0 ? "Up to date." : $"{outcome.Changes} change(s) from your other devices.", null);
            foreach (var source in outcome.NewSources)
            {
                try { await AppServices.RefreshSourceAsync(source); }
                catch (Exception ex) { Log.Info($"sync: couldn't load {source.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is SyncPassphraseException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Info($"sync: {ex.Message}");
            Synced?.Invoke(null, ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    public static void TurnOff()
    {
        AppSettings.SyncFolder = null;
        AppSettings.SyncPassphrase = null;
        SyncService.ResetBase(AppServices.Library);
        LastSynced = null;
    }
}
