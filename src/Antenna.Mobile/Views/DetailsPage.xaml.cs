using Antenna.Core;
using Antenna.Core.Playlists;
using Antenna.Mobile.Services;

namespace Antenna.Mobile.Views;

/// <summary>
/// A movie (artwork, plot, Play or Continue) or a series (the same, plus seasons and episodes). Details come
/// from the provider for Xtream Codes accounts; M3U entries show what the playlist has.
/// </summary>
public partial class DetailsPage : ContentPage
{
    private readonly MediaItem _item;
    private IReadOnlyList<Episode> _episodes = [];
    private List<int> _seasons = [];

    public DetailsPage(LibraryEntry entry)
    {
        _item = entry.Item;
        InitializeComponent();
        Title = _item.Title;
        TitleText.Text = _item.Title;
        MetaText.Text = _item.Group;
        if (!string.IsNullOrWhiteSpace(_item.LogoUrl))
            Poster.Source = Backdrop.Source = _item.LogoUrl;
        PlayButton.IsEnabled = !IsSeries;
        ShowFavourite();
    }

    private bool IsSeries => _item.Kind == MediaKind.Series;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ShowActions();
        if (_episodes.Count > 0)
            ShowEpisodes();  // back from the player: progress has changed
        if (Busy.IsRunning || PlotText.Text is { Length: > 0 } || ErrorText.IsVisible)
            return;
        await LoadDetailsAsync();
    }

    private async Task LoadDetailsAsync()
    {
        var source = MobileServices.Library.GetSources().FirstOrDefault(s => s.Id == _item.SourceId);
        if (source?.Kind != SourceKind.Xtream)
        {
            if (IsSeries)
                ShowError("This playlist doesn't list episodes. Search for them under Movies, or use an Xtream Codes login.");
            return;
        }

        Busy.IsRunning = true;
        try
        {
            var client = XtreamClient.For(MobileServices.Http, source);
            MediaDetails details;
            if (IsSeries)
            {
                (details, _episodes) = await Task.Run(() => client.GetSeriesInfoAsync(_item.ExternalId));
                _seasons = [.. _episodes.Select(e => e.Season).Distinct().Order()];
                Seasons.ItemsSource = _seasons.Select(s => new GroupChip(s == 0 ? "Specials" : $"Season {s}", null)).ToList();
                Seasons.IsVisible = _seasons.Count > 0;
                if (_seasons.Count > 0)
                    Seasons.SelectedItem = ((List<GroupChip>)Seasons.ItemsSource)[Math.Max(0, _seasons.IndexOf(NextEpisode()?.Season ?? _seasons[0]))];
                if (_episodes.Count == 0)
                    ShowError("The provider lists no episodes for this series.");
            }
            else
            {
                details = await Task.Run(() => client.GetVodInfoAsync(_item.ExternalId));
            }
            ShowDetails(details);
            ShowActions();
        }
        catch (Exception ex) when (ex is XtreamException or HttpRequestException or TaskCanceledException)
        {
            Log.Info($"Details for {_item.Title} failed: {ex.Message}");
            ShowError(IsSeries ? $"Couldn't load the episodes: {ex.Message}" : $"Couldn't load the details: {ex.Message}");
        }
        finally
        {
            Busy.IsRunning = false;
        }
    }

    private void ShowDetails(MediaDetails details)
    {
        PlotText.Text = details.Plot;
        var meta = new[] { details.Released?.Split('-')[0], details.Genre, details.Duration, details.Rating is { Length: > 0 } r ? $"★ {r}" : null }
            .Where(x => !string.IsNullOrWhiteSpace(x));
        MetaText.Text = string.Join(" · ", meta);
        CreditsText.Text = string.Join("\n", new[]
        {
            details.Director is { Length: > 0 } d ? $"Director: {d}" : null,
            details.Cast is { Length: > 0 } c ? $"Cast: {c}" : null,
        }.Where(x => x is not null));
        if (!string.IsNullOrWhiteSpace(details.PosterUrl))
            Poster.Source = details.PosterUrl;
        if (!string.IsNullOrWhiteSpace(details.BackdropUrl ?? details.PosterUrl))
            Backdrop.Source = details.BackdropUrl ?? details.PosterUrl;
    }

    // ---- Movie ----

    private WatchRecord? MovieWatch() => MobileServices.Library.GetWatch(_item.SourceId, _item.ExternalId);

    private void ShowActions()
    {
        if (IsSeries)
        {
            var next = NextEpisode();
            PlayButton.IsEnabled = _episodes.Count > 0;
            PlayButton.Text = next is null ? "Play first episode" : $"Continue S{next.Season:00}E{next.Number:00}";
            return;
        }
        var watch = MovieWatch() is { IsResumable: true } w ? w : null;
        PlayButton.Text = watch is null ? "Play" : $"Continue ({watch.Progress:P0})";
        RestartButton.IsVisible = watch is not null;
    }

    private async void Play_Clicked(object? sender, EventArgs e)
    {
        if (IsSeries)
        {
            if ((NextEpisode() ?? _episodes.FirstOrDefault()) is { } episode)
                await PlayEpisodeAsync(episode);
            return;
        }
        await Playback.PlayAsync([_item], 0);
    }

    private async void Restart_Clicked(object? sender, EventArgs e)
    {
        // Forget the position, then play from the start.
        await Task.Run(() => MobileServices.Library.RemoveWatch(_item.SourceId, _item.ExternalId));
        await Playback.PlayAsync([_item], 0);
    }

    private void Favourite_Clicked(object? sender, EventArgs e)
    {
        MobileServices.Library.SetFavourite(_item, !MobileServices.Library.IsFavourite(_item));
        ShowFavourite();
    }

    private void ShowFavourite() =>
        FavouriteButton.Text = MobileServices.Library.IsFavourite(_item) ? "★ Favourite" : "☆ Favourite";

    // ---- Series ----

    /// <summary>The episode to continue: a part-watched one, else the one after the last watched.</summary>
    private Episode? NextEpisode()
    {
        if (_episodes.Count == 0)
            return null;
        var last = _episodes
            .Select((ep, i) => (ep, i, w: MobileServices.Library.GetWatch(_item.SourceId, "ep:" + ep.Id)))
            .Where(x => x.w is not null)
            .OrderByDescending(x => x.w!.WatchedAt)
            .FirstOrDefault();
        if (last.w is null)
            return null;
        return last.w.IsResumable ? last.ep : _episodes.ElementAtOrDefault(last.i + 1) ?? last.ep;
    }

    private void Seasons_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ShowEpisodes();

    private void ShowEpisodes()
    {
        var index = Seasons.ItemsSource is List<GroupChip> chips && Seasons.SelectedItem is GroupChip chip ? chips.IndexOf(chip) : -1;
        if (index < 0 || index >= _seasons.Count)
            return;
        var season = _seasons[index];
        Episodes.ItemsSource = _episodes.Where(ep => ep.Season == season)
            .Select(ep => new EpisodeRow(ep, MobileServices.Library.GetWatch(_item.SourceId, "ep:" + ep.Id)))
            .ToList();
    }

    private async void Episode_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is EpisodeRow row)
            await PlayEpisodeAsync(row.Episode);
    }

    /// <summary>Plays an episode with the whole series queued after it, so the next one follows on.</summary>
    private Task PlayEpisodeAsync(Episode episode)
    {
        var queue = _episodes.Select(ep => PlayItem.For(_item, ep)).ToList();
        return Playback.PlayAsync(queue, Math.Max(0, _episodes.ToList().IndexOf(episode)));
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }
}
