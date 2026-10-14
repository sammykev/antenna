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

        Window = new MainWindow();
        Window.Activate();
    }
}
