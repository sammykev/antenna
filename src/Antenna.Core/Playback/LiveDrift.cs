namespace Antenna.Core.Playback;

/// <summary>
/// How far live playback has slipped behind the stream since it (re)started. Every stall to rebuffer adds to it,
/// and over a match those add up to minutes. Fed the wall clock and the player's media time while playing;
/// once it's more than <see cref="MaxBehind"/> the player reconnects, which picks the stream up at its live edge.
/// </summary>
public sealed class LiveDrift
{
    /// <summary>Keeps what the app adds well under the 25 seconds people accept for live sport.</summary>
    public static readonly TimeSpan MaxBehind = TimeSpan.FromSeconds(12);

    private DateTimeOffset? _wallStart;
    private long _mediaStart;

    /// <summary>Starts counting afresh: after (re)connecting, or when the user resumes from a pause on purpose.</summary>
    public void Reset() => _wallStart = null;

    /// <returns>How far behind it has fallen since the last reset.</returns>
    public TimeSpan Update(DateTimeOffset now, long mediaTimeMs)
    {
        if (_wallStart is not { } start)
        {
            Rebase(now, mediaTimeMs);
            return TimeSpan.Zero;
        }
        var behind = (now - start) - TimeSpan.FromMilliseconds(mediaTimeMs - _mediaStart);
        // Media time running ahead (a reconnect skipped forward) or going back (a new stream): count from here.
        if (behind < TimeSpan.Zero || mediaTimeMs < _mediaStart)
        {
            Rebase(now, mediaTimeMs);
            return TimeSpan.Zero;
        }
        return behind;
    }

    public bool ShouldCatchUp(DateTimeOffset now, long mediaTimeMs) => Update(now, mediaTimeMs) > MaxBehind;

    private void Rebase(DateTimeOffset now, long mediaTimeMs)
    {
        _wallStart = now;
        _mediaStart = mediaTimeMs;
    }
}
