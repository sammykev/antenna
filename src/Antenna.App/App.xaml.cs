using Antenna.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Antenna.App;

public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;

    /// <summary>
    /// Started with --smoke-test (used by CI): open every main page, log the result, and exit
    /// with 0 when all of them loaded.
    /// </summary>
    public static bool SmokeTest { get; set; }

    public App()
    {
        // Log everything that escapes, and keep running where the platform allows it.
        UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UI");
            e.Handled = true;
            try
            {
                Window?.ShowError($"Something went wrong: {e.Exception.Message}", CrashReport.Details(e.Exception, "UI"));
            }
            catch (Exception)
            {
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Error(ex, "fatal");
                CrashReport.ShowFatal(ex, "keep running and had to close");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "task");
            e.SetObserved();
        };

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Info($"Starting Antenna {Updater.CurrentVersion} on {Environment.OSVersion}, {(Environment.Is64BitProcess ? "x64" : "x86")}");

        // Loads the native libvlc shipped by the VideoLAN.LibVLC.Windows package.
        Step("LibVLC", () => LibVLCSharp.Shared.Core.Initialize());

        // Locked categories stay hidden until the PIN is entered in this session.
        Step("library", Parental.Apply);

        Step("window", () =>
        {
            Window = new MainWindow();
            Window.Activate();
        });

        Step("auto refresh", () => AutoRefresh.Start(Window.DispatcherQueue));
        Step("recorder", () => Recorder.Start(Window.DispatcherQueue));
        Step("updates", () =>
        {
            Updater.UpdateReady += update => Window.ShowUpdateReady(update.TargetFullRelease.Version.ToString());
            Updater.Start(Window.DispatcherQueue);
        });
        _ = Task.Run(ImageCache.Trim);
        Log.Info("Started");
        if (SmokeTest)
            _ = Window.RunSmokeTestAsync();
    }

    /// <summary>Runs a start-up step, logging where it failed. Window and LibVLC failures are fatal.</summary>
    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"start-up: {name}");
            if (name is "window" or "LibVLC" or "library")
            {
                CrashReport.ShowFatal(ex, $"start ({name} failed)");
                throw;
            }
        }
    }
}
