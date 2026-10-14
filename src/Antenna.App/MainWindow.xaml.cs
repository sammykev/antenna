using Antenna.App.Services;
using Antenna.App.Views;
using Antenna.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        // First run goes straight to adding a playlist; otherwise open Live TV.
        var start = AppServices.Library.GetSources().Count > 0 ? "live" : "playlists";
        NavigateTo(start);
    }

    public Frame Frame => ContentFrame;

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>Full-screen hides the navigation pane so video fills the screen.</summary>
    public void SetFullScreen(bool fullScreen)
    {
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        NavView.IsPaneVisible = !fullScreen;
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: string tag })
            NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        switch (tag)
        {
            case "live": Navigate(typeof(BrowsePage), MediaKind.Live); break;
            case "movies": Navigate(typeof(BrowsePage), MediaKind.Movie); break;
            case "series": Navigate(typeof(BrowsePage), MediaKind.Series); break;
            case "playlists": Navigate(typeof(PlaylistsPage), null); break;
        }
    }

    private void Navigate(Type page, object? parameter)
    {
        if (ContentFrame.CurrentSourcePageType == page && Equals(ContentFrame.Tag, parameter))
            return;
        ContentFrame.Tag = parameter;
        ContentFrame.Navigate(page, parameter);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.QueryText))
            return;
        ContentFrame.Tag = null;
        ContentFrame.Navigate(typeof(SearchPage), args.QueryText);
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
            ContentFrame.GoBack();
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // Keep the pane selection in sync when navigating back.
        var tag = e.SourcePageType == typeof(PlaylistsPage) ? "playlists"
            : e.SourcePageType == typeof(BrowsePage) && e.Parameter is MediaKind k ? k switch
            {
                MediaKind.Live => "live",
                MediaKind.Movie => "movies",
                _ => "series",
            }
            : null;

        if (tag is null)
            return;
        ContentFrame.Tag = e.Parameter;
        var item = NavView.MenuItems.Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null && !ReferenceEquals(NavView.SelectedItem, item))
            NavView.SelectedItem = item;
    }
}
