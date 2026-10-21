using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Antenna.Core;
using Antenna.Core.Sports;

namespace Antenna.Mobile.Services;

/// <summary>
/// While a followed team plays, checks the score every minute and posts kick-off, goal and full-time
/// notifications ("Watch" opens the channel showing it). Runs as a foreground service so Android keeps it
/// alive with the app closed; an exact alarm starts it shortly before the next followed kick-off.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class ScoreAlertService : Service
{
    public const string ChannelId = "scores";
    private const string OngoingChannelId = "following";
    private const int OngoingId = 1;
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Ahead = TimeSpan.FromMinutes(20);

    private CancellationTokenSource? _cts;
    private int _nextId = 100;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannels(this);
        var ongoing = Ongoing("Following your teams…");
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(OngoingId, ongoing, ForegroundService.TypeDataSync);
        else
            StartForeground(OngoingId, ongoing);
        if (_cts is null)
        {
            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        _cts?.Cancel();
        _cts = null;
        base.OnDestroy();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var feed = new EspnFixtures(MobileServices.Http);
        var before = new Dictionary<string, Fixture>();
        var channels = new Dictionary<string, LibraryEntry?>();
        Log.Info("Score alerts: started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var teams = ScoreAlerts.Followed(MobileServices.Library);
                var feeds = EspnFixtures.Feeds.Where(f => teams.Any(t => t.FeedPath == f.Path)).ToList();
                if (feeds.Count == 0)
                    break;

                var today = DateOnly.FromDateTime(DateTime.Today);
                IReadOnlyList<Fixture> fixtures;
                try
                {
                    fixtures = [.. (await feed.GetDayAsync(today.AddDays(-1), feeds, ct)).Concat(await feed.GetDayAsync(today, feeds, ct))
                        .Where(f => ScoreAlerts.Involves(f, teams))];
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(Poll, ct);
                    continue;
                }

                foreach (var e in ScoreAlerts.Changes(before, fixtures, teams))
                {
                    if (!channels.TryGetValue(e.Fixture.Id, out var channel))
                        channels[e.Fixture.Id] = channel = FixtureMatcher.Find(MobileServices.Library, e.Fixture).Channels.FirstOrDefault();
                    Notify(e, channel);
                }
                foreach (var f in fixtures)
                    before[f.Id] = f;

                var live = fixtures.Where(f => f.State == FixtureState.Live).ToList();
                var soon = fixtures.Where(f => f.State == FixtureState.Scheduled && f.Start - DateTimeOffset.Now < Ahead).ToList();
                if (live.Count == 0 && soon.Count == 0)
                    break;  // nothing on now: sleep until the next kick-off
                UpdateOngoing(live.Count > 0
                    ? string.Join(" · ", live.Select(f => $"{f.Home} {f.HomeScore}–{f.AwayScore} {f.Away} ({f.Status})"))
                    : $"{soon[0].Title} at {soon[0].Start.LocalDateTime:HH:mm}");
                await Task.Delay(Poll, ct);
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Score alerts failed", ex);
        }
        Log.Info("Score alerts: stopped");
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
        _ = ScoreAlertScheduler.ScheduleAsync(this);
    }

    private void Notify(ScoreEvent e, LibraryEntry? channel)
    {
        Log.Info($"Score alert: {e.Title} — {e.Text}");
        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Mipmap.appicon)!
            .SetContentTitle(e.Title)!
            .SetContentText(e.Text)!
            .SetAutoCancel(true)!
            .SetPriority(NotificationCompat.PriorityHigh)!
            .SetContentIntent(OpenIntent(e.Fixture.Id, null))!;
        if (channel is not null && e.Kind != ScoreEventKind.FullTime)
            builder.AddAction(0, $"Watch on {channel.Item.Title}", OpenIntent(e.Fixture.Id, channel.Item));
        NotificationManagerCompat.From(this)!.Notify(_nextId++, builder.Build()!);
    }

    private PendingIntent OpenIntent(string fixtureId, MediaItem? watch)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        intent.PutExtra(MainActivity.FixtureExtra, fixtureId);
        if (watch is not null)
            intent.PutExtra(MainActivity.WatchExtra, $"{watch.SourceId}|{watch.ExternalId}");
        return PendingIntent.GetActivity(this, _nextId++ + (watch is null ? 0 : 50_000), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private Notification Ongoing(string text) =>
        new NotificationCompat.Builder(this, OngoingChannelId)
            .SetSmallIcon(Resource.Mipmap.appicon)!
            .SetContentTitle("Following your teams")!
            .SetContentText(text)!
            .SetOngoing(true)!
            .SetSilent(true)!
            .SetContentIntent(OpenIntent("", null))!
            .Build()!;

    private void UpdateOngoing(string text) => NotificationManagerCompat.From(this)!.Notify(OngoingId, Ongoing(text));

    public static void CreateChannels(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || context.GetSystemService(NotificationService) is not NotificationManager manager)
            return;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Goals and results", NotificationImportance.High)
        {
            Description = "Kick-off, goals and full time for the teams you follow",
        });
        manager.CreateNotificationChannel(new NotificationChannel(OngoingChannelId, "Following a match", NotificationImportance.Low)
        {
            Description = "Shown while Antenna checks the score of a followed match",
        });
    }
}

/// <summary>Starts the score service now if a followed team is playing, else sets an alarm for shortly before the next kick-off.</summary>
public static class ScoreAlertScheduler
{
    private const int AlarmRequest = 7001;

    public static async Task ScheduleAsync(Context context)
    {
        try
        {
            var teams = ScoreAlerts.Followed(MobileServices.Library);
            var alarms = (AlarmManager)context.GetSystemService(Context.AlarmService)!;
            var alarm = PendingIntent.GetBroadcast(context, AlarmRequest, new Intent(context, typeof(ScoreAlarmReceiver)),
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
            alarms.Cancel(alarm);
            var feeds = EspnFixtures.Feeds.Where(f => teams.Any(t => t.FeedPath == f.Path)).ToList();
            if (feeds.Count == 0)
                return;

            var feed = new EspnFixtures(MobileServices.Http);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var fixtures = new List<Fixture>();
            for (var day = -1; day <= 3; day++)
                fixtures.AddRange((await feed.GetDayAsync(today.AddDays(day), feeds)).Where(f => ScoreAlerts.Involves(f, teams)));

            var now = DateTimeOffset.Now;
            if (fixtures.Any(f => f.State == FixtureState.Live || f.State == FixtureState.Scheduled && f.Start - now < TimeSpan.FromMinutes(20)))
            {
                Start(context);
                return;
            }
            if (fixtures.Where(f => f.State == FixtureState.Scheduled).OrderBy(f => f.Start).FirstOrDefault() is not { } next)
                return;

            var at = next.Start.AddMinutes(-10).ToUnixTimeMilliseconds();
            if (OperatingSystem.IsAndroidVersionAtLeast(31) && !alarms.CanScheduleExactAlarms())
                alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, at, alarm);
            else
                alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, at, alarm);
            Log.Info($"Score alerts: next is {next.Title} at {next.Start.LocalDateTime:ddd HH:mm}");
        }
        catch (Exception ex)
        {
            Log.Info($"Score alerts: couldn't schedule: {ex.Message}");
        }
    }

    public static void Start(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(ScoreAlertService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch (Exception ex)
        {
            // Android can refuse a foreground service from the background; the next app start tries again.
            Log.Info($"Score alerts: couldn't start: {ex.Message}");
        }
    }
}

[BroadcastReceiver(Exported = false)]
public sealed class ScoreAlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null)
            ScoreAlertScheduler.Start(context);
    }
}
