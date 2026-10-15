using Microsoft.UI.Dispatching;

namespace Antenna.App.Services;

/// <summary>Refreshes playlists and guides in the background when they're older than the chosen interval.</summary>
internal static class AutoRefresh
{
    private static DispatcherQueueTimer? _timer;
    private static bool _running;

    /// <summary>Raised on the UI thread after each background refresh (source name, result or error).</summary>
    public static event Action<string, RefreshResult?, string?>? Refreshed;

    public static void Start(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(15);
        _timer.Tick += async (_, _) => await RunAsync();
        _timer.Start();
        // First check shortly after start-up, once the window is responsive.
        queue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            await RunAsync();
        });
    }

    public static async Task RunAsync()
    {
        var hours = AppSettings.AutoRefreshHours;
        if (_running || hours <= 0)
            return;
        _running = true;
        try
        {
            var due = AppServices.Library.GetSources()
                .Where(s => s.LastRefreshed is null || DateTimeOffset.Now - s.LastRefreshed > TimeSpan.FromHours(hours));
            foreach (var source in due)
            {
                try
                {
                    var result = await AppServices.RefreshSourceAsync(source);
                    Refreshed?.Invoke(source.Name, result, null);
                }
                catch (Exception ex)
                {
                    Refreshed?.Invoke(source.Name, null, ex.Message);
                }
            }
        }
        finally
        {
            _running = false;
        }
    }
}
