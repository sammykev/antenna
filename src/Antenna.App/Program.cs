using Antenna.App.Services;
using Antenna.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Antenna.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN): Velopack must run before anything else so it can
/// handle install, update and uninstall hooks and apply a downloaded update at start-up.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var firstRun = false;
        VelopackApp.Build()
            .OnFirstRun(_ => firstRun = true)
            .Run();

        // Before anything opens the library or the log: move data out of the installer's folder.
        IReadOnlyList<string> moved = [];
        Exception? moveError = null;
        try
        {
            moved = AppDataFolder.MigrateFromLegacy();
        }
        catch (Exception ex)
        {
            moveError = ex;
        }
        if (moved.Count > 0)
            Log.Info($"Moved {string.Join(", ", moved)} from {AppDataFolder.LegacyPath} to {AppDataFolder.Path}");
        if (moveError is not null)
            Log.Error(moveError, "moving data folder");
        if (firstRun)
            Log.Info("First start after installing");

        App.SmokeTest = args.Contains("--smoke-test");

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
