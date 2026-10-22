using Antenna.Core.Playback;

namespace Antenna.Core.Tests;

public class LiveDriftTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Smooth_playback_stays_live_and_stalls_add_up()
    {
        var drift = new LiveDrift();
        Assert.Equal(TimeSpan.Zero, drift.Update(T0, 5_000));
        Assert.Equal(TimeSpan.Zero, drift.Update(T0.AddSeconds(60), 65_000));

        // Three 5-second stalls: the picture is 15 seconds behind where it would be.
        Assert.Equal(TimeSpan.FromSeconds(15), drift.Update(T0.AddSeconds(120), 110_000));
        Assert.True(drift.ShouldCatchUp(T0.AddSeconds(121), 111_000));
    }

    [Fact]
    public void A_short_stall_is_tolerated()
    {
        var drift = new LiveDrift();
        drift.Update(T0, 0);
        Assert.False(drift.ShouldCatchUp(T0.AddSeconds(30), 22_000)); // 8 s behind.
    }

    [Fact]
    public void Resets_and_new_streams_count_from_scratch()
    {
        var drift = new LiveDrift();
        drift.Update(T0, 50_000);
        drift.Reset(); // The user paused for a minute on purpose, then resumed.
        Assert.Equal(TimeSpan.Zero, drift.Update(T0.AddSeconds(70), 50_000));
        Assert.Equal(TimeSpan.Zero, drift.Update(T0.AddSeconds(80), 60_000));

        Assert.Equal(TimeSpan.Zero, drift.Update(T0.AddSeconds(90), 1_000)); // Reconnected: time starts again.
        Assert.Equal(TimeSpan.FromSeconds(2), drift.Update(T0.AddSeconds(100), 9_000));
    }
}
