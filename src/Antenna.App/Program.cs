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
        VelopackApp.Build().Run();

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
