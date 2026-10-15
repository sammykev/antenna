using Microsoft.UI.Dispatching;
using Velopack;
using Velopack.Sources;

namespace Antenna.App.Services;

/// <summary>
/// In-app updates from the GitHub Releases of the Antenna repository (built and uploaded by the
/// release workflow with Velopack). Only active when Antenna was installed with Setup.exe; a copy
/// run from a folder or with "dotnet run" reports that updates are unavailable.
/// </summary>
internal static class Updater
{
    public const string RepositoryUrl = "https://github.com/sammykev/antenna";

    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private static UpdateManager? _manager;
    private static DispatcherQueueTimer? _timer;
    private static Task<UpdateInfo?>? _running;

    /// <summary>Set once an update has been downloaded and only needs a restart.</summary>
    public static UpdateInfo? Ready { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Ready"/> is set.</summary>
    public static event Action<UpdateInfo>? UpdateReady;

    private static UpdateManager? Manager
    {
        get
        {
            if (_manager is not null)
                return _manager;
            try
            {
                _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "updater");
            }
            return _manager;
        }
    }

    public static bool IsInstalled => Manager?.IsInstalled == true;

    public static string CurrentVersion =>
        (IsInstalled ? Manager!.CurrentVersion?.ToString() : null)
        ?? typeof(Updater).Assembly.GetName().Version?.ToString(3)
        ?? "?";

    /// <summary>Checks now and again every few hours; downloads in the background when allowed.</summary>
    public static void Start(DispatcherQueue ui)
    {
        if (!IsInstalled)
        {
            Log.Info("updates: not installed with Setup.exe, automatic updates are off");
            return;
        }
        _timer = ui.CreateTimer();
        _timer.Interval = FirstCheck;
        _timer.Tick += async (timer, _) =>
        {
            timer.Interval = Interval;
            if (!AppSettings.AutoUpdate || Ready is not null)
                return;
            try
            {
                if (await CheckAsync() is { } update)
                    await DownloadAsync(update, null);
            }
            catch (Exception ex)
            {
                Log.Info($"updates: automatic check failed: {ex.Message}");
            }
        };
        _timer.Start();
    }

    /// <summary>Returns the newer release, or null when up to date. Throws when GitHub can't be reached.</summary>
    public static Task<UpdateInfo?> CheckAsync()
    {
        if (Manager is not { IsInstalled: true } manager)
            return Task.FromResult<UpdateInfo?>(null);
        // A manual check while the timer is checking shares the same request.
        if (_running is { IsCompleted: false } running)
            return running;
        return _running = Task.Run(async () =>
        {
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            Log.Info(update is null ? "updates: up to date" : $"updates: {update.TargetFullRelease.Version} available");
            return update;
        });
    }

    /// <summary>Downloads the update (only the changed parts when possible) so a restart applies it.</summary>
    public static async Task DownloadAsync(UpdateInfo update, Action<int>? progress)
    {
        if (Manager is not { } manager)
            return;
        // Called from the UI thread, so the event below is raised back on it.
        await Task.Run(() => manager.DownloadUpdatesAsync(update, progress));
        Log.Info($"updates: {update.TargetFullRelease.Version} downloaded");
        Ready = update;
        UpdateReady?.Invoke(update);
    }

    /// <summary>Closes Antenna, installs the downloaded update and starts the new version.</summary>
    public static void RestartToUpdate()
    {
        if (Manager is { } manager && Ready is { } update)
        {
            Log.Info($"updates: restarting into {update.TargetFullRelease.Version}");
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
    }
}
