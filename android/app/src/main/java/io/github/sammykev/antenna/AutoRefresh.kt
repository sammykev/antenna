package io.github.sammykev.antenna

import java.time.Duration
import java.time.OffsetDateTime
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Keeps playlists and guides fresh while Antenna is open (AutoRefresh.cs on Windows). The guide only reaches three
 * days ahead, so without this Matches and "on now" slowly run dry until the playlist is refreshed by hand.
 */
object AutoRefresh {
    /** How old a playlist may get before it's refreshed again. */
    val maxAge: Duration = Duration.ofHours(12)

    private val running = AtomicBoolean(false)

    /** Refreshes each playlist that's due, one at a time. Blocking: call off the main thread. */
    fun run() {
        if (!running.compareAndSet(false, true)) return
        try {
            val due = OffsetDateTime.now().minus(maxAge)
            for (source in Services.library.getSources().filter { it.lastRefreshed?.isBefore(due) ?: true }) {
                try {
                    val result = Services.refresher.refresh(source)
                    Log.info("Auto refresh: ${source.name}: ${result.items} items, ${result.programmes} programmes" +
                        (result.guideError?.let { ", guide failed: $it" } ?: ""))
                } catch (e: Exception) {
                    Log.info("Auto refresh: couldn't refresh ${source.name}: ${e.message}")
                }
            }
        } finally {
            running.set(false)
        }
    }
}
