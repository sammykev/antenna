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
/// Home: how many live channels, movies and series there are (click one to open it), then continue
/// watching, favourite channels with what's on now, and recently watched.
/// </summary>
public sealed partial class HomePage : Page
{
    private const int RowLimit = 8;
    private List<ItemViewModel> _onNow = [];

    public HomePage()
    {
        InitializeComponent();
        Logo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo-256.png")));
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            var counts = await Task.Run(AppServices.Library.CountItems);
            LiveCount.Text = Format(counts.Live);
            MovieCount.Text = Format(counts.Movies);
            SeriesCount.Text = Format(counts.Series);
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

            Show(ContinueSection, ContinueList, resumable);
            Show(OnNowSection, OnNowList, _onNow);
            Show(RecentSection, RecentList, recent);
            EmptySection.Visibility = counts.Playlists > 0 && resumable.Count == 0 && _onNow.Count == 0 && recent.Count == 0
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

    private void Channel_RightTapped(object sender, RightTappedRoutedEventArgs e) => ItemMenu.OnRightTapped(sender, e, _onNow);
}
