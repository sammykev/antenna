using Android.App;
using Android.Runtime;
using Antenna.Mobile.Services;

namespace Antenna.Mobile;

[Application]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Crashed", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("Unobserved task error", e.Exception);
        return MauiProgram.CreateMauiApp();
    }
}
