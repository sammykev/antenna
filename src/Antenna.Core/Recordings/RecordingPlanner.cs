namespace Antenna.Core.Recordings;

/// <summary>Decides what the recorder should do at a moment in time. Pure, so it's easy to test.</summary>
public static class RecordingPlanner
{
    /// <summary>Extra time recorded around a guide programme, since broadcasts rarely run exactly to schedule.</summary>
    public static readonly TimeSpan PaddingBefore = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan PaddingAfter = TimeSpan.FromMinutes(5);

    public static IEnumerable<Recording> DueToStart(IEnumerable<Recording> all, DateTimeOffset now) =>
        all.Where(r => r.Status == RecordingStatus.Scheduled && r.Start <= now && (r.Stop is null || r.Stop > now));

    /// <summary>Scheduled recordings whose whole window passed while the app wasn't running.</summary>
    public static IEnumerable<Recording> Missed(IEnumerable<Recording> all, DateTimeOffset now) =>
        all.Where(r => r.Status == RecordingStatus.Scheduled && r.Stop is { } stop && stop <= now);

    public static bool ShouldStop(Recording rec, DateTimeOffset now) =>
        rec.Status == RecordingStatus.Recording && rec.Stop is { } stop && stop <= now;

    /// <summary>A recording window for a guide programme, with padding.</summary>
    public static (DateTimeOffset Start, DateTimeOffset Stop) Window(Programme programme) =>
        (programme.Start - PaddingBefore, programme.Stop + PaddingAfter);

    /// <summary>A file-system-safe file name like "BBC One - News 2026-10-14 2000.ts".</summary>
    public static string FileName(string title, DateTimeOffset start, string extension)
    {
        var safe = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
        if (safe.Length > 120)
            safe = safe[..120];
        return $"{safe} {start.LocalDateTime:yyyy-MM-dd HHmm}.{extension.TrimStart('.')}";
    }
}
