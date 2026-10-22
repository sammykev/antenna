package io.github.sammykev.antenna

import io.github.sammykev.antenna.data.LiveDrift
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.Instant

/** Mirrors LiveDriftTests.cs. */
class LiveDriftTests {
    private val t0 = Instant.parse("2026-10-22T20:00:00Z")
    private fun at(seconds: Long) = t0.plusSeconds(seconds)

    @Test
    fun smoothPlaybackStaysLiveAndStallsAddUp() {
        val drift = LiveDrift()
        assertEquals(Duration.ZERO, drift.update(t0, 5_000))
        assertEquals(Duration.ZERO, drift.update(at(60), 65_000))
        assertEquals(Duration.ofSeconds(15), drift.update(at(120), 110_000))
        assertTrue(drift.shouldCatchUp(at(121), 111_000))
    }

    @Test
    fun aShortStallIsTolerated() {
        val drift = LiveDrift()
        drift.update(t0, 0)
        assertFalse(drift.shouldCatchUp(at(30), 22_000))
    }

    @Test
    fun resetsAndNewStreamsCountFromScratch() {
        val drift = LiveDrift()
        drift.update(t0, 50_000)
        drift.reset()
        assertEquals(Duration.ZERO, drift.update(at(70), 50_000))
        assertEquals(Duration.ZERO, drift.update(at(80), 60_000))
        assertEquals(Duration.ZERO, drift.update(at(90), 1_000))
        assertEquals(Duration.ofSeconds(2), drift.update(at(100), 9_000))
    }
}
