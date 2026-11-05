package io.github.sammykev.antenna.data

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException

/** A film's scores: IMDb (out of 10, with its vote count), Rotten Tomatoes' Tomatometer and Metacritic (out of 100). */
data class Rating(
    val imdb: Double? = null,
    val imdbVotes: Int? = null,
    val rottenTomatoes: Int? = null,
    val metascore: Int? = null,
    val imdbId: String? = null,
    val genres: List<String> = emptyList(),
) {
    /** One number to rank by: the average of IMDb (×10) and Rotten Tomatoes where both are known. */
    val score: Double? get() = when {
        imdb != null && rottenTomatoes != null -> (imdb * 10 + rottenTomatoes) / 2
        imdb != null -> imdb * 10
        else -> rottenTomatoes?.toDouble()
    }
}

class OmdbException(message: String, val stop: Boolean) : IOException(message)

/**
 * IMDb and Rotten Tomatoes ratings from OMDb (omdbapi.com), which needs a free key. Free keys allow 1,000 lookups a day,
 * so [Ratings] keeps everything it learns and spreads lookups out.
 */
class OmdbClient(private val http: OkHttpClient, private val key: String) {
    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    /** The film's ratings, or null when OMDb doesn't know it. Throws [OmdbException] with stop = true for a bad key or the daily limit. */
    fun find(title: String, year: Int?): Rating? {
        val url = "https://www.omdbapi.com/".toHttpUrl().newBuilder()
            .addQueryParameter("apikey", key.trim())
            .addQueryParameter("t", title)
            .addQueryParameter("type", "movie")
            .apply { if (year != null) addQueryParameter("y", year.toString()) }
            .build()
        val body = http.newCall(Request.Builder().url(url).build()).execute().use { it.body?.string() ?: "" }
        val root = runCatching { json.parseToJsonElement(body) as? JsonObject }.getOrNull() ?: throw OmdbException("OMDb sent something unexpected.", false)
        return parse(root)
    }

    companion object {
        internal fun parse(root: JsonObject): Rating? {
            if (root.str("Response").equals("False", ignoreCase = true)) {
                val error = root.str("Error") ?: "OMDb refused the request."
                if (error.contains("not found", ignoreCase = true)) return null
                // "Invalid API key!", "Request limit reached!": no point asking again today.
                throw OmdbException(error, stop = error.contains("key", true) || error.contains("limit", true))
            }
            val ratings = root["Ratings"] as? JsonArray
            val tomatoes = ratings?.firstOrNull { (it as? JsonObject)?.str("Source") == "Rotten Tomatoes" }?.str("Value")
            return Rating(
                imdb = root.str("imdbRating")?.toDoubleOrNull(),
                imdbVotes = root.str("imdbVotes")?.replace(",", "")?.toIntOrNull(),
                rottenTomatoes = tomatoes?.removeSuffix("%")?.toIntOrNull(),
                metascore = root.str("Metascore")?.toIntOrNull(),
                imdbId = root.str("imdbID"),
                genres = root.str("Genre")?.split(',')?.map { it.trim() }?.filter { it.isNotEmpty() && it != "N/A" }.orEmpty(),
            )
        }
    }
}
