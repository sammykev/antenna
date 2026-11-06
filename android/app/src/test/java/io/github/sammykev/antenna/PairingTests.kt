package io.github.sammykev.antenna

import io.github.sammykev.antenna.pair.PairCrypto
import io.github.sammykev.antenna.pair.PairPayload
import io.github.sammykev.antenna.pair.PairRequest
import io.github.sammykev.antenna.pair.PairServer
import io.github.sammykev.antenna.pair.PairSource
import kotlinx.serialization.json.Json
import okhttp3.FormBody
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/** The TV's "scan with your phone" server: only the code's holder gets in, and only sealed payloads are taken. */
class PairingTests {
    private val received = LinkedBlockingQueue<PairPayload>()
    private val server = PairServer("io.github.sammykev.antenna", { received.add(it) }, InetAddress.getLoopbackAddress()).start()
    private val http = OkHttpClient()
    private val base = "http://127.0.0.1:${server.port}"

    @After
    fun stop() = server.close()

    private fun get(url: String) = http.newCall(Request.Builder().url(url).build()).execute().use { it.code to it.body!!.string() }

    private fun send(secret: String, body: String) = http.newCall(Request.Builder().url("$base/send?s=$secret")
        .post(body.toRequestBody("text/plain".toMediaType())).build()).execute().use { it.code }

    @Test
    fun thePageNeedsTheCodesSecret() {
        assertEquals(403, get("$base/?s=wrong").first)
        val (code, page) = get("$base/?s=${server.secret}")
        assertEquals(200, code)
        assertTrue(page.contains("intent://pair"))
        assertTrue(page.contains("package=io.github.sammykev.antenna"))
        // The key is never in the page: the phone's browser reads it from the link's #fragment.
        assertFalse(page.contains(server.key))
        assertTrue(server.link("192.168.1.20").endsWith("#k=${server.key}"))
    }

    @Test
    fun theAppsSealedPlaylistsArrive() {
        val payload = PairPayload(listOf(PairSource("3 Months", true, "https://operator2.example.org", "user", "pass"),
            PairSource("File", false, "/data/x.m3u", m3u = "#EXTM3U\n")), omdbKey = "abc123")
        val sealed = PairCrypto.seal(server.key, Json.encodeToString(PairPayload.serializer(), payload).toByteArray())

        assertEquals(403, send("wrong", sealed))
        assertEquals(200, send(server.secret, sealed))
        assertEquals(payload, received.poll(5, TimeUnit.SECONDS))
    }

    @Test
    fun anythingNotSealedWithTheCodesKeyIsRefused() {
        val other = PairCrypto.seal(PairCrypto.newSecret(32), """{"sources":[]}""".toByteArray())
        assertEquals(400, send(server.secret, other))
        assertEquals(400, send(server.secret, "not even base64 !"))
        assertNull(received.poll(300, TimeUnit.MILLISECONDS))
    }

    @Test
    fun thePageFormAddsAnXtreamLogin() {
        val form = FormBody.Builder().add("name", "Mine").add("type", "xtream").add("location", " http://host:8080 ")
            .add("username", "u ").add("password", "p@ss").add("epg", "").build()
        http.newCall(Request.Builder().url("$base/add?s=${server.secret}").post(form).build()).execute().use { assertEquals(200, it.code) }
        assertEquals(PairPayload(listOf(PairSource("Mine", true, "http://host:8080", "u", "p@ss"))), received.poll(5, TimeUnit.SECONDS))
    }

    @Test
    fun onlyHomeNetworkAddressesAreSentTo() {
        assertTrue(PairRequest.isLocal("192.168.1.20"))
        assertTrue(PairRequest.isLocal("10.0.0.5"))
        assertTrue(PairRequest.isLocal("172.20.1.1"))
        assertFalse(PairRequest.isLocal("8.8.8.8"))
        assertFalse(PairRequest.isLocal("evil.example.com"))
        assertFalse(PairRequest.isLocal("172.32.0.1"))
    }
}
