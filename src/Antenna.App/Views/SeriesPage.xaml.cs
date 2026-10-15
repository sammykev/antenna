using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Antenna.Core.Playlists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>Series details, a season picker and episodes with watch progress.</summary>
public sealed partial class SeriesPage : Page
{
    private MediaItem? _series;
    private IReadOnlyList<Episode> _episodes = [];
    private List<int> _seasons = [];

    public SeriesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not LibraryEntry entry)
            return;

        _series = entry.Item;
        Header.Show(_series, null);
        BuildActions();
        var source = AppServices.Library.GetSources().FirstOrDefault(s => s.Id == _series.SourceId);
        if (source?.Kind != SourceKind.Xtream)
            return;

        Header.IsLoading = true;
        try
        {
            var (details, episodes) = await XtreamClient.For(AppServices.Http, source).GetSeriesInfoAsync(_series.ExternalId);
            _episodes = episodes;
            Header.Show(_series, details);
            _seasons = episodes.Select(ep => ep.Season).Distinct().Order().ToList();
            Seasons.ItemsSource = _seasons.Select(s => s == 0 ? "Specials" : $"Season {s}").ToList();
            Seasons.SelectedIndex = SeasonToOpen();
            BuildActions();
        }
        catch (XtreamException ex)
        {
            ShowStatus(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            Header.IsLoading = false;
        }
    }

    /// <summary>The season of the last episode watched, or the first.</summary>
    private int SeasonToOpen()
    {
        var last = NextEpisode();
        return last is null ? 0 : Math.Max(0, _seasons.IndexOf(last.Season));
    }

    /// <summary>The episode to continue: a part-watched one, else the one after the last watched.</summary>
    private Episode? NextEpisode()
    {
        if (_series is null || _episodes.Count == 0)
            return null;
        var history = _episodes
            .Select((ep, i) => (ep, i, w: AppServices.Library.GetWatch(_series.SourceId, "ep:" + ep.Id)))
            .Where(x => x.w is not null)
            .OrderByDescending(x => x.w!.WatchedAt)
            .FirstOrDefault();
        if (history.w is null)
            return null;
        return history.w.IsResumable ? history.ep : _episodes.ElementAtOrDefault(history.i + 1) ?? history.ep;
    }

    private void BuildActions()
    {
        if (_series is not { } series)
            return;
        Header.ClearActions();
        if (NextEpisode() is { } next)
            Header.AddAction($"Continue S{next.Season:00}E{next.Number:00}", "", () => PlayEpisode(next), accent: true);
        else if (_episodes.Count > 0)
            Header.AddAction("Play first episode", "", () => PlayEpisode(_episodes[0]), accent: true);

        var favourite = AppServices.Library.IsFavourite(series);
        Header.AddAction(favourite ? "Favourited" : "Favourite", favourite ? "" : "", () =>
        {
            AppServices.Library.SetFavourite(series, !favourite);
            BuildActions();
        });
    }

    private void ShowEpisodes()
    {
        if (_series is null || Seasons.SelectedIndex < 0)
            return;
        var season = _seasons[Seasons.SelectedIndex];
        Episodes.ItemsSource = _episodes.Where(ep => ep.Season == season)
            .Select(ep => new EpisodeViewModel(ep, AppServices.Library.GetWatch(_series.SourceId, "ep:" + ep.Id)))
            .ToList();
    }

    private void PlayEpisode(Episode episode, bool resume = true)
    {
        if (_series is null)
            return;
        var request = PlayRequest.ForEpisode(_series, episode);
        if (resume && AppServices.Library.GetWatch(_series.SourceId, "ep:" + episode.Id) is { IsResumable: true } w)
            request = request with { StartAtMs = w.PositionMs };
        Playback.Play(request);
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }

    private void Seasons_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowEpisodes();

    private void Episodes_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EpisodeViewModel vm)
            PlayEpisode(vm.Episode);
    }

    private void Episodes_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not EpisodeViewModel vm || _series is null)
            return;
        var menu = new MenuFlyout();
        menu.Items.Add(ItemMenu.Item("Play", "", () => PlayEpisode(vm.Episode)));
        menu.Items.Add(ItemMenu.Item("Play from start", "", () => PlayEpisode(vm.Episode, resume: false)));
        menu.Items.Add(ItemMenu.Item("Download", "", () =>
        {
            var request = PlayRequest.ForEpisode(_series, vm.Episode);
            Recorder.Download(request.Title, request.Url, request.Options);
            ShowStatus(InfoBarSeverity.Informational, "Downloading in the background. See Recordings.");
        }));
        menu.ShowAt((UIElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
        e.Handled = true;
    }
}
