using Antenna.Core;
using Antenna.Core.Recordings;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Antenna.App.Services;

/// <summary>
/// "Remind me" for upcoming programmes: while Antenna is open, a Windows notification (and an in-app
/// notice) appears shortly before the programme starts, with Watch now.
/// </summary>
internal static class Reminders
{
    private static DispatcherQueue? _queue;
    private static DispatcherQueueTimer? _timer;
    private static bool _notifications;

    public static void Start(DispatcherQueue queue)
    {
        _queue = queue;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _notifications = true;
        }
        catch (Exception ex)
        {
            // Windows notifications unavailable: the in-app notice still shows.
            Log.Error(ex, "reminders: Windows notifications");
        }

        foreach (var expired in ReminderPlanner.Expired(AppServices.Library.GetReminders(), DateTimeOffset.Now))
            AppServices.Library.DeleteReminder(expired.Id);

        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(20);
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        Check();
    }

    public static bool IsSet(string sourceId, string channelId, Programme programme) =>
        AppServices.Library.HasReminder(Reminder.KeyFor(sourceId, channelId, programme.Start));

    /// <summary>Turns the reminder for a programme on or off. Returns true when it's now on.</summary>
    public static bool Toggle(MediaItem channel, Programme programme)
    {
        var reminder = new Reminder
        {
            SourceId = channel.SourceId,
            ChannelId = channel.ExternalId,
            ChannelTitle = channel.Title,
            Title = programme.Title,
            Start = programme.Start,
            Stop = programme.Stop,
        };
        if (AppServices.Library.HasReminder(reminder.Id))
        {
            AppServices.Library.DeleteReminder(reminder.Id);
            Log.Info($"reminders: cancelled '{reminder.Title}'");
            return false;
        }
        AppServices.Library.SaveReminder(reminder);
        Log.Info($"reminders: set '{reminder.Title}' at {reminder.Start:ddd HH:mm} on {reminder.ChannelTitle}");
        Check();
        return true;
    }

    private static void Check()
    {
        try
        {
            foreach (var reminder in ReminderPlanner.Due(AppServices.Library.GetReminders(), DateTimeOffset.Now))
            {
                AppServices.Library.DeleteReminder(reminder.Id);
                Fire(reminder);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "reminders");
        }
    }

    private static void Fire(Reminder reminder)
    {
        var now = DateTimeOffset.Now;
        var when = reminder.Start > now
            ? $"Starts at {reminder.Start.LocalDateTime:HH:mm} on {reminder.ChannelTitle}"
            : $"Started at {reminder.Start.LocalDateTime:HH:mm} on {reminder.ChannelTitle}";
        Log.Info($"reminders: '{reminder.Title}', {when}");

        App.Window.ShowReminder(reminder.Title, when, () => Watch(reminder.SourceId, reminder.ChannelId));
        if (!_notifications)
            return;
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "watch")
                .AddArgument("source", reminder.SourceId)
                .AddArgument("channel", reminder.ChannelId)
                .AddText(reminder.Title)
                .AddText(when)
                .AddButton(new AppNotificationButton("Watch now")
                    .AddArgument("action", "watch")
                    .AddArgument("source", reminder.SourceId)
                    .AddArgument("channel", reminder.ChannelId))
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "reminders: show notification");
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out var action) && action == "watch"
            && args.Arguments.TryGetValue("source", out var source) && args.Arguments.TryGetValue("channel", out var channel))
        {
            _queue?.TryEnqueue(() =>
            {
                App.Window.Activate();
                Watch(source, channel);
            });
        }
    }

    private static void Watch(string sourceId, string channelId)
    {
        if (AppServices.Library.GetItem(sourceId, channelId, MediaKind.Live) is not { } entry)
        {
            App.Window.ShowNotice(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, "That channel is no longer in your playlists.");
            return;
        }
        Playback.Play(PlayRequest.ForItem(entry.Item));
    }
}
