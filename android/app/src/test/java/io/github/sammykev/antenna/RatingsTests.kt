package io.github.sammykev.antenna

import io.github.sammykev.antenna.data.LibraryStore
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.OmdbClient
import io.github.sammykev.antenna.data.OmdbException
import io.github.sammykev.antenna.data.PlaylistSource
import io.github.sammykev.antenna.data.Rating
import io.github.sammykev.antenna.data.SourceKind
import io.github.sammykev.antenna.data.TitleCleaner
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

class RatingsTests {
    private val file = File.createTempFile("antenna", ".db").also { it.delete() }
    private val store = LibraryStore(file.path)

    @After
    fun cleanUp() {
        store.close()
        listOf("", "-wal", "-shm").forEach { File(file.path + it).delete() }
    }

    @Test
    fun catalogueNamesBecomeOneFilm() {
        assertEquals("The Matrix" to 1999, TitleCleaner.parse("EN - The Matrix (1999) [4K]"))
        assertEquals("Dune Part Two" to 2024, TitleCleaner.parse("Dune.Part.Two.2024.1080p"))
        assertEquals("Blade Runner 2049" to 2017, TitleCleaner.parse("|UK| Blade Runner 2049 (2017) FHD"))
        // Every playlist's copy is the same film.
        assertEquals(TitleCleaner.key("EN - The Matrix (1999) [4K]"), TitleCleaner.key("|NL| THE MATRIX (1999)"))
    }

    @Test
    fun omdbScoresAreRead() {
        val r = OmdbClient.parse(json("""{"Title":"The Matrix","Genre":"Action, Sci-Fi","imdbRating":"8.7","imdbVotes":"2,123,456",
            "imdbID":"tt0133093","Metascore":"73","Ratings":[{"Source":"Internet Movie Database","Value":"8.7/10"},
            {"Source":"Rotten Tomatoes","Value":"83%"}],"Response":"True"}"""))!!
        assertEquals(8.7, r.imdb!!, 0.001)
        assertEquals(2_123_456, r.imdbVotes)
        assertEquals(83, r.rottenTomatoes)
        assertEquals(73, r.metascore)
        assertEquals(listOf("Action", "Sci-Fi"), r.genres)
        assertEquals((87 + 83) / 2.0, r.score!!, 0.001)
        // Missing scores are "N/A".
        assertNull(OmdbClient.parse(json("""{"imdbRating":"N/A","Metascore":"N/A","Ratings":[],"Response":"True"}"""))!!.imdb)
        assertNull(OmdbClient.parse(json("""{"Response":"False","Error":"Movie not found!"}""")))
        val e = runCatching { OmdbClient.parse(json("""{"Response":"False","Error":"Request limit reached!"}""")) }.exceptionOrNull()
        assertTrue(e is OmdbException && e.stop)
    }

    @Test
    fun topRatedListsEachFilmOnceBestFirstAndSkipsObscureOnes() {
        store.saveSource(PlaylistSource("a", "A", SourceKind.M3u, "http://a"))
        store.saveSource(PlaylistSource("b", "B", SourceKind.M3u, "http://b"))
        store.replaceItems("a", listOf(movie("a", "1", "EN - The Matrix (1999)"), movie("a", "2", "Average Film (2010)"), movie("a", "3", "Tiny Film (2020)")))
        store.replaceItems("b", listOf(movie("b", "9", "|NL| THE MATRIX (1999) 4K")))
        val matrix = TitleCleaner.key("The Matrix (1999)")
        store.saveRating(matrix, Rating(8.7, 2_000_000, 83, genres = listOf("Action")))
        store.saveRating(TitleCleaner.key("Average Film (2010)"), Rating(6.1, 40_000, 55))
        store.saveRating(TitleCleaner.key("Tiny Film (2020)"), Rating(9.6, 40))
        store.linkFilms(store.getUnlinkedMovies(100).map { Triple(it.sourceId, it.externalId, TitleCleaner.key(it.title)) })

        assertEquals(emptyList<MediaItem>(), store.getUnlinkedMovies(100))
        val top = store.getTopRated(10)
        assertEquals(listOf("EN - The Matrix (1999)", "Average Film (2010)"), top.map { it.first.item.title })
        assertEquals(83, top[0].second.rottenTomatoes)
        assertEquals(listOf("Action"), store.getRating("b", "9")!!.genres)
    }

    private fun movie(source: String, id: String, title: String) = MediaItem(source, id, MediaKind.Movie, title, "Films", streamUrl = "http://x/$id.mp4")

    private fun json(text: String) = Json.parseToJsonElement(text) as JsonObject
}
