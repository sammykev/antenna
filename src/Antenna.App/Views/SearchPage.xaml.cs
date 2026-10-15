using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>One search across live channels, movies and series from every playlist.</summary>
public sealed partial class SearchPage : Page
{
    private string _query = "";
    private int _version;
    private List<ItemViewModel> _results = [];

    public SearchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _query = e.Parameter as string ?? "";
        Header.Text = $"Results for “{_query}”";
        EventsLink.Content = $"Find live events for “{_query}” →";
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        MediaKind? kind = (KindFilter.SelectedItem?.Tag as string) switch
        {
            "live" => MediaKind.Live,
            "movie" => MediaKind.Movie,
            "series" => MediaKind.Series,
            _ => null,
        };
        var version = ++_version;
        var query = _query;
        var hits = await Task.Run(() => AppServices.Library.Search(query, kind));
        var groups = await Task.Run(() => AppServices.Library.SearchGroups(query));
        if (version != _version)
            return;
        ShowCategories(kind is null ? groups : groups.Where(g => g.Kind == kind).ToList());
        _results = await GuideService.ToViewModelsAsync(hits);
        Results.ItemsSource = _results;
        NoResults.Visibility = hits.Count == 0 && CategoryChips.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowCategories(IReadOnlyList<(MediaKind Kind, string Group)> groups)
    {
        CategoryChips.Children.Clear();
        foreach (var (kind, group) in groups)
        {
            var label = kind switch { MediaKind.Live => "Live TV", MediaKind.Movie => "Movies", _ => "Series" };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 14 });
            content.Children.Add(new TextBlock { Text = group });
            content.Children.Add(new TextBlock { Text = label, Opacity = 0.6 });
            var chip = new Button { Content = content, CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 4, 12, 4) };
            ToolTipService.SetToolTip(chip, $"Open this {label} category");
            chip.Click += (_, _) => Frame.Navigate(typeof(BrowsePage), new BrowseTarget(kind, group));
            CategoryChips.Children.Add(chip);
        }
        CategoryRow.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KindFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_query.Length > 0)
            _ = RunAsync();
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemViewModel vm)
            Playback.Open(vm, _results);
    }

    private void EventsLink_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(EventsPage), _query);

    private void Results_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
        ItemMenu.OnRightTapped(sender, e, _results);
}
