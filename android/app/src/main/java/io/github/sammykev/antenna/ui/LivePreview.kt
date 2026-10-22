package io.github.sammykev.antenna.ui

import android.content.Context
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.VolumeOff
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.Programme
import org.videolan.libvlc.LibVLC
import org.videolan.libvlc.Media
import org.videolan.libvlc.MediaPlayer
import org.videolan.libvlc.util.VLCVideoLayout

/**
 * The small player at the top of Live TV that shows the picked channel (as on Windows). It stops whenever the screen
 * isn't visible, and is stopped before the full player opens, so accounts that allow one connection still work.
 */
class PreviewPlayer(context: Context) {
    private val vlc = LibVLC(context.applicationContext, arrayListOf("--http-reconnect"))
    private val player = MediaPlayer(vlc)
    private var released = false
    private var attached = false

    var buffering by mutableStateOf(false)
        private set
    var failed by mutableStateOf(false)
        private set
    var muted by mutableStateOf(false)
        private set

    init {
        player.setEventListener { e ->
            when (e.type) {
                MediaPlayer.Event.Buffering -> buffering = e.buffering < 100f
                MediaPlayer.Event.Playing -> { buffering = false; failed = false }
                MediaPlayer.Event.EncounteredError -> { buffering = false; failed = true }
            }
        }
    }

    fun attach(layout: VLCVideoLayout) {
        if (released || attached) return
        player.attachViews(layout, null, false, false)
        player.videoScale = MediaPlayer.ScaleType.SURFACE_BEST_FIT
        attached = true
    }

    fun detach() {
        if (released || !attached) return
        player.detachViews()
        attached = false
    }

    fun play(entry: LibraryEntry) {
        val url = entry.item.streamUrl
        if (released || url.isNullOrEmpty()) return
        failed = false
        buffering = true
        val media = Media(vlc, Uri.parse(url))
        media.setHWDecoderEnabled(true, false)
        media.addOption(":network-caching=1500")
        entry.item.playerOptions?.split('\n')?.filter { it.isNotBlank() }?.forEach(media::addOption)
        player.media = media
        media.release()
        player.volume = if (muted) 0 else 100
        player.play()
    }

    fun mute(value: Boolean) {
        muted = value
        if (!released) player.volume = if (value) 0 else 100
    }

    /** Lets go of the stream (and the provider connection). */
    fun stop() {
        if (released) return
        player.stop()
        buffering = false
    }

    fun release() {
        if (released) return
        try {
            player.stop()
            detach()
            player.release()
            vlc.release()
        } catch (e: Exception) {
            Log.error("preview release", e)
        }
        released = true
    }
}

/** The preview: video with mute and full-screen buttons, then the channel, what's on and what's next. */
@Composable
fun PreviewPanel(preview: PreviewPlayer, entry: LibraryEntry, now: Programme?, next: Programme?, fullScreen: () -> Unit) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Box(Modifier.fillMaxWidth().aspectRatio(16f / 9f).clip(CardShape).background(Color.Black).border(1.dp, Stroke, CardShape)
            .clickable(onClick = fullScreen)) {
            AndroidView(factory = { VLCVideoLayout(it).also(preview::attach) }, onRelease = { preview.detach() }, modifier = Modifier.fillMaxSize())
            if (preview.buffering) CircularProgressIndicator(Modifier.align(Alignment.Center).size(36.dp), color = Color.White)
            if (preview.failed) Text("This channel isn't playing right now.", color = Color(0xB3FFFFFF), modifier = Modifier.align(Alignment.Center))
            LiveBadge(Modifier.align(Alignment.TopStart).padding(10.dp))
            Row(Modifier.align(Alignment.BottomEnd).padding(6.dp)) {
                IconButton(onClick = { preview.mute(!preview.muted) }, modifier = Modifier.clip(CircleShape).background(Color(0x99000000))) {
                    Icon(if (preview.muted) Icons.AutoMirrored.Filled.VolumeOff else Icons.AutoMirrored.Filled.VolumeUp,
                        if (preview.muted) "Unmute" else "Mute", tint = Color.White)
                }
                IconButton(onClick = fullScreen, modifier = Modifier.padding(start = 6.dp).clip(CircleShape).background(Color(0x99000000))) {
                    Icon(Icons.Default.Fullscreen, "Full screen", tint = Color.White)
                }
            }
        }
        Text(entry.item.title, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (now != null) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(now.title, color = Muted, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                Text("${hhmm.format(now.start)} – ${hhmm.format(now.stop)}", color = Muted, style = MaterialTheme.typography.bodySmall)
            }
            ThinProgress(now.progress())
        }
        if (next != null)
            Text("Next  ${hhmm.format(next.start)}  ${next.title}", color = Muted, style = MaterialTheme.typography.bodySmall, maxLines = 1,
                overflow = TextOverflow.Ellipsis)
    }
}
