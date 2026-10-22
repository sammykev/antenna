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
/// Category list on the left; posters for Movies and Series, or for Live TV a channel list with the
/// selected channel playing in a preview pane. Items load a page at a time as you scroll, off the UI thread.
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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewTimer;
    private LivePreview? _preview;
    private ItemViewModel? _previewing;
    private int _infoVersion;

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
        SetLayout();
        LoadSources();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _ = StopPreviewAsync();
    }

    private bool IsLive => _kind == MediaKind.Live;

    private bool PreviewOn => IsLive && AppSettings.LivePreview;

    /// <summary>Where the items are shown: the channel list for Live TV, the poster grid otherwise.</summary>
    private ListViewBase ItemsHost => IsLive ? ChannelList : Items;

    private void SetLayout()
    {
        Items.Visibility = IsLive ? Visibility.Collapsed : Visibility.Visible;
        ChannelList.Visibility = IsLive ? Visibility.Visible : Visibility.Collapsed;
        if (IsLive)
            Items.ItemsSource = null;
        else
            ChannelList.ItemsSource = null;
        PreviewPane.Visibility = PreviewOn ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = PreviewOn ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
        ResetPreviewInfo();
        if (PreviewOn && _preview is null)
        {
            // A fresh player each visit: the old one was released when the page was left.
            _preview = new LivePreview();
            _preview.Buffering += busy => PreviewBusy.IsActive = busy;
            _preview.Failed += () =>
            {
                PreviewHintText.Text = "This channel isn't playing right now.";
                PreviewHint.Visibility = Visibility.Visible;
            };
            PreviewHost.Children.Add(_preview);
        }
        UpdateMuteIcon();
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
            ItemsHost.ItemsSource = null;
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
        ItemsHost.ItemsSource = list;
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
            ItemsHost.ItemsSource = items;
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

    private IReadOnlyList<ItemViewModel> LoadedItems => (ItemsHost.ItemsSource as IEnumerable<ItemViewModel>)?.ToList() ?? [];

    /// <summary>With the preview on, the first click on a channel previews it and the second goes full screen.</summary>
    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ItemViewModel vm)
            return;
        if (PreviewOn && !ReferenceEquals(vm, _previewing))
        {
            ChannelList.SelectedItem = vm;
            StartPreview(vm);
            return;
        }
        _ = WatchAsync(vm);
    }

    // ---- Live preview ----

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!PreviewOn || ChannelList.SelectedItem is not ItemViewModel vm || ReferenceEquals(vm, _previewing))
            return;
        // Arrowing through the list shows details at once but only tunes in when you stop on a channel.
        ShowPreviewInfo(vm);
        if (_previewTimer is null)
        {
            _previewTimer = DispatcherQueue.CreateTimer();
            _previewTimer.Interval = TimeSpan.FromMilliseconds(450);
            _previewTimer.IsRepeating = false;
            _previewTimer.Tick += (_, _) =>
            {
                if (ChannelList.SelectedItem is ItemViewModel selected && !ReferenceEquals(selected, _previewing))
                    StartPreview(selected);
            };
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void StartPreview(ItemViewModel vm)
    {
        _previewTimer?.Stop();
        _previewing = vm;
        ShowPreviewInfo(vm);
        if (_preview is null || string.IsNullOrEmpty(vm.Item.StreamUrl))
            return;
        PreviewHint.Visibility = Visibility.Collapsed;
        PreviewLiveBadge.Visibility = Visibility.Visible;
        _preview.Play(PlayRequest.ForItem(vm.Item));
    }

    /// <summary>Full screen, after the preview has let go of its stream (many accounts allow only one).</summary>
    private async Task WatchAsync(ItemViewModel vm)
    {
        var siblings = LoadedItems;
        await StopPreviewAsync();
        Playback.Open(vm, siblings);
    }

    private async Task StopPreviewAsync()
    {
        _previewTimer?.Stop();
        _previewing = null;
        if (_preview is not { } preview)
            return;
        _preview = null;
        PreviewHost.Children.Clear();
        await preview.ShutdownAsync();
    }

    private void ResetPreviewInfo()
    {
        _previewing = null;
        PreviewInfo.Visibility = Visibility.Collapsed;
        PreviewLiveBadge.Visibility = Visibility.Collapsed;
        PreviewHintText.Text = "Pick a channel to preview it";
        PreviewHint.Visibility = Visibility.Visible;
    }

    /// <summary>The channel, what's on now with progress, and what's next.</summary>
    private async void ShowPreviewInfo(ItemViewModel vm)
    {
        var version = ++_infoVersion;
        PreviewInfo.Visibility = Visibility.Visible;
        PreviewChannel.Text = vm.NumberText.Length > 0 ? $"{vm.NumberText}  {vm.Title}" : vm.Title;
        PreviewSource.Text = string.IsNullOrEmpty(vm.Group) ? vm.SourceName : $"{vm.Group} · {vm.SourceName}";
        PreviewLogo.Source = vm.Logo.Source;
        UpdateFavourite(vm);
        NowPanel.Visibility = NextPanel.Visibility = NoGuideText.Visibility = Visibility.Collapsed;

        if (vm.Item.EpgChannelId is not { } channel)
        {
            NoGuideText.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            var (now, next) = await GuideService.NowAndNextAsync(vm.Item.SourceId, channel);
            if (version != _infoVersion)
                return;
            if (now is not null)
            {
                NowTitle.Text = now.Title;
                NowTimes.Text = $"{now.Start.LocalDateTime:HH:mm} – {now.Stop.LocalDateTime:HH:mm}";
                NowProgress.Value = now.Duration.TotalSeconds > 0 ? Math.Clamp((DateTimeOffset.Now - now.Start) / now.Duration, 0, 1) * 100 : 0;
                NowDescription.Text = now.Description ?? "";
                NowDescription.Visibility = string.IsNullOrWhiteSpace(now.Description) ? Visibility.Collapsed : Visibility.Visible;
                NowPanel.Visibility = Visibility.Visible;
            }
            if (next is not null)
            {
                NextTitle.Text = $"{next.Start.LocalDateTime:HH:mm}  {next.Title}";
                NextPanel.Visibility = Visibility.Visible;
            }
            if (now is null && next is null)
                NoGuideText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "preview guide");
        }
    }

    private void UpdateFavourite(ItemViewModel vm)
    {
        PreviewFavouriteIcon.Glyph = vm.IsFavourite ? "\uE735" : "\uE734";
        PreviewFavouriteText.Text = vm.IsFavourite ? "Favourite" : "Add to favourites";
    }

    private void UpdateMuteIcon()
    {
        PreviewMuteIcon.Glyph = LivePreview.Muted ? "\uE74F" : "\uE767";
        ToolTipService.SetToolTip(PreviewMute, LivePreview.Muted ? "Unmute the preview" : "Mute the preview");
    }

    private void PreviewFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = Math.Round(e.NewSize.Width * 9 / 16);
        if (Math.Abs(PreviewFrame.Height - height) > 0.5)
            PreviewFrame.Height = height;
    }

    private void PreviewFrame_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => PreviewFull_Click(sender, e);

    private void ChannelList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ItemViewModel vm)
            _ = WatchAsync(vm);
    }

    private void PreviewFull_Click(object sender, RoutedEventArgs e)
    {
        if ((_previewing ?? ChannelList.SelectedItem as ItemViewModel) is { } vm)
            _ = WatchAsync(vm);
    }

    private void PreviewMute_Click(object sender, RoutedEventArgs e)
    {
        var muted = !LivePreview.Muted;
        if (_preview is { } preview)
            preview.SetMuted(muted);
        else
            LivePreview.Muted = muted;
        UpdateMuteIcon();
    }

    private void PreviewFavourite_Click(object sender, RoutedEventArgs e)
    {
        if ((_previewing ?? ChannelList.SelectedItem as ItemViewModel) is not { } vm)
            return;
        var on = !vm.IsFavourite;
        AppServices.Library.SetFavourite(vm.Item, on);
        vm.IsFavourite = on;
        UpdateFavourite(vm);
    }

    private void PreviewGuide_Click(object sender, RoutedEventArgs e) => App.Window.Open("guide");

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
