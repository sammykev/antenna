package io.github.sammykev.antenna.data

import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.time.Duration
import java.time.Instant
import java.time.OffsetDateTime

data class RefreshResult(val items: Int, val programmes: Int, val guideError: String?)

/**
 * Re-imports a playlist's catalogue and then its guide (SourceRefresher.cs + PlaylistImporter.cs + EpgImporter.cs).
 * Xtream catalogues are saved in parts (live first) so Live TV is usable while films and series download.
 */
class Refresher(private val http: OkHttpClient, private val store: LibraryStore, private val tempDir: File) {
    /** Called (on a background thread) whenever part of a catalogue or a guide has been saved. */
    var onSaved: () -> Unit = {}

    private val busy = Any()

    /** One refresh at a time (a manual one waits for an automatic one): each holds a whole catalogue in memory. */
    fun refresh(source: PlaylistSource): RefreshResult = synchronized(busy) { refreshNow(source) }

    private fun refreshNow(source: PlaylistSource): RefreshResult {
        var epgUrl: String?
        val all: List<MediaItem>
        when (source.kind) {
            SourceKind.M3u -> {
                val playlist = M3uParser.parse(readM3u(source.location), source.id)
                store.replaceItems(source.id, playlist.items)
                onSaved()
                all = playlist.items
                epgUrl = source.epgUrl ?: playlist.epgUrl
            }
            SourceKind.Xtream -> {
                val client = XtreamClient.forSource(http, source)
                client.authenticate()
                val live = client.getLive(source.id)
                store.replaceItems(source.id, live, MediaKind.Live)
                onSaved()
                // One after the other: big catalogues side by side ran phones out of memory.
                val movies = client.getMovies(source.id).also { store.replaceItems(source.id, it, MediaKind.Movie); onSaved() }
                val series = client.getSeries(source.id).also { store.replaceItems(source.id, it, MediaKind.Series); onSaved() }
                all = live + movies + series
                epgUrl = source.epgUrl ?: client.xmltvUrl
            }
        }
        // Xtream guide URLs embed the password, so they're derived at import time, never stored.
        val saved = source.copy(
            epgUrl = if (source.kind != SourceKind.Xtream) epgUrl else source.epgUrl?.takeUnless { it.contains("xmltv.php", true) },
            lastRefreshed = OffsetDateTime.now(),
        )
        store.saveSource(saved)
        onSaved()
        return try {
            val programmes = importGuide(source.id, epgUrl, all)
            onSaved()
            RefreshResult(all.size, programmes, null)
        } catch (e: Exception) {
            RefreshResult(all.size, 0, e.message ?: e.javaClass.simpleName)
        }
    }

    private fun importGuide(sourceId: String, epgUrl: String?, items: List<MediaItem>): Int {
        if (epgUrl.isNullOrBlank()) return 0
        val live = items.filter { it.kind == MediaKind.Live }
        if (live.isEmpty()) return 0
        // Keep as much history as the longest catch-up archive (at least a few hours, for "what was just on").
        val now = Instant.now()
        val archiveDays = live.maxOf { it.catchupDays }.coerceIn(0, 14)
        val from = if (archiveDays > 0) now.minus(Duration.ofDays(archiveDays.toLong())) else now.minus(Duration.ofHours(6))
        val to = now.plus(Duration.ofDays(3))
        XmltvParser.open(http, epgUrl, tempDir).use { input ->
            store.beginGuideImport(sourceId).use { writer ->
                XmltvParser.parse(input, { channels ->
                    val map = if (channels.isNotEmpty()) EpgMatcher.match(live, channels)
                        // Guides without <channel> entries: trust the playlist's tvg-ids as-is.
                        else live.filter { it.epgChannelId != null }.associate { it.externalId to it.epgChannelId!! }
                    for ((externalId, channelId) in map) writer.setEpgChannel(externalId, channelId)
                    val wanted = map.values.toHashSet()
                    wanted::contains
                }, writer::add, from, to)
                writer.commit()
                return writer.count
            }
        }
    }

    private fun readM3u(location: String): String =
        if (location.startsWith("http://", true) || location.startsWith("https://", true))
            http.newCall(Request.Builder().url(location).build()).execute().use { r ->
                if (!r.isSuccessful) throw java.io.IOException("The playlist server answered ${r.code}.")
                r.body!!.string()
            }
        else File(location.removePrefix("file://")).readText()
}
