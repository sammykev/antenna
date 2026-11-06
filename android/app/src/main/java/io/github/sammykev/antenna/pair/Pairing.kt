package io.github.sammykev.antenna.pair

import io.github.sammykev.antenna.Log
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import kotlin.concurrent.thread

/** A playlist as sent from a phone to a TV. A playlist file (rather than a link) travels with its contents. */
@Serializable
data class PairSource(
    val name: String,
    val xtream: Boolean,
    val location: String,
    val username: String? = null,
    val password: String? = null,
    val epgUrl: String? = null,
    val m3u: String? = null,
)

/** Everything a phone hands a TV: its playlists, and the OMDb key for ratings if it has one. */
@Serializable
data class PairPayload(val sources: List<PairSource>, val omdbKey: String? = null)

/**
 * The phone app's payload is sealed with AES-GCM under a key that only the QR code carries (in the link's #fragment,
 * which browsers never send), so the logins can't be read off the Wi-Fi.
 */
object PairCrypto {
    private val random = SecureRandom()
    private val b64 = Base64.getUrlEncoder().withoutPadding()

    fun newSecret(bytes: Int): String = b64.encodeToString(ByteArray(bytes).also(random::nextBytes))

    fun seal(key: String, plain: ByteArray): String {
        val iv = ByteArray(12).also(random::nextBytes)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, aes(key), GCMParameterSpec(128, iv)) }
        return b64.encodeToString(iv + cipher.doFinal(plain))
    }

    fun open(key: String, sealed: String): ByteArray {
        val data = Base64.getUrlDecoder().decode(sealed.trim())
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, aes(key), GCMParameterSpec(128, data, 0, 12)) }
        return cipher.doFinal(data, 12, data.size - 12)
    }

    private fun aes(key: String) = SecretKeySpec(Base64.getUrlDecoder().decode(key), "AES")

    /** Compares secrets in constant time. */
    fun same(a: String?, b: String) = a != null && MessageDigest.isEqual(a.toByteArray(), b.toByteArray())
}

/**
 * The TV's side of "scan to sign in": a small web server on the home network, open only while the QR code is on
 * screen. Scanning the code with a phone's camera opens [page]; from there Antenna on the phone sends its playlists
 * (sealed with the key in the code), or any phone can type a playlist into the form with its own keyboard. Every
 * request must carry the code's secret.
 */
class PairServer(
    private val appPackage: String,
    private val onPayload: (PairPayload) -> Unit,
    private val bindAddress: java.net.InetAddress? = null,
) : AutoCloseable {
    val secret = PairCrypto.newSecret(16)
    val key = PairCrypto.newSecret(32)
    private val socket = ServerSocket().apply { reuseAddress = true; bind(InetSocketAddress(bindAddress, 0)) }
    val port: Int get() = socket.localPort
    @Volatile private var closed = false
    private val json = Json { ignoreUnknownKeys = true }

    /** What the QR code holds: the page, the secret, and (after #) the key. */
    fun link(host: String) = "http://$host:$port/?s=$secret#k=$key"

    fun start() = apply {
        thread(isDaemon = true, name = "pair-server") {
            while (!closed) {
                val client = runCatching { socket.accept() }.getOrNull() ?: break
                thread(isDaemon = true) { runCatching { client.use(::serve) }.onFailure { Log.info("Pairing: ${it.message}") } }
            }
        }
    }

    override fun close() {
        closed = true
        runCatching { socket.close() }
    }

    private fun serve(client: Socket) {
        client.soTimeout = 15_000
        val input = BufferedInputStream(client.getInputStream())
        val requestLine = readLine(input) ?: return
        val (method, target) = requestLine.split(' ').let { (it.getOrNull(0) ?: "") to (it.getOrNull(1) ?: "/") }
        var length = 0
        while (true) {
            val line = readLine(input) ?: break
            if (line.isEmpty()) break
            if (line.startsWith("Content-Length:", true)) length = line.substringAfter(':').trim().toIntOrNull() ?: 0
        }
        if (length > MAX_BODY) return respond(client, 413, "text/plain", "Too big.")
        val body = ByteArray(length).also { var read = 0; while (read < length) { val n = input.read(it, read, length - read); if (n < 0) break; read += n } }
        val path = target.substringBefore('?')
        val query = form(target.substringAfter('?', ""))
        if (!PairCrypto.same(query["s"], secret)) return respond(client, 403, "text/html", page("This code has expired", "<p>Show the code again on the TV and scan the new one.</p>"))
        when {
            method == "GET" && path == "/" -> respond(client, 200, "text/html", mainPage())
            method == "POST" && path == "/send" -> {
                val payload = runCatching { json.decodeFromString(PairPayload.serializer(), PairCrypto.open(key, String(body)).decodeToString()) }.getOrNull()
                    ?: return respond(client, 400, "application/json", """{"ok":false}""")
                respond(client, 200, "application/json", """{"ok":true}""")
                onPayload(payload)
            }
            method == "POST" && path == "/add" -> {
                val f = form(String(body))
                val location = f["location"]?.trim().orEmpty()
                if (location.isEmpty()) return respond(client, 200, "text/html", mainPage("Enter the playlist link or server address."))
                val xtream = f["type"] == "xtream"
                respond(client, 200, "text/html", page("Sent to your TV", "<p>Your TV is loading <b>${esc(f["name"]?.ifBlank { null } ?: "the playlist")}</b>. You can close this page.</p>"))
                onPayload(PairPayload(listOf(PairSource(f["name"]?.trim().orEmpty(), xtream, location,
                    f["username"]?.trim()?.ifEmpty { null }.takeIf { xtream }, f["password"]?.trim()?.ifEmpty { null }.takeIf { xtream },
                    f["epg"]?.trim()?.ifEmpty { null }))))
            }
            else -> respond(client, 404, "text/plain", "Not found.")
        }
    }

    /** Antenna on the phone opens through an intent link; the form works on any phone, iPhones included. */
    private fun mainPage(error: String? = null) = page("Set up Antenna on your TV", """
        <p class="lead">Your TV is waiting. Send your playlists from Antenna on this phone, or type one in below.</p>
        <a id="app" class="button" href="#">Send from Antenna on this phone</a>
        <p class="muted">Android only. Your playlists and logins go straight to the TV over your home Wi-Fi, encrypted.</p>
        <h2>Or type a playlist</h2>
        ${error?.let { "<p class=\"error\">${esc(it)}</p>" } ?: ""}
        <form method="post" action="/add?s=$secret">
          <label>Name<input name="name" placeholder="My provider"></label>
          <label>Type<select name="type" id="type"><option value="m3u">M3U link</option><option value="xtream">Xtream Codes</option></select></label>
          <label><span id="loclabel">M3U link</span><input name="location" required placeholder="http://…" inputmode="url" autocapitalize="off"></label>
          <div id="xc" hidden>
            <label>Username<input name="username" autocapitalize="off" autocomplete="off"></label>
            <label>Password<input name="password" type="password" autocomplete="off"></label>
          </div>
          <label>Guide (XMLTV) link, optional<input name="epg" inputmode="url" autocapitalize="off"></label>
          <button type="submit">Send to TV</button>
        </form>
        <script>
          var k = (location.hash.match(/k=([^&]+)/) || [])[1] || "";
          var base = location.origin;
          document.getElementById("app").href = "intent://pair?u=" + encodeURIComponent(base) + "&s=$secret&k=" + k +
            "#Intent;scheme=antenna;package=$appPackage;S.browser_fallback_url=" +
            encodeURIComponent("https://github.com/sammykev/antenna/releases/latest") + ";end";
          var type = document.getElementById("type");
          type.onchange = function () {
            var x = type.value === "xtream";
            document.getElementById("xc").hidden = !x;
            document.getElementById("loclabel").textContent = x ? "Server (e.g. http://example.com:8080)" : "M3U link";
          };
        </script>""")

    private fun page(title: String, content: String) = """<!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1"><title>${esc(title)}</title><style>
        body{margin:0;padding:24px 16px;background:#0B0C10;color:#fff;font:16px/1.5 system-ui,sans-serif}
        main{max-width:480px;margin:auto}h1{font-size:26px;margin:0 0 8px}h2{font-size:18px;margin:32px 0 8px}
        .lead,.muted{color:#ffffffa6}.muted{font-size:13px}.error{color:#FF453A}
        .button,button{display:block;width:100%;box-sizing:border-box;padding:14px;border:0;border-radius:24px;background:#4F7CFF;
        color:#fff;font:600 16px system-ui;text-align:center;text-decoration:none;margin-top:16px}
        label{display:block;margin-top:12px;color:#ffffffa6;font-size:14px}
        input,select{display:block;width:100%;box-sizing:border-box;margin-top:4px;padding:12px;border-radius:8px;border:1px solid #ffffff26;
        background:#16181E;color:#fff;font-size:16px}</style></head><body><main><h1>${esc(title)}</h1>$content</main></body></html>"""

    private fun respond(client: Socket, status: Int, type: String, text: String) {
        val bytes = text.toByteArray()
        val reason = when (status) { 200 -> "OK"; 400 -> "Bad Request"; 403 -> "Forbidden"; 413 -> "Payload Too Large"; else -> "Not Found" }
        client.getOutputStream().apply {
            write("HTTP/1.1 $status $reason\r\nContent-Type: $type; charset=utf-8\r\nContent-Length: ${bytes.size}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n".toByteArray())
            write(bytes)
            flush()
        }
    }

    companion object {
        /** A playlist file can travel with the payload; anything bigger isn't a playlist. */
        const val MAX_BODY = 40 * 1024 * 1024

        private fun readLine(input: InputStream): String? {
            val out = ByteArrayOutputStream()
            while (true) {
                val b = input.read()
                if (b < 0) return if (out.size() == 0) null else out.toString()
                if (b == '\n'.code) return out.toString().trimEnd('\r')
                if (out.size() > 8192) return null
                out.write(b)
            }
        }

        fun form(text: String): Map<String, String> = text.split('&').filter { it.isNotEmpty() }.associate {
            val (k, v) = it.split('=', limit = 2).let { p -> p[0] to p.getOrElse(1) { "" } }
            URLDecoder.decode(k, "UTF-8") to URLDecoder.decode(v, "UTF-8")
        }

        private fun esc(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;")

        /** The TV's address on the home network (Wi-Fi or Ethernet), or null when it has none. */
        fun localAddress(): String? = runCatching {
            NetworkInterface.getNetworkInterfaces().toList()
                .filter { it.isUp && !it.isLoopback && !it.isVirtual }
                .sortedBy { if (it.name.startsWith("wlan") || it.name.startsWith("eth")) 0 else 1 }
                .flatMap { it.inetAddresses.toList() }
                .firstOrNull { it is Inet4Address && it.isSiteLocalAddress }?.hostAddress
        }.getOrNull()
    }
}
