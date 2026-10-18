using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Antenna.Core.Epg;
using Antenna.Core.Recordings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

public sealed partial class GuidePage : Page
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromHours(3);
    private static readonly TimeSpan WindowStep = TimeSpan.FromMinutes(90);

    private IReadOnlyList<PlaylistSource> _sources = [];
    private List<ItemViewModel> _allChannels = [];
    private List<DateTime> _days = [];
    private DateTimeOffset _windowStart;
    private bool _loading;

    public GuidePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        _windowStart = DefaultWindowStart();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadSourcesAsync();
    }

    private bool IsTimeline => (ViewMode.SelectedItem?.Tag as string) == "grid";

    private ItemViewModel? SelectedChannel => Channels.SelectedItem as ItemViewModel;

    /// <summary>Search results mix channels: which channel each row belongs to.</summary>
    private Dictionary<ProgrammeViewModel, ItemViewModel> _searchChannels = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchTimer;
    private int _searchVersion;

    private bool Searching => !string.IsNullOrWhiteSpace(ProgrammeSearch.Text);

    private ItemViewModel? ChannelFor(ProgrammeViewModel row) =>
        _searchChannels.TryGetValue(row, out var channel) ? channel : SelectedChannel;

    private string? SelectedSourceId => SourceFilter.SelectedIndex > 0 ? _sources[SourceFilter.SelectedIndex - 1].Id : null;

    private static DateTimeOffset DefaultWindowStart()
    {
        // Start half an hour back, on a half-hour boundary, so "now" sits near the left.
        var now = DateTimeOffset.Now;
        var rounded = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute >= 30 ? 30 : 0, 0, now.Offset);
        return rounded.AddMinutes(-30);
    }

    private async Task LoadSourcesAsync()
    {
        _loading = true;
        var previous = SelectedSourceId;
        _sources = AppServices.Library.GetSources();
        SourceFilter.ItemsSource = new[] { "All playlists" }.Concat(_sources.Select(s => s.Name)).ToList();
        SourceFilter.SelectedIndex = _sources.ToList().FindIndex(s => s.Id == previous) + 1;
        _loading = false;
        await LoadChannelsAsync();
    }

    private async Task LoadChannelsAsync()
    {
        var previous = SelectedChannel?.Item;
        var source = SelectedSourceId;
        var entries = await Task.Run(() => AppServices.Library.GetItems(MediaKind.Live, source));
        _allChannels = await GuideService.ToViewModelsAsync(entries);
        ApplyChannelFilter();

        var keep = previous is null ? null
            : _allChannels.FirstOrDefault(c => c.Item.SourceId == previous.SourceId && c.Item.ExternalId == previous.ExternalId);
        Channels.SelectedItem = keep ?? (Channels.ItemsSource as List<ItemViewModel>)?.FirstOrDefault(c => c.Item.EpgChannelId is not null);
        if (Channels.SelectedItem is null && !IsTimeline)
            ShowEmpty("No guide yet", "Add an EPG (XMLTV) URL to a playlist, or refresh it in Playlists. Xtream accounts include one automatically.");
        ShowMode();
    }

    private List<ItemViewModel> FilteredChannels()
    {
        var text = ChannelFilter.Text.Trim();
        return text.Length == 0 ? _allChannels : _allChannels.Where(c => c.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void ApplyChannelFilter()
    {
        var filtered = FilteredChannels();
        Channels.ItemsSource = filtered;
        if (IsTimeline)
            TimelineRows.ItemsSource = filtered.Where(c => c.Item.EpgChannelId is not null).ToList();
    }

    private void ShowMode()
    {
        var grid = IsTimeline;
        foreach (var e in new UIElement[] { Channels, ListHeader, Schedule })
            e.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        TimelineHeader.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        Timeline.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        if (grid)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            BuildRuler();
            TimelineRows.ItemsSource = FilteredChannels().Where(c => c.Item.EpgChannelId is not null).ToList();
        }
    }

    // ---- List view ----

    private void ShowChannel(ItemViewModel channel)
    {
        ChannelTitle.Text = channel.Title;

        // Offer each day back to the start of the archive, and a couple of days ahead.
        var today = DateTime.Today;
        var back = Math.Clamp(channel.Item.CatchupDays, 0, 14);
        _days = Enumerable.Range(-back, back + 3).Select(i => today.AddDays(i)).ToList();

        _loading = true;
        Days.ItemsSource = _days.Select(DayLabel).ToList();
        Days.SelectedIndex = back; // today
        _loading = false;
        _ = LoadScheduleAsync();
    }

    private async Task LoadScheduleAsync()
    {
        Status.IsOpen = false;
        if (SelectedChannel is not { } channel || Days.SelectedIndex < 0)
            return;

        if (channel.Item.EpgChannelId is not { } epgId)
        {
            Schedule.ItemsSource = null;
            ShowEmpty("No guide for this channel", "The guide didn't list this channel by id or by name.");
            return;
        }

        var from = new DateTimeOffset(_days[Days.SelectedIndex]);
        var programmes = await Task.Run(() => AppServices.Library.GetProgrammes(channel.Item.SourceId, epgId, from, from.AddDays(1)));
        if (!ReferenceEquals(channel, SelectedChannel))
            return;

        var now = DateTimeOffset.Now;
        var rows = programmes.Select(p => Row(channel.Item, p, now)).ToList();
        Schedule.ItemsSource = rows;
        if (rows.Count == 0)
        {
            ShowEmpty("Nothing scheduled", "The guide has no programmes for this channel on this day.");
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        if (rows.FirstOrDefault(r => r.IsLive) is { } live)
            Schedule.ScrollIntoView(live, ScrollIntoViewAlignment.Leading);
    }

    private static ProgrammeViewModel Row(MediaItem channel, Programme p, DateTimeOffset now) =>
        new(p, p.IsOnAt(now), Catchup.IsAvailable(channel, p, now) && !p.IsOnAt(now));

    private void ShowEmpty(string title, string detail)
    {
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        EmptyState.Visibility = Visibility.Visible;
    }

    private static string DayLabel(DateTime day) => (day - DateTime.Today).Days switch
    {
        0 => "Today",
        -1 => "Yesterday",
        1 => "Tomorrow",
        _ => day.ToString("ddd d MMM"),
    };

    // ---- Timeline view ----

    private void BuildRuler()
    {
        Ruler.Children.Clear();
        Ruler.ColumnDefinitions.Clear();
        var slots = (int)(WindowLength / TimeSpan.FromMinutes(30));
        for (var i = 0; i < slots; i++)
        {
            Ruler.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock { Text = _windowStart.AddMinutes(30 * i).LocalDateTime.ToString("HH:mm"), Opacity = 0.7, Margin = new Thickness(4, 0, 0, 0) };
            Grid.SetColumn(label, i);
            Ruler.Children.Add(label);
        }
        WindowText.Text = $"{_windowStart.LocalDateTime:ddd d MMM, HH:mm} – {(_windowStart + WindowLength).LocalDateTime:HH:mm}";
    }

    /// <summary>Fills a channel row's lane with programmes sized by their share of the window.</summary>
    private void TimelineRows_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ItemViewModel channel || args.ItemContainer.ContentTemplateRoot is not Grid root)
            return;
        if (root.FindName("Lane") is not Grid lane)
            return;

        lane.Children.Clear();
        lane.ColumnDefinitions.Clear();
        if (channel.Item.EpgChannelId is not { } epgId)
            return;

        var start = _windowStart;
        var end = _windowStart + WindowLength;
        var programmes = AppServices.Library.GetProgrammes(channel.Item.SourceId, epgId, start, end);
        var now = DateTimeOffset.Now;
        var cursor = start;
        var column = 0;

        void AddColumn(TimeSpan span)
        {
            lane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(span.TotalMinutes, 0.5), GridUnitType.Star) });
        }

        foreach (var p in programmes)
        {
            var from = p.Start < start ? start : p.Start;
            var to = p.Stop > end ? end : p.Stop;
            if (to <= cursor)
                continue;
            if (from > cursor)
            {
                AddColumn(from - cursor);
                column++;
            }
            AddColumn(to - (from > cursor ? from : cursor));
            var row = Row(channel.Item, p, now);
            var cell = Cell(channel, row);
            Grid.SetColumn(cell, column++);
            lane.Children.Add(cell);
            cursor = to;
        }
        if (cursor < end)
            AddColumn(end - cursor);
    }

    private Button Cell(ItemViewModel channel, ProgrammeViewModel row)
    {
        var text = new StackPanel { Spacing = 0 };
        text.Children.Add(new TextBlock { Text = row.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = row.Time + (row.State.Length > 0 ? "  " + row.State : ""), FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        var cell = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
            Opacity = row.Opacity,
        };
        if (row.IsLive)
            cell.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        ToolTipService.SetToolTip(cell, string.IsNullOrEmpty(row.Description) ? row.Title : $"{row.Title}\n{row.Description}");
        cell.Click += (_, _) => _ = ActivateAsync(channel, row);
        cell.RightTapped += (s, e) =>
        {
            ProgrammeMenu(channel, row).ShowAt((UIElement)s, new FlyoutShowOptions { Position = e.GetPosition((UIElement)s) });
            e.Handled = true;
        };
        return cell;
    }

    private void ShiftWindow(TimeSpan by)
    {
        _windowStart += by;
        BuildRuler();
        // Re-realize rows so lanes redraw for the new window.
        var rows = TimelineRows.ItemsSource;
        TimelineRows.ItemsSource = null;
        TimelineRows.ItemsSource = rows;
    }

    private void Earlier_Click(object sender, RoutedEventArgs e) => ShiftWindow(-WindowStep);
    private void Later_Click(object sender, RoutedEventArgs e) => ShiftWindow(WindowStep);
    private void Now_Click(object sender, RoutedEventArgs e) => ShiftWindow(DefaultWindowStart() - _windowStart);

    // ---- Actions (both views) ----

    private async Task ActivateAsync(ItemViewModel channel, ProgrammeViewModel row)
    {
        if (row.IsLive)
        {
            Playback.Open(channel, FilteredChannels());
            return;
        }
        if (!row.CanReplay)
        {
            ShowStatus(InfoBarSeverity.Informational, row.Programme.Start > DateTimeOffset.Now
                ? "Right-click to set a reminder or record this when it airs."
                : "This programme isn't in the provider's catch-up archive.");
            return;
        }

        try
        {
            var url = await GuideService.ResolveCatchupUrlAsync(channel.Item, row.Programme);
            if (url is null)
                ShowStatus(InfoBarSeverity.Warning, "This playlist doesn't say how to replay programmes for this channel.");
            else
                Playback.Play(PlayRequest.ForCatchup(channel.Item, row.Programme, url));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, $"Couldn't start catch-up: {ex.Message}");
        }
    }

    private MenuFlyout ProgrammeMenu(ItemViewModel channel, ProgrammeViewModel row)
    {
        var p = row.Programme;
        var menu = new MenuFlyout();
        if (row.IsLive || row.CanReplay)
            menu.Items.Add(ItemMenu.Item(row.IsLive ? "Watch live" : "Play catch-up", "", () => _ = ActivateAsync(channel, row)));

        var title = $"{channel.Title} - {p.Title}";
        if (p.Start > DateTimeOffset.Now)
        {
            var isSet = Reminders.IsSet(channel.Item.SourceId, channel.Item.ExternalId, p);
            menu.Items.Add(ItemMenu.Item(isSet ? "Cancel reminder" : "Remind me", isSet ? "\uE7ED" : "\uEA8F", () =>
            {
                var on = Reminders.Toggle(channel.Item, p);
                ShowStatus(InfoBarSeverity.Success, on
                    ? $"I'll remind you at {(p.Start - Antenna.Core.Recordings.ReminderPlanner.Lead).LocalDateTime:ddd HH:mm}. Keep Antenna open."
                    : "Reminder cancelled.");
            }));
        }
        if (p.Stop > DateTimeOffset.Now && channel.Item.StreamUrl is { } live)
        {
            menu.Items.Add(ItemMenu.Item(row.IsLive ? "Record the rest of this programme" : "Record when it airs", "", () =>
            {
                var (start, stop) = RecordingPlanner.Window(p);
                Recorder.Schedule(title, live, channel.Item.Options, start, stop);
                ShowStatus(InfoBarSeverity.Success, row.IsLive
                    ? "Recording now. See Recordings."
                    : $"Scheduled for {p.Start.LocalDateTime:ddd HH:mm}. Keep Antenna open to record.");
            }));
        }
        else if (row.CanReplay)
        {
            menu.Items.Add(ItemMenu.Item("Save catch-up to disk", "", async () =>
            {
                try
                {
                    if (await GuideService.ResolveCatchupUrlAsync(channel.Item, p) is { } url)
                    {
                        var now = DateTimeOffset.Now;
                        Recorder.Schedule(title, url, channel.Item.Options, now, now + p.Duration + RecordingPlanner.PaddingAfter);
                        ShowStatus(InfoBarSeverity.Success, "Saving in the background. See Recordings.");
                    }
                }
                catch (Exception ex)
                {
                    ShowStatus(InfoBarSeverity.Error, ex.Message);
                }
            }));
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = "Nothing to do for this programme", IsEnabled = false });
        return menu;
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }

    // ---- Events ----

    private void ViewMode_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Timeline is not null) // Also fires during InitializeComponent.
            ShowMode();
    }

    private void SourceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
            _ = LoadChannelsAsync();
    }

    private void ChannelFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyChannelFilter();

    private void Channels_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedChannel is not { } channel)
            return;
        // Picking a channel leaves the search and shows its day.
        if (Searching)
        {
            ProgrammeSearch.Text = "";
            EndSearch();
        }
        ShowChannel(channel);
    }

    // ---- Programme search ----

    private void ProgrammeSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        if (_searchTimer is null)
        {
            _searchTimer = DispatcherQueue.CreateTimer();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
            _searchTimer.IsRepeating = false;
            _searchTimer.Tick += (_, _) => _ = SearchAsync();
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ProgrammeSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchTimer?.Stop();
        _ = SearchAsync();
    }

    /// <summary>Programmes matching the search on any channel: the last week (for catch-up) and the next one.</summary>
    private async Task SearchAsync()
    {
        var query = ProgrammeSearch.Text.Trim();
        if (query.Length == 0)
        {
            EndSearch();
            return;
        }
        if (IsTimeline)
            foreach (var item in ViewMode.Items)
                item.IsSelected = (item.Tag as string) == "list";

        var version = ++_searchVersion;
        var now = DateTimeOffset.Now;
        var source = SelectedSourceId;
        var hits = await Task.Run(() => AppServices.Library.SearchEvents(query, now.AddDays(-7), now.AddDays(7), 300, keepAllWords: true));
        if (version != _searchVersion)
            return;

        var rows = new List<ProgrammeViewModel>();
        var channels = new Dictionary<ProgrammeViewModel, ItemViewModel>();
        foreach (var hit in hits)
        {
            var entry = hit.Channels.FirstOrDefault(c => source is null || c.Item.SourceId == source);
            if (entry is null)
                continue;
            var p = hit.Programme;
            var row = new ProgrammeViewModel(p, p.IsOnAt(now), !p.IsOnAt(now) && Catchup.IsAvailable(entry.Item, p, now), entry.Item.Title);
            rows.Add(row);
            channels[row] = new ItemViewModel(entry);
        }
        // Now first, then what's coming up, then the past (most recent first).
        rows = [.. rows.Where(r => r.IsLive),
                .. rows.Where(r => !r.IsLive && r.Programme.Start > now).OrderBy(r => r.Programme.Start),
                .. rows.Where(r => !r.IsLive && r.Programme.Start <= now).OrderByDescending(r => r.Programme.Start)];

        _searchChannels = channels;
        ChannelTitle.Text = rows.Count == 0 ? $"No programmes match “{query}”" : $"{rows.Count} programme{(rows.Count == 1 ? "" : "s")} matching “{query}”";
        Days.Visibility = Visibility.Collapsed;
        Schedule.ItemsSource = rows;
        if (rows.Count == 0)
            ShowEmpty("No matches", "Try other words. The search covers the last week (for catch-up) and the next week.");
        else
            EmptyState.Visibility = Visibility.Collapsed;
    }

    private void EndSearch()
    {
        _searchVersion++;
        _searchChannels = [];
        Days.Visibility = Visibility.Visible;
        if (SelectedChannel is { } channel)
            ShowChannel(channel);
    }

    private void Days_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
            _ = LoadScheduleAsync();
    }

    private void Schedule_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProgrammeViewModel row && ChannelFor(row) is { } channel)
            _ = ActivateAsync(channel, row);
    }

    private void Schedule_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ProgrammeViewModel row || ChannelFor(row) is not { } channel)
            return;
        ProgrammeMenu(channel, row).ShowAt((UIElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
        e.Handled = true;
    }
}
