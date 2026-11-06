package io.github.sammykev.antenna.pair

import android.net.Uri
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.Ratings
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.PlaylistSource
import io.github.sammykev.antenna.data.SourceKind
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File

/** A TV's "scan to sign in" code, opened on this phone: where the TV is, its secret and the key to seal with. */
data class PairRequest(val base: String, val secret: String, val key: String) {
    companion object {
        /** From antenna://pair?u=http://192.168.1.20:41234&s=…&k=… (the TV's page opens it). */
        fun from(uri: Uri?): PairRequest? {
            if (uri?.scheme != "antenna" || uri.host != "pair") return null
            val base = uri.getQueryParameter("u") ?: return null
            val parsed = Uri.parse(base)
            // Only a TV on the home network: never send logins anywhere else a link might point.
            if (parsed.scheme != "http" || !isLocal(parsed.host)) return null
            return PairRequest(base.trimEnd('/'), uri.getQueryParameter("s") ?: return null, uri.getQueryParameter("k") ?: return null)
        }

        internal fun isLocal(host: String?): Boolean {
            val parts = host?.split('.')?.mapNotNull { it.toIntOrNull() } ?: return false
            if (parts.size != 4 || parts.any { it !in 0..255 }) return false
            return parts[0] == 10 || (parts[0] == 192 && parts[1] == 168) || (parts[0] == 172 && parts[1] in 16..31)
        }
    }
}

object SendToTv {
    private val json = Json { encodeDefaults = false }

    /** This phone's playlists as the TV wants them; a playlist file goes with its contents. */
    fun payload(sources: List<PlaylistSource>, includeRatingsKey: Boolean) = PairPayload(
        sources = sources.map { s ->
            val file = if (s.kind == SourceKind.M3u && !s.location.startsWith("http")) File(s.location).takeIf { it.isFile } else null
            PairSource(s.name, s.kind == SourceKind.Xtream, s.location, s.username, s.password, s.epgUrl, file?.readText())
        },
        omdbKey = if (includeRatingsKey) Ratings.key else null,
    )

    /** Seals the payload with the code's key and hands it to the TV. Blocking. */
    fun send(request: PairRequest, payload: PairPayload) {
        val sealed = PairCrypto.seal(request.key, json.encodeToString(PairPayload.serializer(), payload).toByteArray())
        val http = Services.http.newBuilder().readTimeout(30, java.util.concurrent.TimeUnit.SECONDS).build()
        http.newCall(Request.Builder().url("${request.base}/send?s=${Uri.encode(request.secret)}")
            .post(sealed.toRequestBody("text/plain".toMediaType())).build()).execute().use {
            if (!it.isSuccessful) throw java.io.IOException(if (it.code == 403) "The code on the TV has changed. Scan it again." else "The TV answered ${it.code}.")
        }
    }
}

/** "Send your playlists to the TV?": shown when the TV's code is scanned with this phone. */
@Composable
fun SendToTvDialog(request: PairRequest, close: () -> Unit) {
    val scope = rememberCoroutineScope()
    var sources by remember { mutableStateOf<List<PlaylistSource>?>(null) }
    var includeKey by remember { mutableStateOf(true) }
    var sending by remember { mutableStateOf(false) }
    var result by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { sources = withContext(Dispatchers.IO) { Services.library.getSources() } }
    val list = sources

    AlertDialog(onDismissRequest = { if (!sending) close() }, title = { Text("Send to your TV") },
        text = {
            Column {
                when {
                    result != null -> Text(result!!)
                    list == null -> LinearProgressIndicator()
                    list.isEmpty() -> Text("There are no playlists on this phone yet. Use the form on the TV's page instead.")
                    else -> {
                        Text("Antenna on your TV will get these playlists and their logins, sent over your home Wi-Fi, encrypted:")
                        list.forEach { Text("•  ${it.name}") }
                        if (Ratings.enabled) Row(verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(includeKey, { includeKey = it })
                            Text("Also send the IMDb / Rotten Tomatoes ratings key")
                        }
                        if (sending) LinearProgressIndicator()
                    }
                }
            }
        },
        confirmButton = {
            if (result == null && !list.isNullOrEmpty()) TextButton(enabled = !sending, onClick = {
                sending = true
                scope.launch {
                    result = try {
                        withContext(Dispatchers.IO) { SendToTv.send(request, SendToTv.payload(list, includeKey && Ratings.enabled)) }
                        "Sent. Your TV is loading ${if (list.size == 1) "the playlist" else "${list.size} playlists"} now."
                    } catch (e: Exception) {
                        Log.error("Send to TV", e)
                        "Couldn't reach the TV: ${e.message}. Make sure the phone is on the same Wi-Fi as the TV and the code is still on screen."
                    }
                    sending = false
                }
            }) { Text("Send") }
            else TextButton(onClick = close) { Text("Close") }
        },
        dismissButton = { if (result == null && !list.isNullOrEmpty()) TextButton(enabled = !sending, onClick = close) { Text("Cancel") } })
}
