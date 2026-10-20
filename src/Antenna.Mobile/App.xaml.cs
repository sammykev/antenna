using Antenna.Mobile.Services;

namespace Antenna.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        // A video player looks best dark, whatever the phone's theme.
        UserAppTheme = AppTheme.Dark;
        Log.Info($"Started {AppInfo.VersionString} on Android {DeviceInfo.VersionString} ({DeviceInfo.Manufacturer} {DeviceInfo.Model})");
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new AppShell());
}
