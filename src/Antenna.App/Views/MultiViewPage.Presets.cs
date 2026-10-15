using Antenna.App.Services;
using Antenna.Core;
using Antenna.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Antenna.App.Views;

/// <summary>Saved multi-views ("Saturday football"): the channels in each tile, which has sound, and the layout.</summary>
public sealed partial class MultiViewPage
{
    private static IReadOnlyList<MultiViewPreset> LoadPresets() =>
        Antenna.Core.Library.MultiViewPresets.Parse(AppSettings.MultiViewPresets);

    private static void StorePresets(IReadOnlyList<MultiViewPreset> presets) =>
        AppSettings.MultiViewPresets = Antenna.Core.Library.MultiViewPresets.Serialize(presets);

    private void PresetsMenu_Opening(object sender, object e)
    {
        PresetsMenu.Items.Clear();
        var presets = LoadPresets();
        foreach (var preset in presets)
        {
            var open = new MenuFlyoutItem
            {
                Text = $"{preset.Name}  ·  {preset.Tiles.Count(t => t is not null)} channels",
                Icon = new FontIcon { Glyph = "\uE768" },
            };
            open.Click += (_, _) => OpenPreset(preset);
            PresetsMenu.Items.Add(open);
        }
        if (presets.Count > 0)
            PresetsMenu.Items.Add(new MenuFlyoutSeparator());

        var save = new MenuFlyoutItem { Text = "Save these channels as a preset…", Icon = new FontIcon { Glyph = "\uE74E" } };
        save.IsEnabled = Chosen.Any(c => c?.SourceId is not null);
        save.Click += async (_, _) => await SavePresetAsync();
        PresetsMenu.Items.Add(save);

        if (presets.Count > 0)
        {
            var delete = new MenuFlyoutSubItem { Text = "Delete a preset", Icon = new FontIcon { Glyph = "\uE74D" } };
            foreach (var preset in presets)
            {
                var item = new MenuFlyoutItem { Text = preset.Name };
                item.Click += (_, _) =>
                {
                    StorePresets(Antenna.Core.Library.MultiViewPresets.Delete(LoadPresets(), preset.Name));
                    Log.Info($"multi-view: deleted preset '{preset.Name}'");
                };
                delete.Items.Add(item);
            }
            PresetsMenu.Items.Add(delete);
        }
    }

    private async Task SavePresetAsync()
    {
        var name = new TextBox { PlaceholderText = "e.g. Saturday football", Text = SuggestName() };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Save as a preset",
            Content = name,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        name.SelectAll();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || name.Text.Trim() is not { Length: > 0 } title)
            return;

        var tiles = Chosen.Select(c => c is { SourceId: { } source } && (c.Item?.ExternalId ?? c.HistoryId) is { } id
            ? new PresetTile(source, id, c.Title)
            : null).ToList();
        StorePresets(Antenna.Core.Library.MultiViewPresets.Save(LoadPresets(), new MultiViewPreset(title, _layout, _mainSlot, tiles)));
        Log.Info($"multi-view: saved preset '{title}' ({tiles.Count(t => t is not null)} channels)");
        LimitBar.Severity = InfoBarSeverity.Success;
        LimitBar.Title = "Preset saved";
        LimitBar.Message = $"Open \"{title}\" any time from Presets.";
        LimitBar.IsOpen = true;
    }

    private string SuggestName() =>
        Chosen.FirstOrDefault(c => c is not null)?.Title is { } first ? $"{first} + {Chosen.Count(c => c is not null) - 1} more" : "";

    /// <summary>Fills the tiles from a preset. Channels no longer in any playlist are left empty.</summary>
    private void OpenPreset(MultiViewPreset preset)
    {
        var missing = 0;
        for (var i = 0; i < SlotCount; i++)
        {
            var tile = i < preset.Tiles.Count ? preset.Tiles[i] : null;
            PlayRequest? request = null;
            if (tile is not null)
            {
                if (AppServices.Library.GetItem(tile.SourceId, tile.ChannelId, MediaKind.Live) is { } entry)
                    request = ForTile(PlayRequest.ForItem(entry.Item));
                else
                    missing++;
            }
            Chosen[i] = request;
        }
        _mainSlot = Math.Clamp(preset.MainTile, 0, SlotCount - 1);
        _layout = preset.Layout is "spot" or "grid" or "focus" ? preset.Layout : "spot";
        foreach (var item in LayoutChoice.Items)
            item.IsSelected = (item.Tag as string) == _layout;

        foreach (var slot in _slots)
        {
            slot.Retry = 0;
            slot.Drops.Clear();
            Play(slot);
        }
        ApplyLayout();
        _ = CheckLimitsAsync();
        Log.Info($"multi-view: opened preset '{preset.Name}'{(missing > 0 ? $", {missing} channel(s) missing" : "")}");
        if (missing > 0)
        {
            LimitBar.Severity = InfoBarSeverity.Warning;
            LimitBar.Title = "Some channels are gone";
            LimitBar.Message = $"{missing} channel(s) in \"{preset.Name}\" are no longer in your playlists.";
            LimitBar.IsOpen = true;
        }
    }
}
