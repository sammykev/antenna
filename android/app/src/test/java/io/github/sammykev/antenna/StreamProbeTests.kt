package io.github.sammykev.antenna

import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.ProbeResult
import io.github.sammykev.antenna.data.StreamProbe
import okhttp3.OkHttpClient
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.Duration

/** Mirrors StreamProbeTests.cs against a real local HTTP server (a minimal one: the JDK's isn't on Android's classpath). */
class StreamProbeTests {
    private val server = TestHttpServer()
    private val base = server.base
    private val http = OkHttpClient()
    private val lastUserAgent get() = server.lastUserAgent

    private fun route(path: String, status: Int = 200, body: ByteArray = ByteArray(0)) = server.route(path, status, body)

    private fun start() = server.start()

    @After
    fun stop() = server.close()

    private fun check(path: String, vararg options: String) = StreamProbe.check(http, base + path, options.toList(), Duration.ofSeconds(2))

    @Test
    fun streamsSendingVideoAreOnAirAndDarkOnesAreNot() {
        route("/live/1.ts", body = ByteArray(64 * 1024) { 0x47 })
        route("/live/2.ts")
        route("/live/3.ts", status = 403)
        start()
        assertEquals(ProbeResult.OnAir, check("/live/1.ts"))
        assertEquals(ProbeResult.OffAir, check("/live/2.ts"))
        assertEquals(ProbeResult.OffAir, check("/live/3.ts"))
        assertEquals(ProbeResult.Unknown, StreamProbe.check(http, "rtmp://h/live/5", emptyList(), Duration.ofSeconds(1)))
    }

    @Test
    fun hlsFollowsTheFirstVariantAndNeedsSegments() {
        route("/a/master.m3u8", body = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nlow/index.m3u8\n".toByteArray())
        route("/a/low/index.m3u8", body = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg1.ts\n".toByteArray())
        route("/b/master.m3u8", body = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nlow/index.m3u8\n".toByteArray())
        route("/b/low/index.m3u8", body = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n".toByteArray())
        start()
        assertEquals(ProbeResult.OnAir, check("/a/master.m3u8"))
        assertEquals(ProbeResult.OffAir, check("/b/master.m3u8"))
    }

    @Test
    fun sendsTheUserAgentTheStreamNeeds() {
        route("/live/1.ts", body = ByteArray(64 * 1024))
        start()
        check("/live/1.ts", ":http-user-agent=MyBox/1.0")
        assertEquals("MyBox/1.0", lastUserAgent)
    }

    @Test
    fun skipsDarkChannelsForTheFirstOneBroadcasting() {
        route("/live/2.ts", body = ByteArray(64 * 1024))
        start()
        fun channel(id: String) = LibraryEntry(MediaItem("s", id, MediaKind.Live, "Ch $id", streamUrl = "$base/live/$id.ts"), "P", "#fff", "")

        val (onAir, dark) = StreamProbe.firstOnAir(http, listOf(channel("1"), channel("2"), channel("3")), Duration.ofSeconds(2))
        assertEquals("2", onAir?.item?.externalId)
        assertEquals(listOf("1"), dark.map { it.item.externalId })

        val (none, allDark) = StreamProbe.firstOnAir(http, listOf(channel("1"), channel("3")), Duration.ofSeconds(2))
        assertNull(none)
        assertEquals(2, allDark.size)
    }
}
