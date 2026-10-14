using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Antenna.Core.Playlists;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>Episode list for an Xtream series, fetched on demand.</summary>
public sealed partial class SeriesPage : Page
{
    private string _seriesTitle = "";

    public SeriesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not LibraryEntry entry)
            return;

        _seriesTitle = entry.Item.Title;
        Header.Text = _seriesTitle;
        var source = AppServices.Library.GetSources().FirstOrDefault(s => s.Id == entry.Item.SourceId);
        if (source is null)
            return;

        Busy.IsActive = true;
        try
        {
            var client = XtreamClient.For(AppServices.Http, source);
            var episodes = await client.GetEpisodesAsync(entry.Item.ExternalId);
            Episodes.ItemsSource = episodes.Select(ep => new EpisodeViewModel(ep)).ToList();
        }
        catch (XtreamException ex)
        {
            Error.Message = ex.Message;
            Error.IsOpen = true;
        }
        finally
        {
            Busy.IsActive = false;
        }
    }

    private void Episodes_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EpisodeViewModel vm)
            Frame.Navigate(typeof(PlayerPage), new PlayRequest($"{_seriesTitle} · {vm.Title}", vm.Episode.StreamUrl, IsLive: false));
    }
}
