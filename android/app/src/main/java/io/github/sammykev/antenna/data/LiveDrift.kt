package io.github.sammykev.antenna.data

import java.time.Duration
import java.time.Instant

/**
 * How far live playback has slipped behind the stream since it (re)started (LiveDrift.cs). Every stall to rebuffer adds
 * to it; once it's more than [MAX_BEHIND] the player reconnects, which picks the stream up at its live edge.
 */
class LiveDrift {
    companion object {
        /** Keeps what the app adds well under the 25 seconds people accept for live sport. */
        val MAX_BEHIND: Duration = Duration.ofSeconds(12)
    }

    private var wallStart: Instant? = null
    private var mediaStart = 0L

    /** Starts counting afresh: after (re)connecting, or when the viewer resumes from a pause on purpose. */
    fun reset() {
        wallStart = null
    }

    /** How far behind it has fallen since the last reset. */
    fun update(now: Instant, mediaTimeMs: Long): Duration {
        val start = wallStart ?: return rebase(now, mediaTimeMs)
        val behind = Duration.between(start, now).minusMillis(mediaTimeMs - mediaStart)
        // Media time running ahead (a reconnect skipped forward) or going back (a new stream): count from here.
        if (behind.isNegative || mediaTimeMs < mediaStart) return rebase(now, mediaTimeMs)
        return behind
    }

    fun shouldCatchUp(now: Instant, mediaTimeMs: Long) = update(now, mediaTimeMs) > MAX_BEHIND

    private fun rebase(now: Instant, mediaTimeMs: Long): Duration {
        wallStart = now
        mediaStart = mediaTimeMs
        return Duration.ZERO
    }
}
