package io.github.sammykev.antenna.data

import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import java.time.Duration

enum class ProbeResult {
    /** The stream answered and sent video data (or an HLS playlist with segments). */
    OnAir,

    /** The server refused, sent nothing, or didn't answer in time. */
    OffAir,

    /** Not something that can be checked over HTTP (rtmp, udp, a local file): assume it plays. */
    Unknown,
}

/**
 * Checks a live channel is actually broadcasting before offering it for a match (StreamProbe.cs): guides often list a
 * match on a channel that's dark, geo-blocked or showing a slate. Connects, reads a little and lets go.
 */
object StreamProbe {
    /** A few MPEG-TS packets: enough to know the server is really sending video. */
    private const val ENOUGH_BYTES = 188 * 32

    /** Blocking: call off the main thread. */
    fun check(http: OkHttpClient, url: String?, options: List<String>, timeout: Duration): ProbeResult {
        val parsed = url?.toHttpUrlOrNull() ?: return ProbeResult.Unknown
        // Ask the way the player does: the app's usual user agent would override one the stream insists on.
        val client = http.newBuilder().apply { interceptors().clear() }.callTimeout(timeout).readTimeout(timeout).build()
        return try {
            if (checkUrl(client, parsed.toString(), headersFrom(options), 0)) ProbeResult.OnAir else ProbeResult.OffAir
        } catch (_: java.io.IOException) {
            ProbeResult.OffAir
        }
    }

    /** The first of [channels] (in order, up to [max]) that's broadcasting, and the ones found dark before it. */
    fun firstOnAir(http: OkHttpClient, channels: List<LibraryEntry>, timeout: Duration, max: Int = 4): Pair<LibraryEntry?, List<LibraryEntry>> {
        val dark = mutableListOf<LibraryEntry>()
        for (channel in channels.take(max)) {
            val options = channel.item.playerOptions?.split('\n')?.filter { it.isNotBlank() } ?: emptyList()
            if (check(http, channel.item.streamUrl, options, timeout) != ProbeResult.OffAir) return channel to dark
            dark += channel
        }
        return null to dark
    }

    /** HTTP headers from libVLC options like ":http-user-agent=…" and ":http-referrer=…". */
    fun headersFrom(options: List<String>): List<Pair<String, String>> = options.mapNotNull { option ->
        val o = option.trimStart(':')
        when {
            o.startsWith("http-user-agent=", true) -> "User-Agent" to o.substringAfter('=')
            o.startsWith("http-referrer=", true) || o.startsWith("http-referer=", true) -> "Referer" to o.substringAfter('=')
            else -> null
        }
    }

    private fun checkUrl(client: OkHttpClient, url: String, headers: List<Pair<String, String>>, depth: Int): Boolean {
        val request = Request.Builder().url(url).header("User-Agent", "VLC/3.0.21 LibVLC/3.0.21")
            .apply { headers.forEach { (k, v) -> header(k, v) } }.build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) return false
            val body = response.body ?: return false
            val stream = body.byteStream()
            val buffer = ByteArray(ENOUGH_BYTES)
            var read = 0
            while (read < buffer.size) {
                val n = stream.read(buffer, read, buffer.size - read)
                if (n < 0) break
                read += n
                if (read >= 7 && isPlaylist(buffer, read)) break
            }
            if (isPlaylist(buffer, read)) {
                // HLS: a full playlist is needed to see segments; it's small, so read the rest.
                val text = String(buffer, 0, read, Charsets.UTF_8) + stream.bufferedReader().readText()
                return depth < 2 && checkPlaylist(client, url, text, headers, depth)
            }
            return read >= ENOUGH_BYTES
        }
    }

    /** A master playlist is checked through its first variant; a media playlist must list segments. */
    private fun checkPlaylist(client: OkHttpClient, playlist: String, text: String, headers: List<Pair<String, String>>, depth: Int): Boolean {
        val lines = text.lines().map { it.trim() }.filter { it.isNotEmpty() }
        val variantAt = lines.indexOfFirst { it.startsWith("#EXT-X-STREAM-INF", true) }
        if (variantAt >= 0) {
            val variant = lines.drop(variantAt + 1).firstOrNull { !it.startsWith("#") } ?: return false
            val next = playlist.toHttpUrlOrNull()?.resolve(variant) ?: return false
            return checkUrl(client, next.toString(), headers, depth + 1)
        }
        return lines.any { it.startsWith("#EXTINF", true) }
    }

    private fun isPlaylist(data: ByteArray, length: Int): Boolean {
        val start = if (length >= 3 && data[0] == 0xEF.toByte() && data[1] == 0xBB.toByte() && data[2] == 0xBF.toByte()) 3 else 0
        return length - start >= 7 && String(data, start, 7, Charsets.US_ASCII) == "#EXTM3U"
    }
}
