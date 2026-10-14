using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>Category list on the left, tiles on the right. Used for Live TV, Movies and Series.</summary>
public sealed partial class BrowsePage : Page
{
    private const string AllGroups = "All";

    private MediaKind _kind;
    private IReadOnlyList<PlaylistSource> _sources = [];
    private bool _loading;

    public BrowsePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _kind = (MediaKind)e.Parameter;
        Header.Text = _kind switch { MediaKind.Live => "Live TV", MediaKind.Movie => "Movies", _ => "Series" };
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
        var previous = Groups.SelectedItem as string;
        var groups = new List<string> { AllGroups };
        groups.AddRange(AppServices.Library.GetGroups(_kind, SelectedSourceId).Select(g => g.Length == 0 ? "Uncategorised" : g));
        Groups.ItemsSource = groups;
        Groups.SelectedItem = groups.Contains(previous ?? "") ? previous : AllGroups;
    }

    private void LoadItems()
    {
        var group = Groups.SelectedItem as string;
        var filter = group is null or AllGroups ? null : group == "Uncategorised" ? "" : group;
        var entries = AppServices.Library.GetItems(_kind, SelectedSourceId, filter);
        Items.ItemsSource = entries.Select(e => new ItemViewModel(e)).ToList();
        EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemViewModel vm)
            Playback.Open(vm);
    }
}
