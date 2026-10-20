using System.Collections.ObjectModel;
using Antenna.Core;
using Antenna.Mobile.Services;

namespace Antenna.Mobile.Views;

public partial class LiveTvPage : ContentPage
{
    private const int PageSize = 300;

    private readonly ObservableCollection<ChannelRow> _rows = [];
    private GroupChip? _chip;
    private bool _exhausted;
    private bool _loading;
    private bool _stale = true;
    private CancellationTokenSource? _searchDelay;

    public LiveTvPage()
    {
        InitializeComponent();
        Channels.ItemsSource = _rows;
        MobileServices.LibraryChanged += () => _stale = true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_stale)
        {
            _stale = false;
            MobileServices.InvalidateNowPlaying();
            await LoadChipsAsync();
        }
        await Updates.CheckOnceAsync(this);
    }

    private async Task LoadChipsAsync()
    {
        var (groups, hasFavourites) = await Task.Run(() =>
            (MobileServices.Library.GetGroups(MediaKind.Live), MobileServices.Library.GetFavourites(MediaKind.Live).Count > 0));
        var chips = new List<GroupChip>();
        if (hasFavourites)
            chips.Add(new GroupChip("★ Favourites", null, Favourites: true));
        chips.Add(new GroupChip("All channels", null));
        chips.AddRange(groups.Select(g => new GroupChip(g.Length == 0 ? "Other" : g, g)));
        Chips.ItemsSource = chips;

        var last = Preferences.Get("live.group", "");
        var selected = chips.FirstOrDefault(c => c.Name == last) ?? chips[0];
        if (Equals(Chips.SelectedItem, selected))
            await ReloadAsync();
        else
            Chips.SelectedItem = selected;  // triggers the reload
        Chips.ScrollTo(selected, position: ScrollToPosition.MakeVisible, animate: false);
    }

    private async void Chips_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not GroupChip chip)
            return;
        _chip = chip;
        Preferences.Set("live.group", chip.Name);
        await ReloadAsync();
    }

    private async void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchDelay?.Cancel();
        _searchDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, _searchDelay.Token);
            await ReloadAsync();
        }
        catch (TaskCanceledException)
        {
            // Still typing.
        }
    }

    private async Task ReloadAsync()
    {
        _rows.Clear();
        _exhausted = false;
        await LoadMoreAsync();
    }

    private async void Channels_RemainingItemsThresholdReached(object? sender, EventArgs e) => await LoadMoreAsync();

    private async Task LoadMoreAsync()
    {
        if (_loading || _exhausted)
            return;
        _loading = true;
        Busy.IsRunning = _rows.Count == 0;
        try
        {
            var query = SearchBox.Text?.Trim() ?? "";
            var chip = _chip;
            var offset = _rows.Count;
            var library = MobileServices.Library;
            var rows = await Task.Run(() =>
            {
                IReadOnlyList<LibraryEntry> entries =
                    query.Length > 0 ? library.Search(query, MediaKind.Live, PageSize) :
                    chip?.Favourites == true ? library.GetFavourites(MediaKind.Live) :
                    library.GetItems(MediaKind.Live, group: chip?.Group, limit: PageSize, offset: offset);
                var now = MobileServices.NowPlaying();
                return entries.Select(e => ChannelRow.For(e, now)).ToList();
            });
            // Search and favourites come back in one go.
            _exhausted = rows.Count < PageSize || query.Length > 0 || chip?.Favourites == true;
            foreach (var row in rows)
                _rows.Add(row);
            EmptyText.Text = query.Length > 0 ? $"No channels match “{query}”."
                : Chips.ItemsSource is List<GroupChip> { Count: > 1 } ? "Nothing in this category."
                : "Add a playlist on the Playlists tab to get started.";
        }
        catch (Exception ex)
        {
            Log.Error("Loading channels failed", ex);
        }
        finally
        {
            _loading = false;
            Busy.IsRunning = false;
        }
    }

    private async void SearchGuide_Clicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("guidesearch");

    private async void Channel_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not ChannelRow row)
            return;
        var queue = _rows.Select(r => r.Item).ToList();
        await Playback.PlayAsync(queue, _rows.IndexOf(row));
    }
}
