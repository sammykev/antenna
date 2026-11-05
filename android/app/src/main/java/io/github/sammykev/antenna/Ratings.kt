package io.github.sammykev.antenna

import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.OmdbClient
import io.github.sammykev.antenna.data.OmdbException
import io.github.sammykev.antenna.data.Rating
import io.github.sammykev.antenna.data.TitleCleaner
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.io.IOException
import java.time.LocalDate

/**
 * IMDb and Rotten Tomatoes scores for the films in your playlists, and recommendations made from them: the best rated,
 * and ones like a film you watched. Only films you can play are recommended. Needs a free OMDb key; lookups are kept
 * and spread out to stay inside OMDb's 1,000 a day.
 */
object Ratings {
    private const val KEY = "omdb.key"
    private const val DAY = "omdb.day"
    private const val COUNT = "omdb.count"
    /** A little under OMDb's free limit, so opening a film's page can still look it up. */
    private const val DAILY = 900
    private const val PER_RUN = 150
    private const val RETRY_MISSES_MS = 30L * 24 * 3600 * 1000

    private val changes = MutableStateFlow(0)

    /** Bumps when new ratings arrive, so Home can show them. */
    val version: StateFlow<Int> = changes

    /** Why lookups stopped (a wrong key, or the day's limit), for the settings card. */
    @Volatile var problem: String? = null
        private set

    var key: String?
        get() = Services.library.getSetting(KEY)
        set(value) {
            Services.library.setSetting(KEY, value?.trim()?.ifEmpty { null })
            problem = null
            changes.value++
        }

    val enabled get() = !key.isNullOrBlank()

    /**
     * Matches up to [PER_RUN] new films to their scores, asking OMDb only about films it hasn't seen (most playlists
     * carry the same film several times). Blocking: call off the main thread.
     */
    @Synchronized
    fun update() {
        val key = key ?: return
        val lib = Services.library
        val client = OmdbClient(Services.http, key)
        val known = lib.getRatedFilms().toMutableSet().apply { removeAll(lib.getStaleMisses(System.currentTimeMillis() - RETRY_MISSES_MS)) }
        val links = mutableListOf<Triple<String, String, String>>()
        var asked = 0
        try {
            for (item in lib.getUnlinkedMovies(PER_RUN * 4)) {
                val film = TitleCleaner.key(item.title)
                if (film.startsWith("|")) continue
                if (film !in known) {
                    if (asked >= PER_RUN || !spend()) break
                    asked++
                    lookUp(client, film, item) ?: break
                    known += film
                }
                links += Triple(item.sourceId, item.externalId, film)
            }
        } finally {
            lib.linkFilms(links)
            if (links.isNotEmpty()) changes.value++
        }
        if (asked > 0) Log.info("Ratings: looked up $asked films, matched ${links.size} entries")
    }

    /** This entry's scores, looking the film up now if it's new (a film's page). Blocking. */
    fun forItem(item: MediaItem): Rating? {
        val lib = Services.library
        lib.getRating(item.sourceId, item.externalId)?.let { return it }
        val key = key ?: return null
        val film = TitleCleaner.key(item.title)
        if (film.startsWith("|")) return null
        if (film !in lib.getRatedFilms()) {
            if (!spend()) return null
            lookUp(OmdbClient(Services.http, key), film, item) ?: return null
        }
        lib.linkFilms(listOf(Triple(item.sourceId, item.externalId, film)))
        return lib.getRating(item.sourceId, item.externalId)
    }

    /** Saves what OMDb says (a miss too, so it isn't asked again for a while); null when lookups must stop for now. */
    private fun lookUp(client: OmdbClient, film: String, item: MediaItem): Unit? {
        val (title, year) = TitleCleaner.parse(item.title)
        return try {
            Services.library.saveRating(film, client.find(title, year))
            Unit
        } catch (e: OmdbException) {
            if (e.stop) {
                problem = e.message
                Log.info("Ratings: OMDb stopped: ${e.message}")
                null
            } else {
                Services.library.saveRating(film, null)
                Unit
            }
        } catch (e: IOException) {
            Log.info("Ratings: couldn't reach OMDb: ${e.message}")
            null
        }
    }

    /** Counts one lookup against today's allowance; false when it's used up. */
    private fun spend(): Boolean {
        val lib = Services.library
        val today = LocalDate.now().toString()
        val used = if (lib.getSetting(DAY) == today) lib.getSetting(COUNT)?.toIntOrNull() ?: 0 else 0
        if (used >= DAILY) return false
        lib.setSetting(DAY, today)
        lib.setSetting(COUNT, (used + 1).toString())
        return true
    }

    // ---- Recommendations ----

    /** The best rated films in your playlists. */
    fun topRated(limit: Int = 20): List<Pair<LibraryEntry, Rating>> = Services.library.getTopRated(limit)

    /**
     * Films like one you watched lately: sharing its genres (two of them when it has two or more), best rated first,
     * leaving out what you've already watched. Null until there's a rated film in your history with enough like it.
     */
    fun becauseYouWatched(limit: Int = 20): Pair<String, List<Pair<LibraryEntry, Rating>>>? {
        val lib = Services.library
        val history = lib.getHistory(40).filter { it.kind == MediaKind.Movie }
        if (history.isEmpty()) return null
        val watched = history.map { it.sourceId to it.externalId }.toSet()
        val watchedFilms = history.map { TitleCleaner.key(it.title) }.toSet()
        val pool by lazy { lib.getTopRated(500) }
        for (seed in history.take(5)) {
            val genres = lib.getRating(seed.sourceId, seed.externalId)?.genres?.takeIf { it.isNotEmpty() } ?: continue
            val need = minOf(2, genres.size)
            val picks = pool.filter { (e, r) ->
                (e.item.sourceId to e.item.externalId) !in watched && TitleCleaner.key(e.item.title) !in watchedFilms &&
                    r.genres.count { it in genres } >= need
            }.take(limit)
            if (picks.size >= 4) return TitleCleaner.parse(seed.title).first.ifEmpty { seed.title } to picks
        }
        return null
    }
}
