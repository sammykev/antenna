using Antenna.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Antenna.App;

public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;

    public App()
    {
        // Log everything that escapes, and keep running where the platform allows it.
        UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UI");
            e.Handled = true;
            try
            {
                Window?.ShowNotice(InfoBarSeverity.Error, $"Something went wrong: {e.Exception.Message} (details in Settings → About → Open log)");
            }
            catch (Exception)
            {
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Log.Error(ex, "fatal");
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
        Log.Info($"Starting Antenna {typeof(App).Assembly.GetName().Version} on {Environment.OSVersion}, {(Environment.Is64BitProcess ? "x64" : "x86")}");

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
        _ = Task.Run(ImageCache.Trim);
        Log.Info("Started");
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
                throw;
        }
    }
}
