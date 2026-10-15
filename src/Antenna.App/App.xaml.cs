using Antenna.App.Services;
using Microsoft.UI.Xaml;

namespace Antenna.App;

public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Loads the native libvlc shipped by the VideoLAN.LibVLC.Windows package.
        LibVLCSharp.Shared.Core.Initialize();

        // Locked categories stay hidden until the PIN is entered in this session.
        Parental.Apply();

        Window = new MainWindow();
        Window.Activate();

        AutoRefresh.Start(Window.DispatcherQueue);
        Recorder.Start(Window.DispatcherQueue);
        _ = Task.Run(ImageCache.Trim);
    }
}
