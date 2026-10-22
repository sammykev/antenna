package io.github.sammykev.antenna.data

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.json.DecodeSequenceMode
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.decodeToSequence
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.net.URI

class XtreamException(message: String, cause: Throwable? = null) : Exception(message, cause)

data class XtreamAccount(val status: String, val expiresAt: Long?, val maxConnections: Int, val activeConnections: Int)

/**
 * Client for the Xtream Codes "player_api.php" API most IPTV panels expose (XtreamClient.cs). Responses are loosely
 * typed, so every field goes through tolerant helpers.
 */
class XtreamClient(private val http: OkHttpClient, serverUrl: String, private val user: String, private val pass: String) {
    private val base = normalizeServer(serverUrl)

    companion object {
        private val json = Json { ignoreUnknownKeys = true; isLenient = true }

        fun forSource(http: OkHttpClient, source: PlaylistSource): XtreamClient {
            require(source.kind == SourceKind.Xtream) { "Source is not an Xtream account." }
            return XtreamClient(http, source.location, source.username ?: "", source.password ?: "")
        }

        /** Accepts "host:port", "http://host:port/", or a pasted player_api.php / get.php URL. */
        fun normalizeServer(serverUrl: String): String {
            var s = serverUrl.trim()
            if (!s.contains("://")) s = "http://$s"
            val uri = URI(s)
            val port = if (uri.port > 0) ":${uri.port}" else ""
            return "${uri.scheme}://${uri.host}$port"
        }
    }

    val xmltvUrl get() = "$base/xmltv.php?username=${escapeData(user)}&password=${escapeData(pass)}"

    fun liveUrl(streamId: String, extension: String = "ts") = "$base/live/${escapeData(user)}/${escapeData(pass)}/$streamId.$extension"

    fun movieUrl(streamId: String, extension: String) = "$base/movie/${escapeData(user)}/${escapeData(pass)}/$streamId.$extension"

    fun episodeUrl(episodeId: String, extension: String) = "$base/series/${escapeData(user)}/${escapeData(pass)}/$episodeId.$extension"

    fun authenticate(): XtreamAccount {
        val root = get(null)
        val info = root.obj("user_info") as? JsonObject
            ?: throw XtreamException("The server did not return account information. Check the server URL.")
        if (info.int("auth") == 0) throw XtreamException("The server rejected the username or password.")
        val exp = info.long("exp_date")
        return XtreamAccount(info.str("status") ?: "Unknown", if (exp > 0) exp else null, info.int("max_connections"), info.int("active_cons"))
    }

    fun getLive(sourceId: String): List<MediaItem> {
        val cats = categories("get_live_categories")
        return streamArray("get_live_streams") { s ->
            val id = s.str("stream_id") ?: return@streamArray null
            val archive = if (s.int("tv_archive") == 1) s.int("tv_archive_duration") else 0
            MediaItem(sourceId, id, MediaKind.Live, s.str("name") ?: id, cats[s.str("category_id") ?: ""] ?: "",
                s.str("stream_icon").nullIfBlank(), liveUrl(id), s.str("epg_channel_id").nullIfBlank(), archive)
        }.mapIndexed { i, item -> item.copy(sortIndex = i) }
    }

    fun getMovies(sourceId: String): List<MediaItem> {
        val cats = categories("get_vod_categories")
        return streamArray("get_vod_streams") { s ->
            val id = s.str("stream_id") ?: return@streamArray null
            MediaItem(sourceId, id, MediaKind.Movie, s.str("name") ?: id, cats[s.str("category_id") ?: ""] ?: "",
                s.str("stream_icon").nullIfBlank(), movieUrl(id, s.str("container_extension") ?: "mp4"))
        }.mapIndexed { i, item -> item.copy(sortIndex = i) }
    }

    fun getSeries(sourceId: String): List<MediaItem> {
        val cats = categories("get_series_categories")
        return streamArray("get_series") { s ->
            val id = s.str("series_id") ?: return@streamArray null
            MediaItem(sourceId, id, MediaKind.Series, s.str("name") ?: id, cats[s.str("category_id") ?: ""] ?: "", s.str("cover").nullIfBlank())
        }.mapIndexed { i, item -> item.copy(sortIndex = i) }
    }

    /** Series details (plot, cast, artwork) and its episodes, from one request. */
    fun getSeriesInfo(seriesId: String): Pair<MediaDetails, List<Episode>> {
        val root = get("get_series_info&series_id=${escapeData(seriesId)}")
        return readDetails(root.obj("info"), "cover") to readEpisodes(root)
    }

    fun getVodInfo(streamId: String): MediaDetails = readDetails(get("get_vod_info&vod_id=${escapeData(streamId)}").obj("info"), "movie_image")

    private fun readDetails(info: JsonElement?, posterField: String): MediaDetails {
        if (info !is JsonObject) return MediaDetails()
        // backdrop_path is usually an array of URLs, sometimes a single string.
        val backdrop = when (val b = info["backdrop_path"]) {
            is JsonArray -> b.mapNotNull { (it as? JsonPrimitive)?.takeIf { p -> p.isString }?.content }.firstOrNull { it.isNotBlank() }
            is JsonPrimitive -> if (b.isString) b.content else null
            else -> null
        }
        return MediaDetails(
            plot = (info.str("plot") ?: info.str("description")).nullIfBlank(),
            cast = (info.str("cast") ?: info.str("actors")).nullIfBlank(),
            director = info.str("director").nullIfBlank(),
            genre = info.str("genre").nullIfBlank(),
            released = (info.str("releasedate") ?: info.str("releaseDate") ?: info.str("release_date")).nullIfBlank(),
            rating = info.str("rating").nullIfBlank()?.takeIf { it != "0" },
            duration = (info.str("duration") ?: info.str("episode_run_time")).nullIfBlank(),
            posterUrl = (info.str(posterField) ?: info.str("cover_big")).nullIfBlank(),
            backdropUrl = backdrop.nullIfBlank(),
        )
    }

    private fun readEpisodes(root: JsonElement): List<Episode> {
        // "episodes" is normally {"1": [...], "2": [...]}, but some panels send a flat array.
        val all = when (val episodes = root.obj("episodes")) {
            is JsonObject -> episodes.values.flatMap { if (it is JsonArray) it else listOf(it) }
            is JsonArray -> episodes.flatMap { if (it is JsonArray) it else listOf(it) }
            else -> emptyList()
        }
        return all.mapNotNull { e ->
            val id = e.str("id") ?: return@mapNotNull null
            val info = e.obj("info") as? JsonObject
            Episode(id, e.int("season"), e.int("episode_num"), e.str("title") ?: "Episode ${e.int("episode_num")}",
                episodeUrl(id, e.str("container_extension") ?: "mp4"), info.str("plot").nullIfBlank(), info.str("movie_image").nullIfBlank())
        }.sortedWith(compareBy({ it.season }, { it.number }))
    }

    private fun categories(action: String): Map<String, String> =
        array(action).mapNotNull { c -> c.str("category_id")?.let { it to (c.str("category_name") ?: "") } }.toMap()

    private fun array(action: String): List<JsonElement> = get(action) as? JsonArray ?: emptyList()

    /**
     * A list action read one entry at a time. Catalogues run to tens of thousands of films: reading the whole reply into
     * one string and JSON tree took hundreds of megabytes and crashed phones (out of memory).
     */
    @OptIn(ExperimentalSerializationApi::class)
    private fun <T : Any> streamArray(action: String, map: (JsonElement) -> T?): List<T> {
        val url = "$base/player_api.php?username=${escapeData(user)}&password=${escapeData(pass)}&action=$action"
        try {
            http.newCall(Request.Builder().url(url).build()).execute().use { response ->
                if (!response.isSuccessful) throw XtreamException("The Xtream server answered ${response.code}.")
                val stream = response.body?.byteStream() ?: return emptyList()
                // Some panels answer an empty list as {} or nothing at all, and some start with a byte-order mark.
                val input = java.io.BufferedInputStream(stream)
                input.mark(3)
                if (!(input.read() == 0xEF && input.read() == 0xBB && input.read() == 0xBF)) input.reset()
                input.mark(4096)
                val first = generateSequence { input.read().takeIf { it >= 0 } }.firstOrNull { !Character.isWhitespace(it) }
                input.reset()
                if (first != '['.code) return emptyList()
                return json.decodeToSequence(input, JsonElement.serializer(), DecodeSequenceMode.ARRAY_WRAPPED).mapNotNull(map).toList()
            }
        } catch (e: XtreamException) {
            throw e
        } catch (e: IOException) {
            throw XtreamException("Could not reach the Xtream server: ${e.message}", e)
        } catch (e: kotlinx.serialization.SerializationException) {
            throw XtreamException("The server returned something that is not Xtream JSON. Check the server URL.", e)
        } catch (e: IllegalArgumentException) {
            throw XtreamException("The server returned something that is not Xtream JSON. Check the server URL.", e)
        }
    }

    private fun get(action: String?): JsonElement {
        var url = "$base/player_api.php?username=${escapeData(user)}&password=${escapeData(pass)}"
        if (action != null) url += "&action=$action"
        val body = try {
            http.newCall(Request.Builder().url(url).build()).execute().use { response ->
                if (!response.isSuccessful) throw XtreamException("The Xtream server answered ${response.code}.")
                response.body?.string() ?: ""
            }
        } catch (e: IOException) {
            throw XtreamException("Could not reach the Xtream server: ${e.message}", e)
        }
        return try {
            json.parseToJsonElement(body)
        } catch (e: Exception) {
            throw XtreamException("The server returned something that is not Xtream JSON. Check the server URL.", e)
        }
    }
}
