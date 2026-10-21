using Antenna.Core;
using Antenna.Core.Sports;
using Antenna.Mobile.Services;

namespace Antenna.Mobile.Views;

/// <summary>
/// Today's and the next days' fixtures with live scores (ESPN's public scoreboard), each matched to your own
/// channels, as on Windows. Filter by day, sport, and to matches on your channels.
/// </summary>
public partial class MatchesPage : ContentPage
{
    private const int DaysAhead = 3;
    private const string AllSports = "All sports";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);
    private static readonly Dictionary<DateOnly, (IReadOnlyList<Fixture> Fixtures, DateTimeOffset At)> Cache = [];
    private static readonly Dictionary<string, FixtureMatch> Found = [];

    private readonly EspnFixtures _feed = new(MobileServices.Http);
    private readonly List<MatchRow> _rows = [];
    private IDispatcherTimer? _timer;
    private int _version;
    private bool _ready;

    public MatchesPage()
    {
        InitializeComponent();
        var today = DateTime.Today;
        var days = Enumerable.Range(0, DaysAhead)
            .Select(i => new GroupChip(i switch { 0 => "Today", 1 => "Tomorrow", _ => today.AddDays(i).ToString("ddd d MMM") }, i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        Days.ItemsSource = days;
        Days.SelectedItem = days[0];

        var sports = new[] { AllSports }.Concat(EspnFixtures.Feeds.Select(f => f.Sport).Distinct()).ToList();
        SportPicker.ItemsSource = sports;
        SportPicker.SelectedIndex = Math.Max(0, sports.IndexOf(Preferences.Get("matches.sport", AllSports)));
        MineOnly.IsToggled = Preferences.Get("matches.mine", false);
        _ready = true;
    }

    private int DayOffset => Days.SelectedItem is GroupChip { Group: { } g } ? int.Parse(g, System.Globalization.CultureInfo.InvariantCulture) : 0;

    private DateOnly SelectedDay => DateOnly.FromDateTime(DateTime.Today.AddDays(DayOffset));

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _timer ??= CreateTimer();
        _timer.Start();
        await LoadAsync(force: false);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _version++;
    }

    private IDispatcherTimer CreateTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(1);
        timer.Tick += async (_, _) => await RefreshScoresAsync();
        return timer;
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
        Busy.IsRunning = true;
        StatusText.Text = "Loading fixtures…";
        IReadOnlyList<Fixture> fixtures;
        try
        {
            fixtures = await FixturesAsync(SelectedDay, force);
        }
        catch (Exception ex)
        {
            Log.Info($"matches: couldn't load fixtures: {ex.Message}");
            fixtures = [];
        }
        if (version != _version)
            return;

        _rows.Clear();
        var teams = ScoreAlerts.Followed(MobileServices.Library);
        _rows.AddRange(fixtures.Select(f => new MatchRow(f) { IsFollowed = ScoreAlerts.Involves(f, teams) }));
        foreach (var row in _rows)
        {
            if (Found.TryGetValue(row.Fixture.Id, out var known))
                row.Found(known);
        }
        ApplyFilters();
        if (fixtures.Count == 0)
        {
            Busy.IsRunning = false;
            StatusText.Text = "";
            EmptyText.Text = "Couldn't load fixtures for this day, or there are none. Check your connection and tap Refresh.";
            return;
        }

        // Find each match on your channels, live and upcoming first.
        StatusText.Text = "Finding matches on your channels…";
        foreach (var row in _rows.Where(r => r.Match is null)
                     .OrderBy(r => r.Fixture.State switch { FixtureState.Live => 0, FixtureState.Scheduled => 1, _ => 2 }).ToList())
        {
            if (version != _version)
                return;
            FixtureMatch match;
            try
            {
                var fixture = row.Fixture;
                match = await Task.Run(() => FixtureMatcher.Find(MobileServices.Library, fixture));
            }
            catch (Exception ex)
            {
                Log.Error("matches: finding a fixture", ex);
                match = new FixtureMatch(row.Fixture, [], null);
            }
            Found[row.Fixture.Id] = match;
            row.Found(match);
        }
        if (version != _version)
            return;
        Busy.IsRunning = false;
        StatusText.Text = $"{fixtures.Count} fixtures · {_rows.Count(r => r.OnYourChannels)} on your channels · scores from ESPN";
        if (MineOnly.IsToggled)
            ApplyFilters();  // matches found since the list was first shown
    }

    /// <summary>Updates scores in place every minute (today only).</summary>
    private async Task RefreshScoresAsync()
    {
        if (DayOffset != 0 || !_rows.Any(r => r.Fixture.State != FixtureState.Finished))
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
        var byId = _rows.ToDictionary(r => r.Fixture.Id);
        foreach (var f in fixtures)
        {
            if (byId.TryGetValue(f.Id, out var row))
                row.Update(f);
        }
    }

    // ---- Filters ----

    private void ApplyFilters()
    {
        var sport = SportPicker.SelectedItem as string is { } s && s != AllSports ? s : null;
        var mine = MineOnly.IsToggled;
        var shown = _rows.Where(r => (sport is null || r.Fixture.Sport == sport) && (!mine || r.OnYourChannels)).ToList();
        var groups = new List<MatchGroup>();
        if (shown.Any(r => r.IsFollowed))
            groups.Add(new MatchGroup("★ Your teams", shown.Where(r => r.IsFollowed)));
        groups.AddRange(shown.Where(r => !r.IsFollowed).GroupBy(r => r.Fixture.League).Select(g => new MatchGroup(g.Key, g)));
        List.ItemsSource = groups;
        EmptyText.Text = _rows.Count == 0 ? "" :
            mine ? "None of these fixtures are on your channels (yet: the guide may not list them until closer to the day)."
            : "No fixtures for this sport on this day.";
    }

    private async void Days_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            await LoadAsync(force: false);
    }

    private void Filter_Changed(object? sender, EventArgs e)
    {
        if (!_ready)
            return;
        Preferences.Set("matches.sport", SportPicker.SelectedItem as string ?? AllSports);
        Preferences.Set("matches.mine", MineOnly.IsToggled);
        ApplyFilters();
    }

    private async void Refresh_Clicked(object? sender, EventArgs e)
    {
        // A refresh also re-checks the channels: the guide may have been updated since.
        foreach (var row in _rows)
            Found.Remove(row.Fixture.Id);
        await LoadAsync(force: true);
    }

    // ---- Watching ----

    private async void Match_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not MatchRow row)
            return;
        var f = row.Fixture;
        var match = row.Match;
        var actions = new List<(string Label, Func<Task> Run)>();

        if (match is { OnYourChannels: true })
        {
            var when = f.State == FixtureState.Finished ? "finished" : row.IsOnSoon ? "on now" : $"starts {f.Start.LocalDateTime:ddd HH:mm}";
            foreach (var channel in match.Channels.Take(6))
                actions.Add(($"▶ Watch on {channel.Item.Title} ({when})", () => Playback.PlayAsync([channel.Item], 0)));
        }
        foreach (var team in new[] { f.Home, f.Away }.OfType<string>())
        {
            var followed = ScoreAlerts.IsFollowed(MobileServices.Library, team);
            actions.Add((followed ? $"★ Stop following {team}" : $"☆ Follow {team} (goal alerts)", () => ToggleFollowAsync(team, f.FeedPath)));
        }

        var detail = match is { OnYourChannels: false }
            ? $"Not on your channels{(f.Broadcasts.Count > 0 ? $" (on TV: {string.Join(", ", f.Broadcasts)})" : "")}."
            : null;
        var picked = await DisplayActionSheetAsync(detail is null ? f.Title : $"{f.Title}\n{detail}", "Close", null, [.. actions.Select(a => a.Label)]);
        if (actions.FirstOrDefault(a => a.Label == picked).Run is { } run)
            await run();
    }

    private async Task ToggleFollowAsync(string team, string feedPath)
    {
        var following = ScoreAlerts.Toggle(MobileServices.Library, team, feedPath);
        if (following && OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        var teams = ScoreAlerts.Followed(MobileServices.Library);
        foreach (var row in _rows)
            row.SetFollowed(ScoreAlerts.Involves(row.Fixture, teams));
        ApplyFilters();
        _ = ScoreAlertScheduler.ScheduleAsync(Platform.AppContext);
        if (following)
            await DisplayAlertAsync($"Following {team}", $"You'll get a notification at kick-off, for each goal and at full time in {team}'s matches, even with Antenna closed.", "OK");
    }
}
