using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Antenna.Core.Events;
using Antenna.Core.Recordings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>
/// Finds live and upcoming matches/events in every guide plus per-event channels, and lets you
/// tick up to four to watch together in multi-view.
/// </summary>
public sealed partial class EventsPage : Page
{
    private const int MaxTogether = 4;
    private int _version;
    private List<EventViewModel> _events = [];
    private List<ItemViewModel> _channels = [];

    public EventsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        foreach (var (label, query) in EventSearch.Suggestions)
        {
            var chip = new Button { Content = label, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 4, 14, 4) };
            chip.Click += (_, _) =>
            {
                QueryBox.Text = query;
                _ = RunAsync();
            };
            Chips.Children.Add(chip);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string { Length: > 0 } query)
        {
            QueryBox.Text = query;
            _ = RunAsync();
        }
        else if (QueryBox.Text.Length == 0)
        {
            ShowEmpty("Find a match or event", "Type a team, league, player or sport, or pick one above. Results come from your playlists' guides and event channels.");
        }
    }

    private string Range => WhenFilter.SelectedItem?.Tag as string ?? "today";

    private async Task RunAsync()
    {
        var query = QueryBox.Text.Trim();
        if (EventSearch.BuildExpression(query) is null)
        {
            ShowEmpty("Find a match or event", "Type a team, league, player or sport, or pick one above.");
            return;
        }

        var version = ++_version;
        var range = Range;
        var now = DateTimeOffset.Now;
        var (from, to) = range switch
        {
            "now" => (now, now.AddSeconds(1)),
            "week" => (now, now.AddDays(7)),
            "past" => (now.AddDays(-14), now),
            _ => (now, new DateTimeOffset(DateTime.Today.AddDays(1))),
        };

        Busy.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;
        var hits = await Task.Run(() => AppServices.Library.SearchEvents(query, from, to));
        IReadOnlyList<LibraryEntry> channels = range == "past"
            ? []
            : await Task.Run(() => AppServices.Library.SearchEventChannels(query, 60));
        if (version != _version)
            return;
        Busy.IsActive = false;

        _events = hits.Select(h => new EventViewModel(h, now))
            .Where(vm => range != "past" || vm.CanReplay)
            .ToList();
        _channels = await GuideService.ToViewModelsAsync(channels);

        Events.ItemsSource = _events;
        EventChannels.ItemsSource = _channels;
        EventsHeader.Visibility = _events.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelsHeader.Visibility = _channels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateWatchTogether();

        if (_events.Count == 0 && _channels.Count == 0)
        {
            ShowEmpty("No matching events", range switch
            {
                "now" => "Nothing matching is on right now. Try Today or Next 7 days.",
                "past" => "Nothing matching in your providers' catch-up archives.",
                _ => "Try fewer words (e.g. just a team name), another time range, or refresh your playlists so the guide is up to date.",
            });
        }
    }

    private void ShowEmpty(string title, string detail)
    {
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        EmptyState.Visibility = Visibility.Visible;
        Events.ItemsSource = null;
        EventChannels.ItemsSource = null;
        EventsHeader.Visibility = Visibility.Collapsed;
        ChannelsHeader.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }

    // ---- Watching ----

    private void Watch(EventViewModel vm, LibraryEntry channel)
    {
        var siblings = vm.Hit.Channels.Select(c => c.Item).ToList();
        Playback.Play(PlayRequest.ForItem(channel.Item, siblings));
    }

    private async Task ActivateAsync(EventViewModel vm, FrameworkElement anchor)
    {
        if (vm.IsLive)
        {
            if (vm.Hit.Channels.Count == 1)
                Watch(vm, vm.Hit.Channels[0]);
            else
                ChannelMenu(vm).ShowAt(anchor);
            return;
        }
        if (vm.CanReplay)
        {
            var channel = vm.Hit.Channels.First(c => Antenna.Core.Epg.Catchup.IsAvailable(c.Item, vm.Programme, DateTimeOffset.Now));
            try
            {
                if (await GuideService.ResolveCatchupUrlAsync(channel.Item, vm.Programme) is { } url)
                    Playback.Play(PlayRequest.ForCatchup(channel.Item, vm.Programme, url));
                else
                    ShowStatus(InfoBarSeverity.Warning, "This playlist doesn't say how to replay this channel.");
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"Couldn't start catch-up: {ex.Message}");
            }
            return;
        }
        ShowStatus(InfoBarSeverity.Informational, $"Starts {vm.When}. Right-click to set a reminder or record it.");
    }

    private MenuFlyout ChannelMenu(EventViewModel vm)
    {
        var menu = new MenuFlyout();
        foreach (var item in ChannelItems(vm))
            menu.Items.Add(item);
        return menu;
    }

    /// <summary>One "watch on …" entry per channel showing the event (fresh items: a menu item can't be in two menus).</summary>
    private IEnumerable<MenuFlyoutItem> ChannelItems(EventViewModel vm) =>
        vm.Hit.Channels.Select(c => ItemMenu.Item($"Watch on {c.Item.Title} · {c.SourceName}", "", () => Watch(vm, c)));

    private void Events_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EventViewModel vm && Events.ContainerFromItem(vm) is FrameworkElement container)
            _ = ActivateAsync(vm, container);
    }

    private void Events_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not EventViewModel vm)
            return;
        var menu = new MenuFlyout();
        var p = vm.Programme;
        var first = vm.Hit.Channels[0];

        if (vm.IsLive)
        {
            foreach (var item in ChannelItems(vm))
                menu.Items.Add(item);
            menu.Items.Add(ItemMenu.Item("Add to multi-view", "", () => MultiViewPage.Add(PlayRequest.ForItem(first.Item) with { Title = $"{p.Title} · {first.Item.Title}" })));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        if (p.Start > DateTimeOffset.Now)
        {
            var isSet = Reminders.IsSet(first.Item.SourceId, first.Item.ExternalId, p);
            menu.Items.Add(ItemMenu.Item(isSet ? "Cancel reminder" : "Remind me", isSet ? "\uE7ED" : "\uEA8F", () =>
            {
                var on = Reminders.Toggle(first.Item, p);
                ShowStatus(InfoBarSeverity.Success, on ? $"I'll remind you before it starts ({vm.When}). Keep Antenna open." : "Reminder cancelled.");
            }));
        }
        if (p.Stop > DateTimeOffset.Now && first.Item.StreamUrl is { } live)
        {
            menu.Items.Add(ItemMenu.Item(vm.IsLive ? "Record the rest" : "Record when it starts", "", () =>
            {
                var (start, stop) = RecordingPlanner.Window(p);
                Recorder.Schedule($"{p.Title}", live, first.Item.Options, start, stop);
                ShowStatus(InfoBarSeverity.Success, vm.IsLive ? "Recording now. See Recordings." : $"Scheduled for {vm.When}. Keep Antenna open to record.");
            }));
        }
        if (vm.CanReplay)
            menu.Items.Add(ItemMenu.Item("Play catch-up", "", () => _ = ActivateAsync(vm, (FrameworkElement)sender)));
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = "Nothing to do for this event", IsEnabled = false });

        menu.ShowAt((UIElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
        e.Handled = true;
    }

    private void EventChannels_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemViewModel vm)
            Playback.Open(vm, _channels);
    }

    private void EventChannels_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
        ItemMenu.OnRightTapped(sender, e, _channels);

    // ---- Watch together ----

    /// <summary>The ticked events (live ones) and event channels, as streams for multi-view.</summary>
    private List<PlayRequest> SelectedStreams()
    {
        var requests = new List<PlayRequest>();
        foreach (var vm in Events.SelectedItems.OfType<EventViewModel>().Where(v => v.IsLive))
        {
            var c = vm.Hit.Channels[0];
            requests.Add(PlayRequest.ForItem(c.Item) with { Title = $"{vm.Title} · {c.Item.Title}" });
        }
        foreach (var vm in EventChannels.SelectedItems.OfType<ItemViewModel>())
            requests.Add(PlayRequest.ForItem(vm.Item));
        return requests;
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateWatchTogether();

    private void UpdateWatchTogether()
    {
        var count = SelectedStreams().Count;
        WatchTogether.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WatchTogetherText.Text = count switch
        {
            1 => "Watch in multi-view",
            > MaxTogether => $"Watch first {MaxTogether} together",
            _ => $"Watch {count} together",
        };
    }

    private void WatchTogether_Click(object sender, RoutedEventArgs e)
    {
        var streams = SelectedStreams();
        if (streams.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Tick live events or event channels first (upcoming ones can't play yet).");
            return;
        }
        MultiViewPage.Show(streams.Take(MaxTogether).ToList());
    }

    // ---- Input ----

    private void Search_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _ = RunAsync();
            e.Handled = true;
        }
    }

    private void When_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (QueryBox is not null && QueryBox.Text.Trim().Length > 0)
            _ = RunAsync();
    }
}
