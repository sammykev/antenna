using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>Favourites, "continue watching" (resume where you stopped) and recently watched.</summary>
public sealed partial class FavouritesPage : Page
{
    private List<ItemViewModel> _favourites = [];

    public FavouritesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = ShowAsync();
    }

    /// <summary>Opens a tab by its tag ("fav", "continue", "recent", "pinned"); used by the smoke test.</summary>
    internal void SelectTab(string tag) =>
        Tabs.SelectedItem = Tabs.Items.FirstOrDefault(i => i.Tag as string == tag) ?? Tabs.SelectedItem;

    private string Tab => Tabs.SelectedItem?.Tag as string ?? "fav";

    private async Task ShowAsync()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        var tab = Tab;
        Favourites.Visibility = tab == "fav" ? Visibility.Visible : Visibility.Collapsed;
        History.Visibility = tab is "continue" or "recent" ? Visibility.Visible : Visibility.Collapsed;
        Pinned.Visibility = tab == "pinned" ? Visibility.Visible : Visibility.Collapsed;

        if (tab == "pinned")
        {
            var pinned = AppServices.Library.GetFlaggedGroups(GroupFlag.Pinned);
            Pinned.ItemsSource = pinned.Select(p => new PinnedRow(p.Kind, p.Group)).ToList();
            if (pinned.Count == 0)
                ShowEmpty("No pinned categories", "Right-click a category in Live TV, Movies or Series and choose “Pin to top”.");
            return;
        }
        ClearButton.Visibility = tab == "recent" ? Visibility.Visible : Visibility.Collapsed;

        if (tab == "fav")
        {
            var entries = await Task.Run(() => AppServices.Library.GetFavourites());
            _favourites = await GuideService.ToViewModelsAsync(entries);
            Favourites.ItemsSource = _favourites;
            if (_favourites.Count == 0)
                ShowEmpty("No favourites yet", "Right-click any channel, movie or series and choose “Add to favourites”.");
            return;
        }

        var history = await Task.Run(() => AppServices.Library.GetHistory(200));
        var rows = (tab == "continue" ? history.Where(h => h.IsResumable) : history)
            .Select(h => new WatchViewModel(h)).ToList();
        History.ItemsSource = rows;
        if (rows.Count == 0)
        {
            ShowEmpty(tab == "continue" ? "Nothing to continue" : "Nothing watched yet",
                tab == "continue" ? "Movies and episodes you stop part-way through appear here." : "Channels, movies and episodes you watch appear here.");
        }
    }

    private void ShowEmpty(string title, string detail)
    {
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Favourites is not null) // Also fires during InitializeComponent.
            _ = ShowAsync();
    }

    private void Favourites_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemViewModel vm)
            Playback.Open(vm, _favourites);
    }

    private void Favourites_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        ItemMenu.OnRightTapped(sender, e, _favourites);
    }

    /// <summary>A pinned category; ToString is what the list shows.</summary>
    private sealed record PinnedRow(MediaKind Kind, string Group)
    {
        public override string ToString() =>
            $"📌 {(Group.Length == 0 ? "Uncategorised" : Group)}  ·  {Kind switch { MediaKind.Live => "Live TV", MediaKind.Movie => "Movies", _ => "Series" }}";
    }

    private void Pinned_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PinnedRow row)
            Frame.Navigate(typeof(BrowsePage), new BrowseTarget(row.Kind, row.Group));
    }

    private void History_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WatchViewModel vm)
            Playback.Resume(vm.Record);
    }

    private void History_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not WatchViewModel vm)
            return;
        var menu = new MenuFlyout();
        menu.Items.Add(ItemMenu.Item(vm.Record.IsResumable ? "Resume" : "Play", "", () => Playback.Resume(vm.Record)));
        if (vm.Record.IsResumable)
            menu.Items.Add(ItemMenu.Item("Play from the start", "", () => Playback.Play(PlayRequest.ForWatch(vm.Record) with { StartAtMs = 0 })));
        menu.Items.Add(ItemMenu.Item("Remove from history", "", () =>
        {
            AppServices.Library.RemoveWatch(vm.Record.SourceId, vm.Record.ExternalId);
            _ = ShowAsync();
        }));
        menu.ShowAt((UIElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
        e.Handled = true;
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear watch history?",
            Content = "Recently watched and resume positions will be forgotten. Favourites are kept.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            AppServices.Library.ClearHistory();
            await ShowAsync();
        }
    }
}
