using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using GroupItem = Antenna.App.ViewModels.GroupItem;

namespace Antenna.App.Views;

/// <summary>
/// Category list on the left, tiles on the right. Used for Live TV, Movies and Series.
/// Tiles load a page at a time as you scroll, off the UI thread.
/// </summary>
public sealed partial class BrowsePage : Page
{
    private MediaKind _kind;
    private string? _initialGroup;
    private IReadOnlyList<PlaylistSource> _sources = [];
    private bool _loading;
    private int _loadVersion;

    public BrowsePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        ItemMenu.LibraryChanged += () => { if (IsLoaded) LoadGroups(); };
        Parental.Changed += () => { if (IsLoaded) LoadGroups(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is BrowseTarget target)
        {
            _kind = target.Kind;
            _initialGroup = target.Group;
        }
        else
        {
            _kind = (MediaKind)e.Parameter;
        }
        Header.Text = _kind switch { MediaKind.Live => "Live TV", MediaKind.Movie => "Movies", _ => "Series" };
        Items.ItemTemplate = ItemTemplates.Get(_kind == MediaKind.Live ? "ItemTileTemplate" : "PosterTileTemplate");
        LoadSources();
    }

    /// <summary>Index 0 of the filter is "All playlists"; the rest map onto <see cref="_sources"/>.</summary>
    private string? SelectedSourceId => SourceFilter.SelectedIndex > 0 ? _sources[SourceFilter.SelectedIndex - 1].Id : null;

    private void LoadSources()
    {
        _loading = true;
        var previous = SelectedSourceId;
        _sources = AppServices.Library.GetSources();
        SourceFilter.ItemsSource = new[] { "All playlists" }.Concat(_sources.Select(s => s.Name)).ToList();
        var index = _sources.ToList().FindIndex(s => s.Id == previous);
        SourceFilter.SelectedIndex = index + 1; // -1 (not found) becomes 0, "All playlists"
        _loading = false;
        LoadGroups();
    }

    private void LoadGroups()
    {
        var previous = _initialGroup is { } initial ? (initial.Length == 0 ? GroupItem.Uncategorised : initial) : (Groups.SelectedItem as GroupItem)?.Name;
        _initialGroup = null;
        HashSet<string> Flagged(GroupFlag flag) => AppServices.Library.GetFlaggedGroups(flag)
            .Where(g => g.Kind == _kind).Select(g => g.Group).ToHashSet();
        var locked = Flagged(GroupFlag.Locked);
        var pinned = Flagged(GroupFlag.Pinned);

        var groups = new List<GroupItem> { new("All", false, IsAll: true) };
        groups.AddRange(AppServices.Library.GetGroups(_kind, SelectedSourceId)
            .Select(g => new GroupItem(g.Length == 0 ? GroupItem.Uncategorised : g, locked.Contains(g), Pinned: pinned.Contains(g))));
        Groups.ItemsSource = groups;
        Groups.SelectedItem = groups.FirstOrDefault(g => g.Name == previous) ?? groups[0];
    }

    private void LoadItems()
    {
        if (Groups.SelectedItem is not GroupItem group)
            return;

        var version = ++_loadVersion;
        EmptyState.Visibility = Visibility.Collapsed;
        UnlockButton.Visibility = Visibility.Collapsed;

        if (group.Locked && Parental.IsLocked)
        {
            Items.ItemsSource = null;
            ShowEmpty("This category is locked", "Enter the parental PIN to see it.", showUnlock: true);
            return;
        }

        var kind = _kind;
        var source = SelectedSourceId;
        var filter = group.Filter;
        Busy.IsActive = true;

        var list = new IncrementalList<ItemViewModel>(async (offset, count) =>
        {
            var entries = await Task.Run(() => AppServices.Library.GetItems(kind, source, filter, count, offset));
            return await GuideService.ToViewModelsAsync(entries, offset + 1);
        });
        list.PageLoaded += any =>
        {
            if (version != _loadVersion)
                return;
            Busy.IsActive = false;
            if (!any)
                ShowEmpty("Nothing here yet", "Add an M3U playlist or Xtream account in Playlists.", showUnlock: false);
        };
        Items.ItemsSource = list;
    }

    private void ShowEmpty(string title, string detail, bool showUnlock)
    {
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        UnlockButton.Visibility = showUnlock ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (await Parental.EnsureUnlockedAsync(XamlRoot))
            LoadItems();
    }

    private void SourceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
            LoadGroups();
    }

    private void Groups_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Groups.SelectedItem is not null)
            LoadItems();
    }

    private IReadOnlyList<ItemViewModel> LoadedItems => (Items.ItemsSource as IEnumerable<ItemViewModel>)?.ToList() ?? [];

    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemViewModel vm)
            Playback.Open(vm, LoadedItems);
    }

    private void Items_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
        ItemMenu.OnRightTapped(sender, e, LoadedItems);

    /// <summary>Category menu: hide, lock or unlock.</summary>
    private void Groups_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not GroupItem { IsAll: false } group)
            return;
        var name = group.Filter ?? "";
        var menu = new MenuFlyout();
        menu.Items.Add(ItemMenu.Item(group.Pinned ? "Unpin" : "Pin to top", group.Pinned ? "\uE77A" : "\uE718", () =>
        {
            AppServices.Library.SetGroupFlag(_kind, name, GroupFlag.Pinned, !group.Pinned);
            LoadGroups();
        }));
        menu.Items.Add(ItemMenu.Item("Hide category", "", () =>
        {
            AppServices.Library.SetGroupFlag(_kind, name, GroupFlag.Hidden, true);
            LoadGroups();
        }));
        if (group.Locked)
        {
            menu.Items.Add(ItemMenu.Item("Unlock category (remove lock)", "", async () =>
            {
                if (await Parental.EnsureUnlockedAsync(XamlRoot))
                {
                    AppServices.Library.SetGroupFlag(_kind, name, GroupFlag.Locked, false);
                    Parental.Apply();
                    LoadGroups();
                }
            }));
        }
        else
        {
            menu.Items.Add(ItemMenu.Item(Parental.IsPinSet ? "Lock category" : "Lock category (set a PIN in Settings first)", "", () =>
            {
                if (!Parental.IsPinSet)
                    return;
                AppServices.Library.SetGroupFlag(_kind, name, GroupFlag.Locked, true);
                Parental.Apply();
                LoadGroups();
            }));
        }
        menu.ShowAt((UIElement)sender, new FlyoutShowOptions { Position = e.GetPosition((UIElement)sender) });
        e.Handled = true;
    }
}
