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
        if (version != _version)
            return;
        _results = await GuideService.ToViewModelsAsync(hits);
        Results.ItemsSource = _results;
        NoResults.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void Results_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
        ItemMenu.OnRightTapped(sender, e, _results);
}
