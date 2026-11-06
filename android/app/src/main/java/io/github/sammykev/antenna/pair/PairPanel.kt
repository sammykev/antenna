package io.github.sammykev.antenna.pair

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.common.BitMatrix
import com.google.zxing.qrcode.QRCodeWriter
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.Ratings
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.PlaylistSource
import io.github.sammykev.antenna.data.SourceKind
import io.github.sammykev.antenna.ui.Muted
import io.github.sammykev.antenna.ui.Refreshing
import io.github.sammykev.antenna.ui.Surface
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

private val colours = listOf("#4F7CFF", "#2EB67D", "#E8A33D", "#D9534F", "#9B6BFF", "#1FB5C9")

/**
 * "Scan with your phone" (as Netflix does on TVs): a QR code that opens a page served by this TV, from which Antenna
 * on the phone sends its playlists, or any phone types one in. Nothing is typed with the remote. The TV listens only
 * while this is on screen. [received] runs once playlists have arrived and loaded.
 */
@Composable
fun PairPanel(received: () -> Unit, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf<String?>(null) }
    val host = remember { PairServer.localAddress() }
    var server by remember { mutableStateOf<PairServer?>(null) }
    val onReceived by rememberUpdatedState(received)

    DisposableEffect(host) {
        val s = if (host == null) null else runCatching {
            PairServer(context.packageName, onPayload = { payload ->
                scope.launch {
                    status = "Adding ${payload.sources.size} playlist${if (payload.sources.size == 1) "" else "s"} from your phone…"
                    val problems = receive(payload)
                    status = if (problems.isEmpty()) "Done." else problems.joinToString("\n")
                    if (problems.isEmpty()) onReceived()
                }
            }).start()
        }.onFailure { Log.error("Pairing server", it) }.getOrNull()
        server = s
        onDispose { s?.close() }
    }

    Row(modifier.clip(RoundedCornerShape(16.dp)).background(Surface).padding(20.dp), horizontalArrangement = Arrangement.spacedBy(24.dp),
        verticalAlignment = Alignment.CenterVertically) {
        val link = server?.let { s -> host?.let { s.link(it) } }
        Box(Modifier.size(196.dp).clip(RoundedCornerShape(12.dp)).background(Color.White).padding(10.dp), contentAlignment = Alignment.Center) {
            if (link != null) QrCode(link, 176.dp) else Text("No network", color = Color.Black)
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Set up with your phone", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
            if (link == null) Text("Connect the TV to your home Wi-Fi or network, then come back here.", color = Muted)
            else {
                Text("1. Point your phone's camera at the code.", color = Muted)
                Text("2. Open the link, then tap Send from Antenna on this phone, or type a playlist in.", color = Muted)
                Text("Your phone needs to be on the same Wi-Fi as the TV.", color = Muted, style = MaterialTheme.typography.bodySmall)
            }
            status?.let { Text(it, fontWeight = FontWeight.SemiBold) }
        }
    }
}

/** Adds what the phone sent (skipping playlists already here), loads each one, and takes the ratings key. */
private suspend fun receive(payload: PairPayload): List<String> = withContext(Dispatchers.IO) {
    val lib = Services.library
    val existing = lib.getSources()
    val added = mutableListOf<PlaylistSource>()
    for ((i, p) in payload.sources.withIndex()) {
        val location = p.location.trim().trimEnd('/')
        if (existing.any { it.location.trimEnd('/') == location && it.username == p.username }) continue
        val source = PlaylistSource(
            name = p.name.trim().ifEmpty { runCatching { java.net.URI(if ("://" in location) location else "http://$location").host }.getOrNull() ?: "Playlist" },
            kind = if (p.xtream) SourceKind.Xtream else SourceKind.M3u,
            location = location,
            username = p.username?.trim(),
            password = p.password?.trim(),
            epgUrl = p.epgUrl?.trim()?.ifEmpty { null },
            color = colours[(existing.size + i) % colours.size],
        )
        // A playlist file from the phone is kept here, as when one is chosen on this device.
        val saved = if (p.m3u != null) {
            val copy = File(Services.filesDir, "playlists/${source.id}.m3u").apply { parentFile?.mkdirs(); writeText(p.m3u) }
            source.copy(location = copy.path)
        } else source
        lib.saveSource(saved)
        added += saved
    }
    if (!payload.omdbKey.isNullOrBlank() && !Ratings.enabled) Ratings.key = payload.omdbKey
    Services.notifyChanged()
    Log.info("Pairing: received ${payload.sources.size} playlists, added ${added.size}")
    Refreshing.run(added)
}

/** The code itself: dark modules on the white panel, sized to [size]. */
@Composable
private fun QrCode(text: String, size: Dp) {
    val matrix = remember(text) { qr(text) }
    Canvas(Modifier.size(size)) {
        val cell = this.size.width / matrix.width
        for (y in 0 until matrix.height) for (x in 0 until matrix.width)
            if (matrix[x, y]) drawRect(Color.Black, Offset(x * cell, y * cell), Size(cell + 0.5f, cell + 0.5f))
    }
}

internal fun qr(text: String): BitMatrix =
    QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, mapOf(EncodeHintType.MARGIN to 0))
