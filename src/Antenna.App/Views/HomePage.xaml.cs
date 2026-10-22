using System.Globalization;
using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>
/// Home: a featured hero (what you were watching, a favourite channel that's on, or a film), then
/// shelves of continue watching, favourite channels on now, reminders, movies, series and recently watched.
/// </summary>
public sealed partial class HomePage : Page
{
    private const int RowLimit = 12;
    private const int ShelfLimit = 24;
    private List<ItemViewModel> _onNow = [];
    private List<ItemViewModel> _movies = [];
    private List<ItemViewModel> _series = [];
    private Action? _heroPlay;
    private Action? _heroMore;
    private bool _loaded;

    public HomePage()
    {
        InitializeComponent();
        // Kept in memory so Back returns to it exactly as it was left.
        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.NavigationMode == NavigationMode.Back && _loaded)
            return;
        _loaded = true;
        Greeting.Text = DateTime.Now.Hour switch
        {
            < 5 => "Good evening",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening",
        };
        try
        {
            var counts = await Task.Run(() => AppServices.Library.CountItems());
            LiveCount.Text = $"{Format(counts.Live)} channels";
            MovieCount.Text = $"{Format(counts.Movies)} movies";
            SeriesCount.Text = $"{Format(counts.Series)} series";
            StatusText.Text = counts.Playlists switch
            {
                0 => "Add a playlist to get started (Playlists, bottom left)",
                1 => "1 playlist",
                var n => $"{n} playlists",
            };

            var history = await Task.Run(() => AppServices.Library.GetHistory(60));
            var resumable = history.Where(h => h.IsResumable).Take(RowLimit).Select(h => new WatchViewModel(h)).ToList();
            var recent = history.Where(h => !h.IsResumable).Take(RowLimit).Select(h => new WatchViewModel(h)).ToList();
            var favourites = await Task.Run(() => AppServices.Library.GetFavourites(MediaKind.Live));
            _onNow = (await GuideService.ToViewModelsAsync(favourites.Take(RowLimit * 2).ToList())).ToList();
            _movies = (await Task.Run(() => AppServices.Library.GetItems(MediaKind.Movie, limit: ShelfLimit))).Select(x => new ItemViewModel(x)).ToList();
            _series = (await Task.Run(() => AppServices.Library.GetItems(MediaKind.Series, limit: ShelfLimit))).Select(x => new ItemViewModel(x)).ToList();
            var reminders = await Task.Run(AppServices.Library.GetReminders);

            ShowHero(resumable.FirstOrDefault(), _onNow.FirstOrDefault(c => c.Now is not null), _movies.FirstOrDefault());
            Show(ContinueSection, ContinueList, resumable);
            Show(OnNowSection, OnNowList, _onNow);
            Show(ComingUpSection, ComingUpList, reminders.Take(RowLimit).Select(r => new ReminderRow(r)).ToList());
            Show(MoviesSection, MoviesList, _movies);
            Show(SeriesSection, SeriesList, _series);
            Show(RecentSection, RecentList, recent);
            EmptySection.Visibility = counts.Playlists > 0 && resumable.Count == 0 && _onNow.Count == 0 && recent.Count == 0
                                      && reminders.Count == 0 && _movies.Count == 0 && _series.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "home");
            StatusText.Text = "Couldn't load your library.";
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private static void Show<T>(FrameworkElement section, ItemsControl list, IReadOnlyList<T> items)
    {
        list.ItemsSource = items;
        section.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Format(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // ---- Hero ----

    /// <summary>Picks what to feature: something half-watched first, then a favourite channel that's on, then a film.</summary>
    private void ShowHero(WatchViewModel? resume, ItemViewModel? channel, ItemViewModel? movie)
    {
        _heroMore = null;
        HeroMore.Visibility = Visibility.Collapsed;
        HeroProgress.Visibility = Visibility.Collapsed;
        if (resume is not null)
        {
            var r = resume.Record;
            HeroKicker.Text = "CONTINUE WATCHING";
            HeroTitle.Text = r.Title;
            HeroDetails.Text = $"{Ui.Clock(r.LengthMs - r.PositionMs)} left · {Ui.Ago(r.WatchedAt)}";
            HeroProgress.Value = resume.Progress;
            HeroProgress.Visibility = Visibility.Visible;
            HeroPlayText.Text = "Resume";
            _heroPlay = () => Playback.Resume(r);
            if (r.Kind == MediaKind.Movie && AppServices.Library.GetItem(r.SourceId, r.ExternalId, MediaKind.Movie) is { } entry)
            {
                SetHeroMore("Details", () => App.Window.Frame.Navigate(typeof(DetailsPage), entry));
                _ = SetArtworkAsync(r.LogoUrl, entry.Item);
            }
            else
            {
                _ = SetArtworkAsync(r.LogoUrl, null);
            }
        }
        else if (channel is { Now: { } now })
        {
            HeroKicker.Text = "ON NOW";
            HeroTitle.Text = now.Title;
            HeroDetails.Text = $"{channel.Title} · {channel.NowTimes}" + (string.IsNullOrWhiteSpace(now.Description) ? "" : $"\n{now.Description}");
            HeroProgress.Value = channel.NowProgress;
            HeroProgress.Visibility = Visibility.Visible;
            HeroPlayText.Text = "Watch live";
            _heroPlay = () => Playback.Open(channel, _onNow);
            SetHeroMore("Guide", () => App.Window.Open("guide"));
            _ = SetLogoAsync(channel.Item.LogoUrl);
        }
        else if (movie is not null)
        {
            HeroKicker.Text = "FEATURED FILM";
            HeroTitle.Text = movie.Title;
            HeroDetails.Text = string.IsNullOrEmpty(movie.Group) ? movie.SourceName : $"{movie.Group} · {movie.SourceName}";
            HeroPlayText.Text = "Play";
            _heroPlay = () => Playback.PlayMovie(movie.Item);
            SetHeroMore("Details", () => Playback.Open(movie));
            _ = SetArtworkAsync(movie.Item.LogoUrl, movie.Item);
        }
        else
        {
            Hero.Visibility = Visibility.Collapsed;
            return;
        }
        Hero.Visibility = Visibility.Visible;
    }

    private void SetHeroMore(string text, Action action)
    {
        HeroMoreText.Text = text;
        _heroMore = action;
        HeroMore.Visibility = Visibility.Visible;
    }

    /// <summary>A film's backdrop (from TMDB when it's on) or its poster, filling the hero behind the fade.</summary>
    private async Task SetArtworkAsync(string? posterUrl, MediaItem? item)
    {
        HeroLogo.Visibility = Visibility.Collapsed;
        HeroBackdrop.Source = null;
        var url = posterUrl;
        if (item is not null && Artwork.IsEnabled && (await Artwork.CompleteAsync(item, null))?.BackdropUrl is { } backdrop)
            url = backdrop;
        if (Ui.IsWebUrl(url) && await ImageCache.GetAsync(url!) is { } path)
            HeroBackdrop.Source = Ui.FromFile(path, 720);
    }

    /// <summary>Channel logos are shown whole on the right rather than stretched behind.</summary>
    private async Task SetLogoAsync(string? logoUrl)
    {
        HeroBackdrop.Source = null;
        HeroLogo.Source = null;
        HeroLogo.Visibility = Visibility.Visible;
        if (Ui.IsWebUrl(logoUrl) && await ImageCache.GetAsync(logoUrl!) is { } path)
            HeroLogo.Source = Ui.FromFile(path, 340);
    }

    private void HeroPlay_Click(object sender, RoutedEventArgs e) => _heroPlay?.Invoke();

    private void HeroMore_Click(object sender, RoutedEventArgs e) => _heroMore?.Invoke();

    // ---- Shelves ----

    private void Count_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            App.Window.Open(tag);
    }

    private void Watch_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WatchViewModel vm)
            Playback.Resume(vm.Record);
    }

    private void Channel_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemViewModel vm)
            Playback.Open(vm, _onNow);
    }

    private void Item_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemViewModel vm)
            Playback.Open(vm);
    }

    /// <summary>A reminder in the Coming up list; ToString is what the list shows.</summary>
    private sealed record ReminderRow(Reminder Reminder)
    {
        public override string ToString()
        {
            var start = Reminder.Start.LocalDateTime;
            var day = start.Date == DateTime.Today ? "Today" : start.Date == DateTime.Today.AddDays(1) ? "Tomorrow" : start.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            return $"🔔  {day} {start:HH:mm}  ·  {Reminder.Title}  ·  {Reminder.ChannelTitle}";
        }
    }

    /// <summary>Clicking a reminder offers to watch the channel (if it's on) or cancel the reminder.</summary>
    private async void ComingUp_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ReminderRow { Reminder: var r })
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = r.Title,
            Content = $"{r.ChannelTitle}, {r.Start.LocalDateTime:ddd d MMM HH:mm}",
            PrimaryButtonText = "Watch the channel",
            SecondaryButtonText = "Cancel reminder",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary when AppServices.Library.GetItem(r.SourceId, r.ChannelId, MediaKind.Live) is { } entry:
                Playback.Play(PlayRequest.ForItem(entry.Item));
                break;
            case ContentDialogResult.Secondary:
                AppServices.Library.DeleteReminder(r.Id);
                ComingUpList.ItemsSource = AppServices.Library.GetReminders().Take(RowLimit).Select(x => new ReminderRow(x)).ToList();
                ComingUpSection.Visibility = ComingUpList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    private void Channel_RightTapped(object sender, RightTappedRoutedEventArgs e) => ItemMenu.OnRightTapped(sender, e, _onNow);

    private void Movies_RightTapped(object sender, RightTappedRoutedEventArgs e) => ItemMenu.OnRightTapped(sender, e, _movies);

    private void Series_RightTapped(object sender, RightTappedRoutedEventArgs e) => ItemMenu.OnRightTapped(sender, e, _series);
}
