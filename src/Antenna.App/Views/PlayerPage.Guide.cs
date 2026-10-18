using Antenna.App.Services;
using Antenna.Core;
using Antenna.Core.Epg;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Antenna.App.Views;

/// <summary>
/// The guide for the channel being watched, in a panel over the video: earlier today (replayable where
/// the provider archives it), what's on now, and what's coming up (click to set a reminder).
/// Opens by itself for a few seconds when a channel starts (Settings), or any time with G.
/// </summary>
public sealed partial class PlayerPage
{
    private static readonly TimeSpan GuideBack = TimeSpan.FromHours(6);
    private static readonly TimeSpan GuideAhead = TimeSpan.FromHours(24);
    private static readonly TimeSpan GuideAutoHide = TimeSpan.FromSeconds(8);

    private DispatcherQueueTimer? _guideHide;
    private PlayRequest? _guideFor;

    /// <summary>One programme in the panel; ToString is what the list shows.</summary>
    private sealed record GuideRow(Programme Programme, bool IsLive, bool CanReplay, bool Reminder)
    {
        public override string ToString()
        {
            var time = $"{Programme.Start.LocalDateTime:HH:mm}";
            var day = Programme.Start.LocalDateTime.Date == DateTime.Today ? "" : $"{Programme.Start.LocalDateTime:ddd} ";
            var mark = IsLive ? "● NOW  " : CanReplay ? "▶  " : Reminder ? "🔔  " : "";
            return $"{day}{time}   {mark}{Programme.Title}";
        }
    }

    private bool GuideAvailable => _request is { IsLive: true, SourceId: not null, EpgChannelId: not null };

    /// <summary>Called when a channel loads: shows the Guide button, and the panel briefly if that's switched on.</summary>
    private void OnChannelLoadedForGuide()
    {
        GuideButton.Visibility = GuideAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (!GuideAvailable)
        {
            GuidePanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (GuidePanel.Visibility == Visibility.Visible)
            _ = LoadGuideAsync();
        else if (AppSettings.ShowGuideOnChannelStart)
            _ = OpenGuideAsync(autoHide: true);
    }

    private async Task OpenGuideAsync(bool autoHide)
    {
        if (!GuideAvailable)
            return;
        GuidePanel.Visibility = Visibility.Visible;
        await LoadGuideAsync();
        _guideHide ??= CreateGuideHideTimer();
        _guideHide.Stop();
        if (autoHide)
            _guideHide.Start();
    }

    private DispatcherQueueTimer CreateGuideHideTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = GuideAutoHide;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => GuidePanel.Visibility = Visibility.Collapsed;
        return timer;
    }

    private async Task LoadGuideAsync()
    {
        if (_request is not { SourceId: { } source, EpgChannelId: { } channel, Item: var item } request)
            return;
        _guideFor = request;
        GuideChannel.Text = request.Title;
        var now = DateTimeOffset.Now;
        var from = now - GuideBack;
        var programmes = await Task.Run(() => AppServices.Library.GetProgrammes(source, channel, from, now + GuideAhead));
        if (!ReferenceEquals(request, _request))
            return;
        var rows = programmes.Select(p => new GuideRow(
            p,
            p.IsOnAt(now),
            item is not null && !p.IsOnAt(now) && Catchup.IsAvailable(item, p, now),
            p.Start > now && item is not null && Reminders.IsSet(item.SourceId, item.ExternalId, p))).ToList();
        GuideList.ItemsSource = rows;
        GuideEmpty.Text = "The guide has nothing for this channel right now.";
        GuideEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.FirstOrDefault(r => r.IsLive) is { } live)
            GuideList.ScrollIntoView(live, ScrollIntoViewAlignment.Leading);
    }

    private async void GuideList_ItemClick(object sender, ItemClickEventArgs e)
    {
        _guideHide?.Stop();
        if (e.ClickedItem is not GuideRow row || _guideFor is not { Item: { } channel } request)
            return;
        if (row.IsLive)
        {
            GuidePanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (row.CanReplay)
        {
            try
            {
                if (await GuideService.ResolveCatchupUrlAsync(channel, row.Programme) is { } url)
                    Load(PlayRequest.ForCatchup(channel, row.Programme, url));
                else
                    ShowStatusBriefly("This playlist doesn't say how to replay programmes.");
            }
            catch (Exception ex)
            {
                ShowStatusBriefly($"Couldn't start catch-up: {ex.Message}");
            }
            return;
        }
        if (row.Programme.Start > DateTimeOffset.Now)
        {
            var on = Reminders.Toggle(channel, row.Programme);
            ShowStatusBriefly(on ? $"I'll remind you before {row.Programme.Title} starts." : "Reminder cancelled.");
            if (ReferenceEquals(request, _request))
                await LoadGuideAsync();
        }
    }

    private void ToggleGuide()
    {
        if (GuidePanel.Visibility == Visibility.Visible)
            GuidePanel.Visibility = Visibility.Collapsed;
        else
            _ = OpenGuideAsync(autoHide: false);
    }

    private void Guide_Click(object sender, RoutedEventArgs e) => ToggleGuide();

    private void GuideClose_Click(object sender, RoutedEventArgs e) => GuidePanel.Visibility = Visibility.Collapsed;

    /// <summary>Once the pointer is on the panel, it stays until closed.</summary>
    private void GuidePanel_PointerEntered(object sender, PointerRoutedEventArgs e) => _guideHide?.Stop();

    private void ToggleGuide_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, ToggleGuide);
}
