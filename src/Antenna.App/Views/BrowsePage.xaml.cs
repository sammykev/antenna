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
    private List<GroupItem> _allGroups = [];
    private bool _searchAllCategories;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchTimer;

    public BrowsePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        ItemMenu.LibraryChanged += () => { if (IsLoaded) LoadGroups(); };
        Parental.Changed += () => { if (IsLoaded) LoadGroups(); };
        // A playlist refresh saves live, movies and series separately: show each as it lands.
        AppServices.CatalogPartLoaded += (_, kind, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && kind == _kind && string.IsNullOrWhiteSpace(ItemSearch.Text))
                LoadGroups();
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var previousKind = _kind;
        if (e.Parameter is BrowseTarget target)
        {
            _kind = target.Kind;
            _initialGroup = target.Group;
        }
        else
        {
            _kind = (MediaKind)e.Parameter;
        }
        // One cached page serves all three types: don't carry a search or category filter across,
        // and don't let an old filter hide a category that was opened directly.
        if (_kind != previousKind || e.Parameter is BrowseTarget)
        {
            _searchTimer?.Stop();
            ItemSearch.Text = "";
            GroupFilter.Text = "";
            _searchAllCategories = false;
        }
        Header.Text = KindName;
        ItemSearch.PlaceholderText = $"Search {KindName}";
        Items.ItemTemplate = ItemTemplates.Get(_kind == MediaKind.Live ? "ItemTileTemplate" : "PosterTileTemplate");
        LoadSources();
    }

    private string KindName => _kind switch { MediaKind.Live => "Live TV", MediaKind.Movie => "Movies", _ => "Series" };

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
        _allGroups = groups;
        var shown = FilteredGroups();
        Groups.ItemsSource = shown;
        Groups.SelectedItem = shown.FirstOrDefault(g => g.Name == previous) ?? shown[0];
    }

    /// <summary>"All" plus the categories matching the "Find a category" box.</summary>
    private List<GroupItem> FilteredGroups()
    {
        var text = GroupFilter.Text;
        if (string.IsNullOrWhiteSpace(text))
            return _allGroups;
        return _allGroups.Where(g => g.IsAll || Antenna.Core.Library.TextMatch.ContainsAll(g.Name, text)).ToList();
    }

    private void GroupFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var selected = Groups.SelectedItem as GroupItem;
        var shown = FilteredGroups();
        Groups.ItemsSource = shown;
        // Keep the selection if it's still listed; otherwise show the best match.
        Groups.SelectedItem = shown.FirstOrDefault(g => ReferenceEquals(g, selected))
                              ?? (shown.Count > 1 && !string.IsNullOrWhiteSpace(GroupFilter.Text) ? shown[1] : shown[0]);
    }

    private void LoadItems()
    {
        if (Groups.SelectedItem is not GroupItem group)
            return;
        if (!string.IsNullOrWhiteSpace(ItemSearch.Text))
        {
            _ = SearchAsync();
            return;
        }
        ScopePanel.Visibility = Visibility.Collapsed;

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

        // Live TV across all playlists: each channel once (the first playlist's), the others are
        // offered in the player as "Watch on another playlist".
        var merge = kind == MediaKind.Live && source is null && _sources.Count > 1 && AppSettings.MergeDuplicateChannels;
        var pager = new Antenna.Core.Library.UniquePager<LibraryEntry>(
            (from, count) => Task.Run(() => AppServices.Library.GetItems(kind, source, filter, count, from)),
            e => Antenna.Core.Library.ChannelMatch.Key(e.Item.Title));

        var list = new IncrementalList<ItemViewModel>(async (offset, count) =>
        {
            var entries = merge
                ? await pager.NextAsync(count)
                : await Task.Run(() => AppServices.Library.GetItems(kind, source, filter, count, offset));
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
        _searchAllCategories = false;
        if (Groups.SelectedItem is not null)
            LoadItems();
    }

    // ---- Search within this type (and category) ----

    private void ItemSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        if (_searchTimer is null)
        {
            _searchTimer = DispatcherQueue.CreateTimer();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(250);
            _searchTimer.IsRepeating = false;
            _searchTimer.Tick += (_, _) => LoadItems();
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ItemSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchTimer?.Stop();
        LoadItems();
    }

    private void Widen_Click(object sender, RoutedEventArgs e)
    {
        _searchAllCategories = true;
        _ = SearchAsync();
    }

    /// <summary>Searches this type, within the selected category unless widened, and this playlist if one is picked.</summary>
    private async Task SearchAsync()
    {
        var query = ItemSearch.Text.Trim();
        var group = Groups.SelectedItem as GroupItem;
        var category = _searchAllCategories || group is null || group.IsAll ? null : group.Filter ?? "";
        var kind = _kind;
        var source = SelectedSourceId;
        var version = ++_loadVersion;
        Busy.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;
        UnlockButton.Visibility = Visibility.Collapsed;
        try
        {
            var entries = await Task.Run(() => AppServices.Library.Search(query, kind, 500, source, category));
            var items = await GuideService.ToViewModelsAsync(entries, 1);
            if (version != _loadVersion)
                return;
            Items.ItemsSource = items;
            ScopeText.Text = $"{items.Count:N0}{(items.Count == 500 ? "+" : "")} {(items.Count == 1 ? "match" : "matches")} in {(category is null ? $"all of {KindName}" : group!.Name)}";
            WidenButton.Content = $"Search all of {KindName}";
            WidenButton.Visibility = category is null ? Visibility.Collapsed : Visibility.Visible;
            ScopePanel.Visibility = Visibility.Visible;
            if (items.Count == 0)
                ShowEmpty($"No matches for “{query}”", category is null ? "Try fewer or different words." : "Try searching all of " + KindName + ".", showUnlock: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "browse search");
        }
        finally
        {
            if (version == _loadVersion)
                Busy.IsActive = false;
        }
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
