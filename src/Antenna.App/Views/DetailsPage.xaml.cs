using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Antenna.Core.Playlists;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>Movie details with play/resume, favourite and download.</summary>
public sealed partial class DetailsPage : Page
{
    private LibraryEntry? _entry;

    public DetailsPage()
    {
        InitializeComponent();
        // Kept in memory so Back returns to it exactly as it was left.
        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not LibraryEntry entry)
            return;
        if (e.NavigationMode == NavigationMode.Back && Equals(entry.Item, _entry?.Item))
            return;
        _entry = entry;
        var item = entry.Item;
        Header.Show(item, null);
        BuildActions();

        var source = AppServices.Library.GetSources().FirstOrDefault(s => s.Id == item.SourceId);
        Header.IsLoading = true;
        MediaDetails? details = null;
        try
        {
            if (source?.Kind == SourceKind.Xtream)
                details = await XtreamClient.For(AppServices.Http, source).GetVodInfoAsync(item.ExternalId);
        }
        catch (XtreamException)
        {
            // Details are a bonus; the movie still plays.
        }
        try
        {
            details = await Artwork.CompleteAsync(item, details);
            if (details is not null && ReferenceEquals(_entry, entry))
                Header.Show(item, details);
        }
        finally
        {
            Header.IsLoading = false;
        }
    }

    private void BuildActions()
    {
        if (_entry is not { Item: var item })
            return;
        Header.ClearActions();
        var watch = AppServices.Library.GetWatch(item.SourceId, item.ExternalId);
        if (watch is { IsResumable: true })
        {
            Header.AddAction($"Resume from {Ui.Clock(watch.PositionMs)}", "", () => Playback.PlayMovie(item), accent: true);
            Header.AddAction("Play from start", "", () => Playback.PlayMovie(item, resume: false));
        }
        else
        {
            Header.AddAction("Play", "", () => Playback.PlayMovie(item, resume: false), accent: true);
        }

        var favourite = AppServices.Library.IsFavourite(item);
        Header.AddAction(favourite ? "Favourited" : "Favourite", favourite ? "" : "", () =>
        {
            AppServices.Library.SetFavourite(item, !favourite);
            BuildActions();
        });

        if (item.StreamUrl is { } url)
        {
            Header.AddAction("Download", "", () =>
            {
                Recorder.Download(item.Title, url, item.Options);
                Status.Severity = InfoBarSeverity.Informational;
                Status.Message = "Downloading in the background. See Recordings.";
                Status.IsOpen = true;
            });
        }
    }
}
