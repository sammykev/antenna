using System.Globalization;
using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Antenna.Core.Recordings;
using Antenna.Core.Playback;
using Antenna.Core.Sports;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>
/// Today's (and the next days') fixtures with live scores, each matched to the user's own channels:
/// Watch when it's on, Remind or Record when it's later, or add it to multi-view.
/// </summary>
public sealed partial class MatchesPage : Page
{
    private const int DaysAhead = 3;
    private static readonly TimeSpan LiveRefresh = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);
    private static readonly Dictionary<DateOnly, (IReadOnlyList<Fixture> Fixtures, DateTimeOffset At)> Cache = [];
    private static readonly Dictionary<string, FixtureMatch> Matches = [];

    private readonly EspnFixtures _feed = new(AppServices.Http);
    private readonly Dictionary<string, Row> _rows = [];
    private readonly List<(TextBlock Header, List<Row> Rows)> _groups = [];
    private DispatcherQueueTimer? _timer;
    private int _version;
    private bool _ready;

    /// <summary>The bits of a match row that change after it's built.</summary>
    private sealed class Row
    {
        public required Fixture Fixture;
        public required Grid Root;
        public required TextBlock Time;
        public required TextBlock HomeScore;
        public required TextBlock AwayScore;
        public required TextBlock Channel;
        public required StackPanel Actions;
        public FixtureMatch? Match;
    }

    public MatchesPage()
    {
        InitializeComponent();
        // Kept in memory so Back returns to it exactly as it was left.
        NavigationCacheMode = NavigationCacheMode.Enabled;
        var today = DateTime.Today;
        for (var i = 0; i < DaysAhead; i++)
            Days.Items.Add(new SelectorBarItem { Text = Ui.Day(today.AddDays(i)), Tag = i, IsSelected = i == 0 });
        BuildSportChips();
        _ready = true;
    }

    private DateOnly SelectedDay =>
        DateOnly.FromDateTime(DateTime.Today.AddDays(Days.SelectedItem?.Tag is int offset ? offset : 0));

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = LiveRefresh;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _ = RefreshScoresAsync();
        _timer.Start();
        // Back returns to the list as it was (scores keep refreshing).
        if (e.NavigationMode == NavigationMode.Back && _rows.Count > 0)
            return;
        _ = LoadAsync(force: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _timer?.Stop();
        _version++;
    }

    // ---- Loading ----

    private async Task<IReadOnlyList<Fixture>> FixturesAsync(DateOnly day, bool force)
    {
        if (!force && Cache.TryGetValue(day, out var cached) && DateTimeOffset.Now - cached.At < CacheFor)
            return cached.Fixtures;
        var fixtures = await _feed.GetDayAsync(day);
        Cache[day] = (fixtures, DateTimeOffset.Now);
        return fixtures;
    }

    private async Task LoadAsync(bool force)
    {
        var version = ++_version;
        var day = SelectedDay;
        Busy.IsActive = true;
        StatusText.Text = "Loading fixtures…";
        EmptyState.Visibility = Visibility.Collapsed;
        IReadOnlyList<Fixture> fixtures;
        try
        {
            fixtures = await FixturesAsync(day, force);
        }
        catch (Exception ex)
        {
            Log.Info($"matches: couldn't load fixtures: {ex.Message}");
            fixtures = [];
        }
        if (version != _version)
            return;

        Build(fixtures);
        if (fixtures.Count == 0)
        {
            Busy.IsActive = false;
            StatusText.Text = "";
            ShowEmpty("No fixtures", "Couldn't load fixtures for this day, or there are none. Check your internet connection and refresh.");
            return;
        }

        // Find each match on the user's channels, a few at a time, live and upcoming first.
        StatusText.Text = "Finding matches on your channels…";
        var order = fixtures.OrderBy(f => f.State == FixtureState.Live ? 0 : f.State == FixtureState.Scheduled ? 1 : 2).ToList();
        foreach (var fixture in order)
        {
            if (version != _version)
                return;
            if (!Matches.TryGetValue(fixture.Id, out var match))
            {
                try
                {
                    match = await Task.Run(() => FixtureMatcher.Find(AppServices.Library, fixture));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "matches: finding a fixture");
                    match = new FixtureMatch(fixture, [], null);
                }
                Matches[fixture.Id] = match;
            }
            if (version == _version && _rows.TryGetValue(fixture.Id, out var row))
                ShowMatch(row, match);
        }
        if (version != _version)
            return;
        Busy.IsActive = false;
        var mine = _rows.Values.Count(r => r.Match?.OnYourChannels == true);
        StatusText.Text = $"{fixtures.Count} fixtures · {mine} on your channels · scores from ESPN";
        ApplyFilters();
    }

    /// <summary>Updates scores and status in place every minute (today only), without rebuilding the list.</summary>
    private async Task RefreshScoresAsync()
    {
        if (SelectedDay != DateOnly.FromDateTime(DateTime.Today) || !_rows.Values.Any(r => r.Fixture.State != FixtureState.Finished))
            return;
        var version = _version;
        IReadOnlyList<Fixture> fixtures;
        try
        {
            fixtures = await FixturesAsync(SelectedDay, force: true);
        }
        catch (Exception ex)
        {
            Log.Info($"matches: score refresh failed: {ex.Message}");
            return;
        }
        if (version != _version)
            return;
        foreach (var f in fixtures)
        {
            if (!_rows.TryGetValue(f.Id, out var row))
                continue;
            row.Fixture = f;
            ShowScore(row);
            if (row.Match is { } m)
                ShowMatch(row, m with { Fixture = f });
        }
    }

    // ---- Building rows ----

    private void Build(IReadOnlyList<Fixture> fixtures)
    {
        MatchList.Children.Clear();
        _rows.Clear();
        _groups.Clear();
        foreach (var league in fixtures.GroupBy(f => f.League))
        {
            var header = new TextBlock { Text = league.Key, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 16, 0, 4) };
            MatchList.Children.Add(header);
            var rows = new List<Row>();
            foreach (var fixture in league)
            {
                var row = BuildRow(fixture);
                _rows[fixture.Id] = row;
                rows.Add(row);
                MatchList.Children.Add(row.Root);
            }
            _groups.Add((header, rows));
        }
        ApplyFilters();
    }

    private Row BuildRow(Fixture f)
    {
        var root = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            ColumnSpacing = 16,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        root.Children.Add(time);

        var teams = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var homeScore = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinWidth = 40, TextAlignment = TextAlignment.Right };
        var awayScore = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinWidth = 40, TextAlignment = TextAlignment.Right };
        if (f.Home is not null && f.Away is not null)
        {
            teams.Children.Add(Side(f.Home, f.HomeLogo, homeScore));
            teams.Children.Add(Side(f.Away, f.AwayLogo, awayScore));
        }
        else
        {
            teams.Children.Add(new TextBlock { Text = f.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        }
        Grid.SetColumn(teams, 1);
        root.Children.Add(teams);

        var channel = new TextBlock { Text = "Looking on your channels…", Opacity = 0.6, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxLines = 2 };
        Grid.SetColumn(channel, 2);
        root.Children.Add(channel);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 3);
        root.Children.Add(actions);

        var row = new Row { Fixture = f, Root = root, Time = time, HomeScore = homeScore, AwayScore = awayScore, Channel = channel, Actions = actions };
        ShowScore(row);
        return row;
    }

    private static Grid Side(string name, string? logo, TextBlock score)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var image = new Image { Width = 22, Height = 22, Stretch = Stretch.Uniform };
        if (Ui.IsWebUrl(logo))
            _ = LoadLogoAsync(image, logo!);
        grid.Children.Add(image);
        var text = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(score, 2);
        grid.Children.Add(score);
        return grid;
    }

    private static async Task LoadLogoAsync(Image image, string url)
    {
        if (await ImageCache.GetAsync(url) is { } path)
            image.Source = Ui.FromFile(path, 22);
    }

    private static void ShowScore(Row row)
    {
        var f = row.Fixture;
        row.Time.Text = f.State switch
        {
            FixtureState.Live => $"● {f.Status}",
            FixtureState.Finished => f.Status.Length > 0 ? f.Status : "FT",
            _ => f.Start.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture),
        };
        row.Time.Foreground = f.State == FixtureState.Live
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 0xE5, 0x48, 0x4D))
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        row.HomeScore.Text = f.HomeScore ?? "";
        row.AwayScore.Text = f.AwayScore ?? "";
    }

    // ---- Where to watch ----

    private void ShowMatch(Row row, FixtureMatch match)
    {
        row.Match = match;
        row.Actions.Children.Clear();
        var f = row.Fixture;
        if (!match.OnYourChannels)
        {
            row.Channel.Text = f.Broadcasts.Count > 0 ? $"Not on your channels (TV: {string.Join(", ", f.Broadcasts.Take(2))})" : "Not on your channels";
            row.Channel.Opacity = 0.5;
            AddFollowButton(row);
            ApplyFilters();
            return;
        }

        var first = match.Channels[0];
        var more = match.Channels.Count - 1;
        // Coverage of the sport or tournament may be another game: say so rather than promise this one.
        var lead = match.IsCoverage ? $"{f.Sport} coverage on" : "On";
        row.Channel.Text = $"{lead} {first.Item.Title}{(more > 0 ? $" +{more}" : "")}{(match.Programme is { } p ? $"\n{p.Title}" : "")}";
        row.Channel.Opacity = 1;

        var soon = f.State == FixtureState.Live || f.Start - DateTimeOffset.Now < TimeSpan.FromMinutes(20);
        if (f.State != FixtureState.Finished && soon)
            row.Actions.Children.Add(WatchButton(match));
        if (f.State == FixtureState.Scheduled && !soon)
            row.Actions.Children.Add(RemindButton(row, match));
        if (f.State != FixtureState.Finished && first.Item.StreamUrl is { } url)
        {
            var record = Small("", "Record");
            record.Click += (_, _) =>
            {
                var programme = match.Programme ?? Stand_in(match);
                var (start, stop) = RecordingPlanner.Window(programme);
                if (start < DateTimeOffset.Now)
                    start = DateTimeOffset.Now;
                Recorder.Schedule(f.Title, url, first.Item.Options, start, stop);
                App.Window.ShowNotice(InfoBarSeverity.Success, start <= DateTimeOffset.Now.AddMinutes(1)
                    ? $"Recording {f.Title}. See Recordings."
                    : $"{f.Title} will record at {start.LocalDateTime:ddd HH:mm}. Keep Antenna open.");
            };
            row.Actions.Children.Add(record);
            var multi = Small("", "Add to multi-view");
            multi.Click += (_, _) => MultiViewPage.Add(PlayRequest.ForItem(first.Item) with { Title = f.Title });
            row.Actions.Children.Add(multi);
        }
        AddFollowButton(row);
        ApplyFilters();
    }

    /// <summary>★ to follow either team: kick-off, goal and full-time notifications (Services/GoalAlerts).</summary>
    private void AddFollowButton(Row row)
    {
        var f = row.Fixture;
        if (f.Home is null || f.Away is null)
            return;
        var followed = ScoreAlerts.Involves(f, ScoreAlerts.Followed(AppServices.Library));
        var button = Small(followed ? "\uE735" : "\uE734", "Follow a team: goal alerts");
        var menu = new MenuFlyout();
        foreach (var team in new[] { f.Home, f.Away })
        {
            var on = ScoreAlerts.IsFollowed(AppServices.Library, team);
            menu.Items.Add(ItemMenu.Item(on ? $"Stop following {team}" : $"Follow {team} (kick-off, goals, full time)", on ? "\uE735" : "\uE734", () =>
            {
                var now = ScoreAlerts.Toggle(AppServices.Library, team, f.FeedPath);
                GoalAlerts.CheckSoon();
                App.Window.ShowNotice(InfoBarSeverity.Success, now
                    ? $"Following {team}: you'll get a notification at kick-off, for each goal and at full time while Antenna is open."
                    : $"Stopped following {team}.");
                foreach (var r in _rows.Values.Where(r => r.Match is not null))
                    ShowMatch(r, r.Match!);
            }));
        }
        button.Flyout = menu;
        row.Actions.Children.Add(button);
    }

    private UIElement WatchButton(FixtureMatch match)
    {
        var first = match.Channels[0];
        if (match.Channels.Count == 1)
        {
            var watch = new Button { Content = "Watch", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            watch.Click += async (_, _) => await WatchCheckedAsync(watch, match, first);
            return watch;
        }
        // Several channels show it: Watch plays the first that's broadcasting; the arrow lists them all.
        var split = new SplitButton { Content = "Watch" };
        split.Click += async (_, _) => await WatchCheckedAsync(split, match, first);
        var menu = new MenuFlyout();
        foreach (var c in match.Channels)
            // Each channel with what its guide says it's showing, so it's clear they're all this match.
            menu.Items.Add(ItemMenu.Item($"{c.Item.Title} · {match.ProgrammeOn(c)?.Title ?? c.SourceName}", "", async () => await WatchCheckedAsync(split, match, c)));
        split.Flyout = menu;
        return split;
    }

    /// <summary>
    /// Plays the chosen channel if it's really broadcasting; otherwise the next of the match's channels that is.
    /// Guides often list a match on a channel that's dark or showing a slate.
    /// </summary>
    private static async Task WatchCheckedAsync(Control button, FixtureMatch match, LibraryEntry chosen)
    {
        if (!button.IsEnabled)
            return;
        List<LibraryEntry> order = [chosen, .. match.Channels.Where(c => !ReferenceEquals(c, chosen))];
        button.IsEnabled = false;
        App.Window.ShowNotice(InfoBarSeverity.Informational, $"Checking {chosen.Item.Title} is broadcasting…");
        try
        {
            var (onAir, dark) = await StreamProbe.FirstOnAirAsync(AppServices.Http, order, TimeSpan.FromSeconds(8));
            if (onAir is null)
            {
                App.Window.ShowNotice(InfoBarSeverity.Warning, dark.Count == 1
                    ? $"{chosen.Item.Title} isn't broadcasting right now. Try again closer to the start."
                    : $"None of the {dark.Count} channels listing {match.Fixture.Title} are broadcasting right now. Try again closer to the start.");
                return;
            }
            if (dark.Count > 0)
                App.Window.ShowNotice(InfoBarSeverity.Informational, $"{string.Join(", ", dark.Select(d => d.Item.Title))} wasn't broadcasting, so playing {onAir.Item.Title}.");
            else
                App.Window.HideNotice();
            Playback.Play(PlayRequest.ForItem(onAir.Item) with { Title = $"{onAir.Item.Title} · {match.Fixture.Title}" });
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private Button RemindButton(Row row, FixtureMatch match)
    {
        var first = match.Channels[0];
        var programme = match.Programme ?? Stand_in(match);
        var set = Reminders.IsSet(first.Item.SourceId, first.Item.ExternalId, programme);
        var button = Small(set ? "" : "", set ? "Reminder set (click to cancel)" : "Remind me");
        button.Click += (_, _) =>
        {
            var on = Reminders.Toggle(first.Item, programme);
            App.Window.ShowNotice(InfoBarSeverity.Success, on ? $"I'll remind you before {row.Fixture.Title}." : "Reminder cancelled.");
            ShowMatch(row, match);
        };
        return button;
    }

    /// <summary>For matches found by channel name rather than the guide: kick-off plus about two hours.</summary>
    private static Programme Stand_in(FixtureMatch match) => new()
    {
        ChannelId = match.Channels[0].Item.EpgChannelId ?? "",
        Title = match.Fixture.Title,
        Start = match.Fixture.Start,
        Stop = match.Fixture.Start.AddHours(match.Fixture.Sport == "Football" ? 2 : 3),
    };

    private static Button Small(string glyph, string tip)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    // ---- Filters ----

    private void ApplyFilters()
    {
        var sport = _sport;
        var mine = MineOnly.IsOn;
        var any = false;
        foreach (var (header, rows) in _groups)
        {
            var visibleInGroup = false;
            foreach (var row in rows)
            {
                var show = (sport is null || row.Fixture.Sport == sport) && (!mine || row.Match?.OnYourChannels == true);
                row.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                visibleInGroup |= show;
            }
            header.Visibility = visibleInGroup ? Visibility.Visible : Visibility.Collapsed;
            any |= visibleInGroup;
        }
        if (_rows.Count > 0)
        {
            if (any)
                EmptyState.Visibility = Visibility.Collapsed;
            else
                ShowEmpty("Nothing to show", mine ? "None of these fixtures are on your channels (yet: the guide may not list them until closer to the day)." : "No fixtures for this sport on this day.");
        }
    }

    private void ShowEmpty(string title, string detail)
    {
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void Days_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_ready)
            _ = LoadAsync(force: false);
    }

    /// <summary>The sport shown, or null for all.</summary>
    private string? _sport;

    private void BuildSportChips()
    {
        SportChips.Children.Clear();
        foreach (var sport in new string?[] { null }.Concat(EspnFixtures.Feeds.Select(f => f.Sport).Distinct()))
        {
            var chip = new Button
            {
                Content = sport ?? "All sports",
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 4, 14, 4),
                Tag = sport,
            };
            if (sport == _sport)
                chip.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            chip.Click += (_, _) =>
            {
                _sport = sport;
                BuildSportChips();
                if (_ready)
                    ApplyFilters();
            };
            SportChips.Children.Add(chip);
        }
    }

    private void MineOnly_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready)
            ApplyFilters();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // A refresh also re-checks the channels: the guide may have been updated since.
        foreach (var id in _rows.Keys)
            Matches.Remove(id);
        _ = LoadAsync(force: true);
    }

    // ---- Links ----

    private void LinksMenu_Opening(object sender, object e)
    {
        LinksMenu.Items.Clear();
        var links = UserLinks.Load();
        foreach (var link in links)
            LinksMenu.Items.Add(ItemMenu.Item(link.Name, "", () => _ = OpenAsync(link)));
        if (links.Count == 0)
            LinksMenu.Items.Add(new MenuFlyoutItem { Text = "No links yet", IsEnabled = false });
        LinksMenu.Items.Add(new MenuFlyoutSeparator());
        LinksMenu.Items.Add(ItemMenu.Item("Add a link…", "", () => _ = AddLinkAsync()));
        if (links.Count > 0)
        {
            var remove = new MenuFlyoutSubItem { Text = "Remove a link", Icon = new FontIcon { Glyph = "" } };
            foreach (var link in links)
                remove.Items.Add(ItemMenu.Item(link.Name, "", () => UserLinks.Save(UserLinks.Load().Where(l => l != link).ToList())));
            LinksMenu.Items.Add(remove);
        }
    }

    private static async Task OpenAsync(UserLink link)
    {
        if (UserLinks.Normalise(link.Url) is { } uri)
            await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private async Task AddLinkAsync()
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "e.g. Fixtures" };
        var url = new TextBox { Header = "Address", PlaceholderText = "https://…" };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(name);
        panel.Children.Add(url);
        panel.Children.Add(new TextBlock { Text = "Links open in your web browser.", Opacity = 0.7 });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add a link",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (UserLinks.Normalise(url.Text) is not { } uri)
        {
            App.Window.ShowNotice(InfoBarSeverity.Warning, "That doesn't look like a web address.");
            return;
        }
        var title = name.Text.Trim() is { Length: > 0 } n ? n : uri.Host;
        UserLinks.Save([.. UserLinks.Load(), new UserLink(title, uri.AbsoluteUri)]);
    }
}
