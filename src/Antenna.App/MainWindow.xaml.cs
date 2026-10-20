using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.App.Views;
using Antenna.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, (Type Page, object? Parameter)> Pages = new()
    {
        ["home"] = (typeof(HomePage), null),
        ["live"] = (typeof(BrowsePage), MediaKind.Live),
        ["guide"] = (typeof(GuidePage), null),
        ["events"] = (typeof(EventsPage), null),
        ["matches"] = (typeof(MatchesPage), null),
        ["movies"] = (typeof(BrowsePage), MediaKind.Movie),
        ["series"] = (typeof(BrowsePage), MediaKind.Series),
        ["favourites"] = (typeof(FavouritesPage), null),
        ["multiview"] = (typeof(MultiViewPage), null),
        ["recordings"] = (typeof(RecordingsPage), null),
        ["playlists"] = (typeof(PlaylistsPage), null),
        ["settings"] = (typeof(SettingsPage), null),
    };

    private readonly DispatcherQueueTimer _suggestTimer;
    private List<SuggestionItem> _suggestions = [];

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 860));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Antenna.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TitleLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo-64.png")));
        ApplyTheme();
        // Set up before the window is shown: moving the navigation view while it's loading can
        // crash WinUI natively (it did, on every restart, in 0.1.9).
        if (AppSettings.TvMode)
            ApplyTvMode(true);

        _suggestTimer = DispatcherQueue.CreateTimer();
        _suggestTimer.Interval = TimeSpan.FromMilliseconds(250);
        _suggestTimer.IsRepeating = false;
        _suggestTimer.Tick += async (_, _) => await UpdateSuggestionsAsync();

        AutoRefresh.Refreshed += OnBackgroundRefresh;

        // First run goes straight to adding a playlist; otherwise Home (if on) or the chosen start page.
        var start = AppServices.Library.GetSources().Count == 0 ? "playlists"
            : AppSettings.ShowSplash ? "home"
            : AppSettings.StartPage;
        NavigateTo(Pages.ContainsKey(start) ? start : "live");
    }

    public Frame Frame => ContentFrame;

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public bool IsMiniPlayer => AppWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;

    public void ApplyTheme()
    {
        RootGrid.RequestedTheme = AppSettings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>Full-screen hides the navigation pane and title bar so video fills the screen.</summary>
    public void SetFullScreen(bool fullScreen)
    {
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        SetChromeVisible(!fullScreen);
    }

    /// <summary>A small always-on-top window showing just the player.</summary>
    public void SetMiniPlayer(bool mini)
    {
        AppWindow.SetPresenter(mini ? AppWindowPresenterKind.CompactOverlay : AppWindowPresenterKind.Default);
        if (mini)
            AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 315));
        SetChromeVisible(!mini);
    }

    private void SetChromeVisible(bool visible)
    {
        // The title bar comes and goes with full screen; TV mode's scaled area follows it.
        DispatcherQueue.TryEnqueue(SizeTvContentIfOn);
        NavView.IsPaneVisible = visible;
        NavView.IsBackButtonVisible = visible ? NavigationViewBackButtonVisible.Auto : NavigationViewBackButtonVisible.Collapsed;
        AppTitleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowNotice(InfoBarSeverity severity, string message)
    {
        Notice.ActionButton = null;
        Notice.Title = "";
        Notice.Severity = severity;
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    /// <summary>Once the window has loaded (dialogs need its XamlRoot), shows What's new if Antenna was just updated.</summary>
    public void ShowWhatsNewWhenReady()
    {
        if (RootGrid.IsLoaded)
        {
            _ = WhatsNew.ShowIfUpdatedAsync(RootGrid.XamlRoot);
            return;
        }
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= OnLoaded;
            _ = WhatsNew.ShowIfUpdatedAsync(RootGrid.XamlRoot);
        }
        RootGrid.Loaded += OnLoaded;
    }

    /// <summary>An error the app survived, with a button that copies the details for a bug report.</summary>
    public void ShowError(string message, string details)
    {
        var copy = new Button { Content = "Copy details" };
        copy.Click += (_, _) =>
        {
            try
            {
                CrashReport.Copy(details);
                copy.Content = "Copied";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "copy crash details");
            }
        };
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Title = "";
        Notice.Message = message;
        Notice.ActionButton = copy;
        Notice.IsOpen = true;
    }

    /// <summary>A reminder going off: the programme, when, and Watch now.</summary>
    public void ShowReminder(string title, string when, Action watch)
    {
        var button = new Button { Content = "Watch now" };
        button.Click += (_, _) =>
        {
            Notice.IsOpen = false;
            watch();
        };
        Notice.Severity = InfoBarSeverity.Informational;
        Notice.Title = title;
        Notice.Message = when;
        Notice.ActionButton = button;
        Notice.IsOpen = true;
    }

    /// <summary>A downloaded update: offer to restart now (otherwise it installs at the next start).</summary>
    public void ShowUpdateReady(string version)
    {
        var restart = new Button { Content = "Restart to update" };
        restart.Click += (_, _) => Updater.RestartToUpdate();
        Notice.Severity = InfoBarSeverity.Success;
        Notice.Title = "Update ready";
        Notice.Message = $"Antenna {version} is ready. Restart now, or it will install the next time you open Antenna.";
        Notice.ActionButton = restart;
        Notice.IsOpen = true;
    }

    private void OnBackgroundRefresh(string name, RefreshResult? result, string? error)
    {
        if (error is not null)
            ShowNotice(InfoBarSeverity.Warning, $"Couldn't refresh {name}: {error}");
        else if (result?.GuideError is { } guideError)
            ShowNotice(InfoBarSeverity.Warning, $"{name} refreshed, but its guide failed: {guideError}");
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: string tag })
            NavigateTo(tag);
    }

    /// <summary>Opens a page by its pane tag ("live", "movies", …).</summary>
    public void Open(string tag) => NavigateTo(tag);

    private void NavigateTo(string tag)
    {
        if (Pages.TryGetValue(tag, out var target))
            Navigate(target.Page, target.Parameter);
    }

    private void Navigate(Type page, object? parameter)
    {
        if (ContentFrame.CurrentSourcePageType == page && Equals(ContentFrame.Tag, parameter))
            return;
        ContentFrame.Tag = parameter;
        ContentFrame.Navigate(page, parameter);
    }

    // ---- Search ----

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        _suggestTimer.Stop();
        if (sender.Text.Trim().Length < 2)
        {
            sender.ItemsSource = null;
            return;
        }
        _suggestTimer.Start();
    }

    private async Task UpdateSuggestionsAsync()
    {
        var text = SearchBox.Text;
        var hits = await Task.Run(() => AppServices.Library.Search(text, null, 8));
        if (text != SearchBox.Text)
            return;
        _suggestions = hits.Select(h => new SuggestionItem(h)).ToList();
        SearchBox.ItemsSource = _suggestions;
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SuggestionItem chosen)
        {
            sender.Text = "";
            sender.ItemsSource = null;
            Playback.Open(new ItemViewModel(chosen.Entry));
            return;
        }
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
        var parameter = e.Parameter is BrowseTarget target ? target.Kind : e.Parameter;
        var match = Pages.FirstOrDefault(p => p.Value.Page == e.SourcePageType && Equals(p.Value.Parameter, parameter is MediaKind ? parameter : p.Value.Parameter));
        if (match.Key is null)
            return;
        ContentFrame.Tag = e.Parameter;
        var item = NavView.MenuItems.Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == match.Key);
        if (item is not null && !ReferenceEquals(NavView.SelectedItem, item))
            NavView.SelectedItem = item;
    }
}
