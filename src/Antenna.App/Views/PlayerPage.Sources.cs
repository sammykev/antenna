using Antenna.App.Services;
using Antenna.Core;
using Microsoft.UI.Xaml.Controls;

namespace Antenna.App.Views;

/// <summary>
/// The same channel in other playlists: switch to it from the menu, or automatically when the current
/// playlist's stream keeps failing. Zapping still follows the list the channel was opened from.
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>Playlists already tried for this channel, so automatic switching doesn't go round in circles.</summary>
    private readonly HashSet<string> _triedSources = [];

    private IReadOnlyList<LibraryEntry> OtherSources()
    {
        if (_request is not { IsLive: true, Item: { } item })
            return [];
        try
        {
            return AppServices.Library.FindSameChannel(item);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "player: other sources");
            return [];
        }
    }

    private bool TrySwitchSource(bool automatic)
    {
        if (_request is not { Item: { } current } request)
            return false;
        _triedSources.Add(current.SourceId);
        var next = OtherSources().FirstOrDefault(e => !_triedSources.Contains(e.Item.SourceId));
        if (next is null)
            return false;
        SwitchTo(request, next, automatic);
        return true;
    }

    private void SwitchTo(PlayRequest from, LibraryEntry to, bool automatic)
    {
        Log.Info($"player: {(automatic ? "kept failing, switching" : "switching")} to '{to.Item.Title}' on {to.SourceName}");
        // Keep the original channel list so Ch+/Ch− carries on through it.
        var request = PlayRequest.ForItem(to.Item) with { Channels = from.Channels, ChannelIndex = from.ChannelIndex };
        Load(request, switchingSource: true);
        ShowStatusBriefly(automatic
            ? $"That stream kept failing, so this is {to.Item.Title} from {to.SourceName}"
            : $"Watching on {to.SourceName}");
    }

    /// <summary>"Watch on another playlist" entries for the Picture and sound menu.</summary>
    private void AddSourceItems(MenuFlyout menu)
    {
        if (_request is not { } request)
            return;
        var others = OtherSources();
        if (others.Count == 0)
            return;
        var sub = new MenuFlyoutSubItem { Text = "Watch on another playlist" };
        foreach (var other in others)
        {
            var item = new MenuFlyoutItem { Text = $"{other.Item.Title} · {other.SourceName}" };
            item.Click += (_, _) => SwitchTo(request, other, automatic: false);
            sub.Items.Add(item);
        }
        menu.Items.Insert(0, sub);
        menu.Items.Insert(1, new MenuFlyoutSeparator());
    }
}
