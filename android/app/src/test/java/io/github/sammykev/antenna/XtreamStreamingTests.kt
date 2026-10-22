package io.github.sammykev.antenna

import io.github.sammykev.antenna.data.XtreamClient
import okhttp3.OkHttpClient
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Big Xtream catalogues are read one entry at a time: holding a whole reply (string plus JSON tree) crashed phones
 * with out-of-memory errors right after Live TV loaded. Runs under a deliberately small test heap (build.gradle.kts).
 */
class XtreamStreamingTests {
    private val server = TestHttpServer()
    private val client = XtreamClient(OkHttpClient(), server.base, "u", "p")

    @After
    fun stop() = server.close()

    @Test
    fun aHugeFilmCatalogueLoadsWithoutHoldingItAllInMemory() {
        val films = 250_000
        server.route("/player_api.php?username=u&password=p&action=get_vod_categories", body = """[{"category_id":"1","category_name":"Drama"}]""".toByteArray())
        server.stream("/player_api.php?username=u&password=p&action=get_vod_streams") { out ->
            out.write('['.code)
            for (i in 1..films) {
                if (i > 1) out.write(','.code)
                out.write(("""{"num":$i,"name":"Movie Title Number $i (2021)","stream_type":"movie","stream_id":$i,""" +
                    """"stream_icon":"http://img.example/m$i.jpg","rating":"7.1","added":"1700000000","category_id":"1",""" +
                    """"container_extension":"mkv","plot":"A long plot description that pads each entry out the way real panels do."}""").toByteArray())
            }
            out.write(']'.code)
        }
        server.start()

        val movies = client.getMovies("s")
        assertEquals(films, movies.size)
        assertEquals("Drama", movies.last().group)
        assertEquals(films - 1, movies.last().sortIndex)
    }

    @Test
    fun emptyListsAnsweredAsAnObjectOrNothingAreEmpty() {
        server.route("/player_api.php?username=u&password=p&action=get_series_categories", body = "[]".toByteArray())
        server.route("/player_api.php?username=u&password=p&action=get_series", body = "{}".toByteArray())
        server.start()
        assertEquals(0, client.getSeries("s").size)
    }

    @Test
    fun aByteOrderMarkBeforeTheListIsSkipped() {
        server.route("/player_api.php?username=u&password=p&action=get_series_categories", body = "[]".toByteArray())
        server.route("/player_api.php?username=u&password=p&action=get_series",
            body = byteArrayOf(0xEF.toByte(), 0xBB.toByte(), 0xBF.toByte()) + """[{"series_id":"7","name":"Show"}]""".toByteArray())
        server.start()
        assertEquals(listOf("Show"), client.getSeries("s").map { it.title })
    }

    @Test
    fun aRefusedLoginShowsThePanelsReason() {
        server.route("/player_api.php?username=u&password=p", status = 401,
            body = """{"error":{"type":2,"code":1,"message":"`x` login username doesn't exist"}}""".toByteArray())
        server.start()
        val e = runCatching { client.authenticate() }.exceptionOrNull()
        assertEquals("The server refused the login: x login username doesn't exist. Check the username and password.", e?.message)
    }

    @Test
    fun spacesAroundTheLoginAreIgnored() {
        server.route("/player_api.php?username=u&password=p", body = """{"user_info":{"auth":1,"status":"Active"}}""".toByteArray())
        server.start()
        assertEquals("Active", XtreamClient(OkHttpClient(), server.base, " u ", "p\n").authenticate().status)
    }
}
