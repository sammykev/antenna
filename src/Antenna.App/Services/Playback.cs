using Antenna.App.ViewModels;
using Antenna.App.Views;
using Antenna.Core;

namespace Antenna.App.Services;

internal static class Playback
{
    /// <summary>
    /// Opens a tile: live channels play (with the surrounding list for zapping), movies open their
    /// details page, series their episode list.
    /// </summary>
    public static void Open(ItemViewModel vm, IReadOnlyList<ItemViewModel>? siblings = null)
    {
        var item = vm.Item;
        switch (item.Kind)
        {
            case MediaKind.Series:
                App.Window.Frame.Navigate(typeof(SeriesPage), vm.Entry);
                break;
            case MediaKind.Movie:
                App.Window.Frame.Navigate(typeof(DetailsPage), vm.Entry);
                break;
            default:
                var channels = siblings?.Where(s => s.Item.Kind == MediaKind.Live).Select(s => s.Item).ToList();
                Play(PlayRequest.ForItem(item, channels));
                break;
        }
    }

    /// <summary>Plays a movie straight away, resuming where it was left if there's history.</summary>
    public static void PlayMovie(MediaItem item, bool resume = true)
    {
        var request = PlayRequest.ForItem(item);
        if (resume && AppServices.Library.GetWatch(item.SourceId, item.ExternalId) is { IsResumable: true } w)
            request = request with { StartAtMs = w.PositionMs };
        Play(request);
    }

    public static void Play(PlayRequest request)
    {
        if (string.IsNullOrEmpty(request.Url))
            return;
        App.Window.Frame.Navigate(typeof(PlayerPage), request);
    }

    /// <summary>Plays a history entry. Live channels get their category back for zapping.</summary>
    public static void Resume(WatchRecord record)
    {
        if (record.Kind == MediaKind.Live && AppServices.Library.GetItem(record.SourceId, record.ExternalId, MediaKind.Live) is { } entry)
        {
            var channels = AppServices.Library.GetItems(MediaKind.Live, entry.Item.SourceId, entry.Item.Group).Select(e => e.Item).ToList();
            Play(PlayRequest.ForItem(entry.Item, channels));
            return;
        }
        Play(PlayRequest.ForWatch(record));
    }
}
