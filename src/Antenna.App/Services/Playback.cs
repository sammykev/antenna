using Antenna.App.ViewModels;
using Antenna.App.Views;
using Antenna.Core;

namespace Antenna.App.Services;

internal static class Playback
{
    /// <summary>Plays a channel or movie, or opens the episode list for a series.</summary>
    public static void Open(ItemViewModel vm)
    {
        var item = vm.Item;
        if (item.Kind == MediaKind.Series)
        {
            App.Window.Frame.Navigate(typeof(SeriesPage), vm.Entry);
        }
        else if (item.StreamUrl is not null)
        {
            App.Window.Frame.Navigate(typeof(PlayerPage), new PlayRequest(item.Title, item.StreamUrl, item.Kind == MediaKind.Live));
        }
    }
}
