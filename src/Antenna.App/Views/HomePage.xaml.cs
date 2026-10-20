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
            var counts = await Task.Run(() => AppServices.Library.CountItems());
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

            var reminders = await Task.Run(AppServices.Library.GetReminders);
            Show(ComingUpSection, ComingUpList, reminders.Take(RowLimit).Select(r => new ReminderRow(r)).ToList());
            Show(ContinueSection, ContinueList, resumable);
            Show(OnNowSection, OnNowList, _onNow);
            Show(RecentSection, RecentList, recent);
            EmptySection.Visibility = counts.Playlists > 0 && resumable.Count == 0 && _onNow.Count == 0 && recent.Count == 0 && reminders.Count == 0
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
}
