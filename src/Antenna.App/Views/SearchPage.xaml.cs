using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>One search across live channels, movies and series from every playlist.</summary>
public sealed partial class SearchPage : Page
{
    private string _query = "";

    public SearchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _query = e.Parameter as string ?? "";
        Header.Text = $"Results for “{_query}”";
        Run();
    }

    private void Run()
    {
        MediaKind? kind = (KindFilter.SelectedItem?.Tag as string) switch
        {
            "live" => MediaKind.Live,
            "movie" => MediaKind.Movie,
            "series" => MediaKind.Series,
            _ => null,
        };
        var hits = AppServices.Library.Search(_query, kind);
        Results.ItemsSource = GuideService.WithNowPlaying(hits);
        NoResults.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KindFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_query.Length > 0)
            Run();
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemViewModel vm)
            Playback.Open(vm);
    }
}
