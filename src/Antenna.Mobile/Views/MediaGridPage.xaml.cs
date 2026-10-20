using System.Collections.ObjectModel;
using Antenna.Core;
using Antenna.Mobile.Services;

namespace Antenna.Mobile.Views;

public sealed class MoviesPage() : MediaGridPage(MediaKind.Movie);

public sealed class SeriesPage() : MediaGridPage(MediaKind.Series);

/// <summary>Movies or series as a poster grid, with category chips (favourites and continue watching first) and search.</summary>
public partial class MediaGridPage : ContentPage
{
    private const int PageSize = 120;

    private readonly MediaKind _kind;
    private readonly ObservableCollection<PosterRow> _rows = [];
    private GroupChip? _chip;
    private bool _exhausted;
    private bool _loading;
    private bool _stale = true;
    private CancellationTokenSource? _searchDelay;

    protected MediaGridPage(MediaKind kind)
    {
        _kind = kind;
        InitializeComponent();
        Title = kind == MediaKind.Movie ? "Movies" : "Series";
        SearchBox.Placeholder = kind == MediaKind.Movie ? "Search movies" : "Search series";
        Posters.ItemsSource = _rows;
        MobileServices.LibraryChanged += () => _stale = true;
    }

    private string PrefKey => _kind == MediaKind.Movie ? "movies.group" : "series.group";

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_stale)
        {
            _stale = false;
            await LoadChipsAsync();
        }
        else if (_chip?.Continue == true)
        {
            await ReloadAsync();  // just watched something: the list has changed
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        // About 120 points per poster: 3 across on a phone held upright, more on its side.
        if (width > 0)
            GridLayout.Span = Math.Max(3, (int)(width / 120));
    }

    private async Task LoadChipsAsync()
    {
        var library = MobileServices.Library;
        var (groups, hasFavourites, hasContinue) = await Task.Run(() => (
            library.GetGroups(_kind),
            library.GetFavourites(_kind).Count > 0,
            _kind == MediaKind.Movie && library.GetHistory(200).Any(w => w.Kind == MediaKind.Movie && w.IsResumable)));
        var chips = new List<GroupChip>();
        if (hasContinue)
            chips.Add(new GroupChip("Continue watching", null, Continue: true));
        if (hasFavourites)
            chips.Add(new GroupChip("★ Favourites", null, Favourites: true));
        chips.Add(new GroupChip(_kind == MediaKind.Movie ? "All movies" : "All series", null));
        chips.AddRange(groups.Select(g => new GroupChip(g.Length == 0 ? "Other" : g, g)));
        Chips.ItemsSource = chips;

        var last = Preferences.Get(PrefKey, "");
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
        Preferences.Set(PrefKey, chip.Name);
        await ReloadAsync();
    }

    private async void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchDelay?.Cancel();
        _searchDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, _searchDelay.Token);
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

    private async void Posters_RemainingItemsThresholdReached(object? sender, EventArgs e) => await LoadMoreAsync();

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
            var kind = _kind;
            var library = MobileServices.Library;
            var rows = await Task.Run(() =>
            {
                // Films show how far you got; series progress is per episode, on the series page.
                var history = kind == MediaKind.Movie
                    ? library.GetHistory(500).Where(w => w.Kind == MediaKind.Movie).GroupBy(w => (w.SourceId, w.ExternalId)).ToDictionary(g => g.Key, g => g.First())
                    : [];
                IReadOnlyList<LibraryEntry> entries =
                    query.Length > 0 ? library.Search(query, kind, PageSize) :
                    chip?.Continue == true ? [.. history.Values.Where(w => w.IsResumable).OrderByDescending(w => w.WatchedAt)
                        .Select(w => library.GetItem(w.SourceId, w.ExternalId, kind)).OfType<LibraryEntry>()] :
                    chip?.Favourites == true ? library.GetFavourites(kind) :
                    library.GetItems(kind, group: chip?.Group, limit: PageSize, offset: offset);
                return entries.Select(e => new PosterRow(e,
                    history.TryGetValue((e.Item.SourceId, e.Item.ExternalId), out var w) && w.IsResumable ? w.Progress : 0)).ToList();
            });
            _exhausted = rows.Count < PageSize || query.Length > 0 || chip?.Favourites == true || chip?.Continue == true;
            foreach (var row in rows)
                _rows.Add(row);
            var noun = _kind == MediaKind.Movie ? "movies" : "series";
            EmptyText.Text = query.Length > 0 ? $"No {noun} match “{query}”."
                : Chips.ItemsSource is List<GroupChip> { Count: > 1 } ? "Nothing in this category."
                : $"No {noun} yet. Xtream Codes logins bring their {noun} in; M3U playlists may have some too.";
        }
        catch (Exception ex)
        {
            Log.Error($"Loading {_kind} failed", ex);
        }
        finally
        {
            _loading = false;
            Busy.IsRunning = false;
        }
    }

    private async void Poster_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not PosterRow row)
            return;
        await Navigation.PushAsync(new DetailsPage(row.Entry));
    }
}
