using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Antenna.Core.Epg;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

public sealed partial class GuidePage : Page
{
    private IReadOnlyList<PlaylistSource> _sources = [];
    private List<ItemViewModel> _allChannels = [];
    private List<DateTime> _days = [];
    private bool _loading;

    public GuidePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadSources();
    }

    private ItemViewModel? SelectedChannel => Channels.SelectedItem as ItemViewModel;

    private string? SelectedSourceId => SourceFilter.SelectedIndex > 0 ? _sources[SourceFilter.SelectedIndex - 1].Id : null;

    private void LoadSources()
    {
        _loading = true;
        var previous = SelectedSourceId;
        _sources = AppServices.Library.GetSources();
        SourceFilter.ItemsSource = new[] { "All playlists" }.Concat(_sources.Select(s => s.Name)).ToList();
        SourceFilter.SelectedIndex = _sources.ToList().FindIndex(s => s.Id == previous) + 1;
        _loading = false;
        LoadChannels();
    }

    private void LoadChannels()
    {
        var previous = SelectedChannel?.Item;
        _allChannels = GuideService.WithNowPlaying(AppServices.Library.GetItems(MediaKind.Live, SelectedSourceId));
        ApplyChannelFilter();

        var keep = previous is null ? null
            : _allChannels.FirstOrDefault(c => c.Item.SourceId == previous.SourceId && c.Item.ExternalId == previous.ExternalId);
        Channels.SelectedItem = keep ?? (Channels.ItemsSource as List<ItemViewModel>)?.FirstOrDefault(c => c.Item.EpgChannelId is not null);
        if (Channels.SelectedItem is null)
            ShowEmpty("No guide yet", "Add an EPG (XMLTV) URL to a playlist, or refresh it in Playlists. Xtream accounts include one automatically.");
    }

    private void ApplyChannelFilter()
    {
        var text = ChannelFilter.Text.Trim();
        Channels.ItemsSource = text.Length == 0
            ? _allChannels
            : _allChannels.Where(c => c.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

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
        LoadSchedule();
    }

    private void LoadSchedule()
    {
        Error.IsOpen = false;
        if (SelectedChannel is not { } channel || Days.SelectedIndex < 0)
            return;

        if (channel.Item.EpgChannelId is not { } epgId)
        {
            Schedule.ItemsSource = null;
            ShowEmpty("No guide for this channel", "The guide didn't list this channel by id or by name.");
            return;
        }

        var day = _days[Days.SelectedIndex];
        var from = new DateTimeOffset(day);
        var programmes = AppServices.Library.GetProgrammes(channel.Item.SourceId, epgId, from, from.AddDays(1));
        var now = DateTimeOffset.Now;
        var rows = programmes
            .Select(p => new ProgrammeViewModel(p, p.IsOnAt(now), Catchup.IsAvailable(channel.Item, p, now) && !p.IsOnAt(now)))
            .ToList();

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

    private void ShowEmpty(string title, string detail)
    {
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        EmptyState.Visibility = Visibility.Visible;
    }

    private static string DayLabel(DateTime day)
    {
        var diff = (day - DateTime.Today).Days;
        return diff switch
        {
            0 => "Today",
            -1 => "Yesterday",
            1 => "Tomorrow",
            _ => day.ToString("ddd d MMM"),
        };
    }

    private void SourceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
            LoadChannels();
    }

    private void ChannelFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyChannelFilter();

    private void Channels_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedChannel is { } channel)
            ShowChannel(channel);
    }

    private void Days_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
            LoadSchedule();
    }

    private async void Schedule_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ProgrammeViewModel row || SelectedChannel is not { } channel)
            return;

        if (row.IsLive && channel.Item.StreamUrl is { } live)
        {
            Frame.Navigate(typeof(PlayerPage), new PlayRequest(channel.Title, live, IsLive: true));
            return;
        }
        if (!row.CanReplay)
            return;

        try
        {
            var url = await GuideService.ResolveCatchupUrlAsync(channel.Item, row.Programme);
            if (url is null)
            {
                ShowError("This playlist doesn't say how to replay programmes for this channel.");
                return;
            }
            Frame.Navigate(typeof(PlayerPage), new PlayRequest($"{channel.Title} · {row.Title} (catch-up)", url, IsLive: false));
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't start catch-up: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        Error.Message = message;
        Error.IsOpen = true;
    }
}
