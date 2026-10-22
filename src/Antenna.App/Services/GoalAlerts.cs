using Antenna.Core;
using Antenna.Core.Sports;
using Microsoft.UI.Dispatching;

namespace Antenna.App.Services;

/// <summary>
/// While Antenna is open, kick-off, goal and full-time notifications for followed teams (follow them on the
/// Matches page). Checks every minute while one of them plays, otherwise every 15 minutes.
/// </summary>
internal static class GoalAlerts
{
    private static readonly EspnFixtures Feed = new(AppServices.Http);
    private static readonly Dictionary<string, Fixture> Before = [];
    private static readonly Dictionary<string, LibraryEntry?> Channels = [];
    private static DispatcherQueueTimer? _timer;
    private static DateTimeOffset _nextQuietCheck;
    private static bool _running;

    public static void Start(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    /// <summary>After following someone: look now rather than at the next quiet check.</summary>
    public static void CheckSoon() => _nextQuietCheck = DateTimeOffset.MinValue;

    private static async Task CheckAsync()
    {
        var teams = ScoreAlerts.Followed(AppServices.Library);
        if (_running || teams.Count == 0 || DateTimeOffset.Now < _nextQuietCheck)
            return;
        _running = true;
        try
        {
            var feeds = EspnFixtures.Feeds.Where(f => teams.Any(t => t.FeedPath == f.Path)).ToList();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var fixtures = (await Feed.GetDayAsync(today.AddDays(-1), feeds)).Concat(await Feed.GetDayAsync(today, feeds))
                .Where(f => ScoreAlerts.Involves(f, teams))
                .ToList();
            foreach (var e in ScoreAlerts.Changes(Before, fixtures, teams))
            {
                if (!Channels.TryGetValue(e.Fixture.Id, out var channel))
                    Channels[e.Fixture.Id] = channel = await Task.Run(() => FixtureMatcher.Find(AppServices.Library, e.Fixture) is { IsCoverage: false } m ? m.Channels.FirstOrDefault() : null);
                Log.Info($"goal alerts: {e.Title}: {e.Text}");
                var watch = e.Kind == ScoreEventKind.FullTime ? null : channel;
                Reminders.Notify(e.Title, e.Text, watch?.Item.SourceId, watch?.Item.ExternalId, watch is null ? "Watch now" : $"Watch on {watch.Item.Title}");
            }
            foreach (var f in fixtures)
                Before[f.Id] = f;

            // Every minute while a followed team plays (or is about to), else every 15.
            var busy = fixtures.Any(f => f.State == FixtureState.Live
                || f.State == FixtureState.Scheduled && f.Start - DateTimeOffset.Now < TimeSpan.FromMinutes(20));
            _nextQuietCheck = busy ? DateTimeOffset.MinValue : DateTimeOffset.Now.AddMinutes(15);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info($"goal alerts: {ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }
}
