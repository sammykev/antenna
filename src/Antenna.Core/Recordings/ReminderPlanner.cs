namespace Antenna.Core.Recordings;

/// <summary>When reminders go off.</summary>
public static class ReminderPlanner
{
    /// <summary>How long before the start the notification appears.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Reminders to show now: starting within <see cref="Lead"/>, or already started but not over
    /// (Antenna was opened late), so a match in progress is still announced.
    /// </summary>
    public static IEnumerable<Reminder> Due(IEnumerable<Reminder> reminders, DateTimeOffset now) =>
        reminders.Where(r => r.Start - Lead <= now && r.Stop > now);

    /// <summary>Reminders for programmes that have already finished.</summary>
    public static IEnumerable<Reminder> Expired(IEnumerable<Reminder> reminders, DateTimeOffset now) =>
        reminders.Where(r => r.Stop <= now);
}
