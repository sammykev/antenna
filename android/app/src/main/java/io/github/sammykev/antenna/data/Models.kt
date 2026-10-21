package io.github.sammykev.antenna.data

import java.time.Duration
import java.time.Instant
import java.util.UUID

// The same model as the Windows app's Antenna.Core (Models.cs); the database and the sync file use these shapes.

enum class SourceKind { M3u, Xtream }

enum class MediaKind { Live, Movie, Series }

enum class GroupFlag(val stored: String) { Hidden("hidden"), Locked("locked"), Pinned("pinned") }

/** A playlist the user has added: an M3U URL/file or an Xtream Codes account. */
data class PlaylistSource(
    val id: String = UUID.randomUUID().toString().replace("-", ""),
    val name: String,
    val kind: SourceKind,
    /** M3U: http(s) URL or local file path. Xtream: server base URL. */
    val location: String,
    val username: String? = null,
    val password: String? = null,
    /** Optional XMLTV guide URL. */
    val epgUrl: String? = null,
    val color: String = "#4F7CFF",
    val icon: String = "",
    val lastRefreshed: java.time.OffsetDateTime? = null,
)

/** A playable entry: live channel, movie, or series (series resolve to episodes on demand). */
data class MediaItem(
    val sourceId: String,
    /** The Xtream stream/series id, or the M3U stream URL. */
    val externalId: String,
    val kind: MediaKind,
    val title: String,
    val group: String = "",
    val logoUrl: String? = null,
    val streamUrl: String? = null,
    val epgChannelId: String? = null,
    val catchupDays: Int = 0,
    val catchupMode: String? = null,
    val catchupSource: String? = null,
    /** libVLC options, one per line (":http-user-agent=…"). */
    val playerOptions: String? = null,
    val sortIndex: Int = 0,
) {
    val options: List<String> get() = playerOptions?.split('\n')?.filter { it.isNotBlank() } ?: emptyList()
}

data class LibraryEntry(val item: MediaItem, val sourceName: String, val sourceColor: String, val sourceIcon: String)

data class Episode(
    val id: String,
    val season: Int,
    val number: Int,
    val title: String,
    val streamUrl: String,
    val plot: String? = null,
    val imageUrl: String? = null,
)

data class MediaDetails(
    val plot: String? = null,
    val cast: String? = null,
    val director: String? = null,
    val genre: String? = null,
    val released: String? = null,
    val rating: String? = null,
    val duration: String? = null,
    val posterUrl: String? = null,
    val backdropUrl: String? = null,
)

/** One programme from the XMLTV guide. */
data class Programme(
    val channelId: String,
    val start: Instant,
    val stop: Instant,
    val title: String,
    val description: String? = null,
) {
    val duration: Duration get() = Duration.between(start, stop)

    fun isOnAt(time: Instant) = !start.isAfter(time) && time.isBefore(stop)

    /** How far through it is at [time], 0..1. */
    fun progress(time: Instant = Instant.now()): Float {
        val total = duration.toMillis().coerceAtLeast(1)
        return (Duration.between(start, time).toMillis().toFloat() / total).coerceIn(0f, 1f)
    }
}

/** Something the user watched: drives Continue watching and resume. Episodes use "ep:{id}". */
data class WatchRecord(
    val sourceId: String,
    val externalId: String,
    val kind: MediaKind,
    val title: String,
    val streamUrl: String,
    val logoUrl: String? = null,
    val playerOptions: String? = null,
    val positionMs: Long = 0,
    val lengthMs: Long = 0,
    val watchedAt: Instant = Instant.now(),
) {
    /** Worth offering "continue": started properly and not (nearly) finished. */
    val isResumable get() = kind != MediaKind.Live && positionMs > 60_000 && lengthMs > 0 && positionMs < lengthMs * 0.95

    val progress: Float get() = if (lengthMs > 0) (positionMs.toFloat() / lengthMs).coerceIn(0f, 1f) else 0f
}

data class LibraryCounts(val live: Int, val movies: Int, val series: Int, val playlists: Int)

/** A guide search hit: the programme and the channels showing it. */
data class EventHit(val programme: Programme, val channels: List<LibraryEntry>) {
    fun isLive(now: Instant) = programme.isOnAt(now)
}
