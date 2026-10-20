using LibVLCSharp.MAUI;

namespace Antenna.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseLibVLCSharp();
        return builder.Build();
    }
}
