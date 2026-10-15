using Antenna.Core.Library;
using Antenna.Core.Recordings;

namespace Antenna.Core.Tests;

public sealed class ReminderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"antenna-rem-{Guid.NewGuid():N}.db");
    private readonly LibraryStore _store;
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    public ReminderTests() => _store = new LibraryStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(f);
    }

    private static Reminder At(int startMinutes, int lengthMinutes = 90, string title = "Man Utd v Man City") => new()
    {
        SourceId = "s", ChannelId = "42", ChannelTitle = "Sky Sports Main Event", Title = title,
        Start = Now.AddMinutes(startMinutes), Stop = Now.AddMinutes(startMinutes + lengthMinutes),
    };

    [Fact]
    public void Saves_lists_and_deletes_reminders()
    {
        var match = At(30);
        _store.SaveReminder(match);
        _store.SaveReminder(match); // Same programme twice is one reminder.
        _store.SaveReminder(At(120, title: "Arsenal v Spurs"));

        Assert.True(_store.HasReminder(match.Id));
        Assert.Equal(["Man Utd v Man City", "Arsenal v Spurs"], _store.GetReminders().Select(r => r.Title));
        Assert.Equal(match.Start, _store.GetReminders()[0].Start);

        _store.DeleteReminder(match.Id);
        Assert.False(_store.HasReminder(match.Id));
        Assert.Single(_store.GetReminders());
    }

    [Fact]
    public void Due_two_minutes_before_and_while_it_is_on()
    {
        Reminder[] all = [At(10), At(2), At(1), At(-30), At(-100)];

        var due = ReminderPlanner.Due(all, Now).Select(r => r.Start).ToList();
        var expired = ReminderPlanner.Expired(all, Now).Select(r => r.Start).ToList();

        Assert.Equal([Now.AddMinutes(2), Now.AddMinutes(1), Now.AddMinutes(-30)], due);
        Assert.Equal([Now.AddMinutes(-100)], expired);
    }
}
