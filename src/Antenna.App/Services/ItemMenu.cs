using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Antenna.App.Services;

/// <summary>The right-click menu for channel/movie/series tiles, shared by every list.</summary>
internal static class ItemMenu
{
    /// <summary>Raised after something changes what lists should show (hide/lock a category).</summary>
    public static event Action? LibraryChanged;

    /// <summary>Hook for a list: shows the menu for the tile under the pointer.</summary>
    public static void OnRightTapped(object sender, RightTappedRoutedEventArgs e, IReadOnlyList<ItemViewModel>? siblings = null)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ItemViewModel vm || sender is not UIElement target)
            return;
        Build(vm, siblings).ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
        e.Handled = true;
    }

    public static MenuFlyout Build(ItemViewModel vm, IReadOnlyList<ItemViewModel>? siblings)
    {
        var item = vm.Item;
        var menu = new MenuFlyout();

        menu.Items.Add(Item(item.Kind == MediaKind.Live ? "Watch" : "Open", "", () => Playback.Open(vm, siblings)));
        if (item.Kind == MediaKind.Movie)
            menu.Items.Add(Item("Play now", "", () => Playback.PlayMovie(item)));

        menu.Items.Add(Item(vm.IsFavourite ? "Remove from favourites" : "Add to favourites", vm.IsFavourite ? "" : "", () =>
        {
            var on = !vm.IsFavourite;
            AppServices.Library.SetFavourite(item, on);
            vm.IsFavourite = on;
        }));

        if (item.Kind == MediaKind.Live && item.StreamUrl is { } live)
        {
            menu.Items.Add(Item("Add to multi-view", "\uF0E2", () => Views.MultiViewPage.Add(PlayRequest.ForItem(item))));
            menu.Items.Add(Item("Record now (until stopped, max 4 h)", "\uE7C8", () =>
            {
                Recorder.RecordNow(item.Title, live, item.Options, TimeSpan.FromHours(4));
                App.Window.ShowNotice(InfoBarSeverity.Success, $"Recording {item.Title}. Stop it from Recordings or the player.");
            }));
        }
        if (item.Kind == MediaKind.Movie && item.StreamUrl is { } file)
        {
            menu.Items.Add(Item("Download", "\uE896", () =>
            {
                Recorder.Download(item.Title, file, item.Options);
                App.Window.ShowNotice(InfoBarSeverity.Informational, $"Downloading {item.Title}. See Recordings.");
            }));
        }

        if (item.Group.Length > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item($"Hide category “{item.Group}”", "", () =>
            {
                AppServices.Library.SetGroupFlag(item.Kind, item.Group, GroupFlag.Hidden, true);
                LibraryChanged?.Invoke();
            }));
            if (Parental.IsPinSet)
            {
                menu.Items.Add(Item($"Lock category “{item.Group}”", "", () =>
                {
                    AppServices.Library.SetGroupFlag(item.Kind, item.Group, GroupFlag.Locked, true);
                    Parental.Apply();
                    LibraryChanged?.Invoke();
                }));
            }
        }
        return menu;
    }

    public static MenuFlyoutItem Item(string text, string glyph, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => onClick();
        return item;
    }
}
