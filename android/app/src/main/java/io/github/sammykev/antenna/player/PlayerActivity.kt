package io.github.sammykev.antenna.player

import android.app.PictureInPictureParams
import android.content.Context
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.util.Rational
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.AspectRatio
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.CastConnected
import androidx.compose.material.icons.filled.EventNote
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PictureInPictureAlt
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Replay10
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.PlayItem
import io.github.sammykev.antenna.Playback
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.LiveDrift
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.Programme
import io.github.sammykev.antenna.data.WatchRecord
import io.github.sammykev.antenna.ui.Accent
import io.github.sammykev.antenna.ui.AntennaTheme
import io.github.sammykev.antenna.ui.ThinProgress
import io.github.sammykev.antenna.ui.hhmm
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.videolan.libvlc.LibVLC
import org.videolan.libvlc.Media
import org.videolan.libvlc.MediaPlayer
import org.videolan.libvlc.RendererDiscoverer
import org.videolan.libvlc.RendererItem
import org.videolan.libvlc.util.VLCVideoLayout
import java.time.Duration
import java.time.Instant

/**
 * Full-screen player on libVLC's own video layout. Live TV: swipe or ⏮/⏭ to change channel, now/next and the channel's
 * guide, reconnect on drops. Films and episodes: seek bar, skip back/forward, resume, next episode. Picture-in-picture
 * when leaving the app, and Chromecast through libVLC's renderers.
 */
class PlayerActivity : ComponentActivity() {
    private companion object {
        const val MAX_ATTEMPTS = 5
        val scales = listOf(
            "Fit" to MediaPlayer.ScaleType.SURFACE_BEST_FIT,
            "Fill" to MediaPlayer.ScaleType.SURFACE_FILL,
            "16:9" to MediaPlayer.ScaleType.SURFACE_16_9,
            "4:3" to MediaPlayer.ScaleType.SURFACE_4_3,
        )
    }

    private lateinit var vlc: LibVLC
    private lateinit var player: MediaPlayer
    private lateinit var videoLayout: VLCVideoLayout
    private var discoverer: RendererDiscoverer? = null
    private var multicast: WifiManager.MulticastLock? = null
    private var stopped = false

    // UI state
    private var item by mutableStateOf<PlayItem?>(null)
    private var status by mutableStateOf<String?>("Connecting…")
    private var playing by mutableStateOf(false)
    private var controls by mutableStateOf(true)
    private var inPip by mutableStateOf(false)
    private var favourite by mutableStateOf(false)
    private var scale by mutableIntStateOf(0)
    private var position by mutableLongStateOf(0)
    private var length by mutableLongStateOf(0)
    private var seeking by mutableStateOf<Float?>(null)
    private var now by mutableStateOf<Programme?>(null)
    private var next by mutableStateOf<Programme?>(null)
    private var guide by mutableStateOf<List<Programme>?>(null)
    private var failedHard by mutableStateOf(false)
    private var casting by mutableStateOf<RendererItem?>(null)
    private val drift = LiveDrift()
    private var startTimeout: Job? = null
    /** Set when the stream has sent something (buffering), so a slow channel isn't mistaken for a dark one. */
    private var sawData = false
    /** Shown when none of a match's channels would play. */
    private var noneBroadcasting by mutableStateOf<String?>(null)
    private var lastCatchUp = 0L
    private val renderers = mutableStateListOf<RendererItem>()
    private var castPicker by mutableStateOf(false)
    private var hideAt = 0L
    private var attempts = 0
    private var resumeAt = 0L
    private var retry: Job? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        hideSystemBars()

        vlc = LibVLC(this, arrayListOf("--http-reconnect"))
        player = MediaPlayer(vlc)
        videoLayout = VLCVideoLayout(this)
        player.setEventListener(::onEvent)

        setContent { AntennaTheme { Screen() } }
        startDiscovery()
        lifecycleScope.launch { ticker() }
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        // Opened again with something else (e.g. from a notification while in picture-in-picture).
        saveProgress()
        attempts = 0
        play(Playback.current)
    }

    override fun onStart() {
        super.onStart()
        // The video layout is attached while visible; libVLC draws into it (as VLC for Android does).
        player.attachViews(videoLayout, null, false, false)
        player.videoScale = scales[scale].second
        if (item == null || stopped) {
            stopped = false
            attempts = 0
            play(item ?: Playback.current)
        }
    }

    override fun onStop() {
        super.onStop()
        saveProgress()
        // Leaving (or closing the picture-in-picture window): stop rather than stream in the background. A cast carries on.
        if (casting == null) {
            player.stop()
            stopped = true
        }
        player.detachViews()
    }

    override fun onDestroy() {
        super.onDestroy()
        retry?.cancel()
        discoverer?.let { it.stop(); it.release() }
        multicast?.takeIf { it.isHeld }?.release()
        player.release()
        vlc.release()
    }

    // ---- Playing ----

    private fun play(next: PlayItem?, resume: Boolean = true) {
        retry?.cancel()
        if (next == null || next.url.isEmpty()) {
            finish()
            return
        }
        item = next
        status = "Connecting…"
        failedHard = false
        guide = null
        position = 0
        length = 0
        Log.info("Playing ${next.title}")
        lifecycleScope.launch {
            val (fav, watch) = withContext(Dispatchers.IO) {
                (next.item?.let { Services.library.isFavourite(it) } ?: false) to
                    (if (resume && !next.isLive) Services.library.getWatch(next.sourceId, next.historyId) else null)
            }
            favourite = fav
            resumeAt = watch?.takeIf { it.isResumable }?.positionMs ?: 0
            refreshNowNext()
        }
        val media = Media(vlc, Uri.parse(next.url))
        media.setHWDecoderEnabled(true, false)
        media.addOption(":network-caching=1500")
        // HLS channels otherwise start 15 seconds back from the live edge.
        if (next.isLive) media.addOption(":adaptive-livedelay=6000")
        next.options.forEach(media::addOption)
        drift.reset()
        player.media = media
        media.release()
        player.play()
        // Watching a match: a channel sending nothing within 20 seconds is given up on; one that's loading gets up to a
        // minute (some take long to start). Then the next one is tried.
        startTimeout?.cancel()
        sawData = false
        if (Playback.matchTitle != null) startTimeout = lifecycleScope.launch {
            val started = System.currentTimeMillis()
            delay(20_000)
            while (!playing && item == next && sawData && System.currentTimeMillis() - started < 60_000) delay(5_000)
            if (!playing && item == next) {
                Log.info("Nothing from ${next.title} after ${(System.currentTimeMillis() - started) / 1000} s" + if (sawData) " (it was loading)" else " (no data)")
                nextMatchChannel()
            }
        }
        if (next.isLive) lifecycleScope.launch(Dispatchers.IO) { remember(next, 0, 0) }
        showControls()
    }

    private fun onEvent(event: MediaPlayer.Event) {
        when (event.type) {
            MediaPlayer.Event.Playing -> {
                playing = true
                startTimeout?.cancel()
                attempts = 0
                status = casting?.let { "Casting to ${it.displayName}" }
                if (resumeAt > 0) {
                    player.time = resumeAt
                    status = "Continuing from ${clock(resumeAt)}"
                    resumeAt = 0
                    lifecycleScope.launch { delay(3000); if (status?.startsWith("Continuing") == true) status = null }
                }
                updatePipParams()
            }
            MediaPlayer.Event.Paused -> playing = false
            MediaPlayer.Event.Buffering -> {
                if (event.buffering > 0f) sawData = true
                if (playing || status != null) status = if (event.buffering < 100f) "Buffering ${event.buffering.toInt()}%"
                else casting?.let { "Casting to ${it.displayName}" }
            }
            MediaPlayer.Event.Vout -> Log.info("Video outputs: ${event.voutCount}")
            MediaPlayer.Event.EncounteredError -> failed("The stream failed")
            MediaPlayer.Event.EndReached -> ended()
            MediaPlayer.Event.TimeChanged -> position = event.timeChanged
            MediaPlayer.Event.LengthChanged -> length = event.lengthChanged
        }
    }

    private fun ended() {
        val current = item ?: return
        playing = false
        if (current.isLive) {
            failed("The stream ended")
            return
        }
        // Finished: forget the position, then the next episode or close.
        lifecycleScope.launch(Dispatchers.IO) { remember(current, 0, 0) }
        if (current.kind == MediaKind.Series && Playback.index + 1 < Playback.queue.size) {
            Playback.index++
            play(Playback.current, resume = false)
        } else {
            finish()
        }
    }

    private fun failed(what: String) {
        val current = item ?: return
        playing = false
        // A match's channel that won't play: try it once more (the error may only be the account's connection limit), then
        // move on to its next channel.
        if (Playback.matchTitle != null && current.isLive && attempts >= 1 && nextMatchChannel()) return
        Log.info("$what (${current.title}, attempt ${attempts + 1})")
        attempts++
        if (attempts > MAX_ATTEMPTS || !current.isLive && attempts > 2) {
            status = "$what. Tap ↻ to try again."
            failedHard = true
            controls = true
            return
        }
        // Live streams drop now and then: reconnect after a short pause.
        status = "$what, reconnecting…"
        retry = lifecycleScope.launch {
            delay(minOf(2000L * attempts, 8000L))
            if (item == current) play(current)
        }
    }

    /** Moves on to the match's next channel, or says none are broadcasting. False when there's none left. */
    private fun nextMatchChannel(): Boolean {
        val match = Playback.matchTitle ?: return false
        val skipped = item?.title ?: "That channel"
        startTimeout?.cancel()
        if (Playback.index + 1 >= Playback.queue.size) {
            Log.info("None of the channels for $match are broadcasting")
            player.stop()
            noneBroadcasting = if (Playback.queue.size == 1) "$skipped isn't broadcasting $match right now. It may start closer to kick-off, so try again then."
                else "None of the ${Playback.queue.size} channels listing $match are broadcasting right now (${Playback.queue.joinToString { it.title }}). " +
                    "They may start closer to kick-off, so try again then."
            return true
        }
        Playback.index++
        attempts = 0
        val next = Playback.current
        Log.info("$skipped isn't broadcasting $match, trying ${next?.title}")
        // Let go of this stream and pause: providers count a connection for a moment after it closes, and a one-stream
        // account refuses the next channel if it starts straight away.
        player.stop()
        status = "$skipped isn't broadcasting it. Trying ${next?.title}…"
        retry?.cancel()
        retry = lifecycleScope.launch {
            delay(3_000)
            if (Playback.current == next) {
                play(next)
                status = "$skipped isn't broadcasting it. Trying ${next?.title}…"
            }
        }
        return true
    }

    private fun step(delta: Int) {
        if (Playback.queue.size < 2) return
        saveProgress()
        Playback.index = (Playback.index + delta + Playback.queue.size) % Playback.queue.size
        attempts = 0
        play(Playback.current)
    }

    private fun saveProgress() {
        val current = item ?: return
        if (current.isLive) return
        val (t, l) = player.time to player.length
        if (l > 0 && t > 0) lifecycleScope.launch(Dispatchers.IO) { remember(current, t, l) }
    }

    private fun remember(p: PlayItem, positionMs: Long, lengthMs: Long) = try {
        Services.library.recordWatch(WatchRecord(p.sourceId, p.historyId, p.kind,
            if (p.kind == MediaKind.Series && p.subtitle != null) "${p.subtitle} · ${p.title}" else p.title, p.url, p.logoUrl, p.playerOptions,
            positionMs, lengthMs))
        Services.notifyChanged()
    } catch (e: Exception) {
        Log.error("Saving history failed", e)
    }

    /** Every second: hides the controls when due; every 15 s saves the position; every 30 s refreshes now/next. */
    private suspend fun ticker() {
        var ticks = 0
        while (true) {
            delay(1000)
            ticks++
            if (controls && System.currentTimeMillis() > hideAt && guide == null && seeking == null && !failedHard && !castPicker) controls = false
            if (ticks % 15 == 0 && playing) saveProgress()
            if (ticks % 2 == 0) checkLiveEdge()
            if (ticks % 30 == 0) refreshNowNext()
        }
    }

    /**
     * Every stall to rebuffer leaves live TV further behind. Once it's more than [LiveDrift.MAX_BEHIND] behind, reconnect:
     * the stream picks up at its live edge, keeping what the app adds well under 25 seconds.
     */
    private fun checkLiveEdge() {
        val current = item ?: return
        if (!playing || !current.isLive || casting != null || System.currentTimeMillis() - lastCatchUp < 20_000) return
        if (!drift.shouldCatchUp(Instant.now(), player.time)) return
        Log.info("Live: fell behind after stalls, reconnecting at the live edge")
        lastCatchUp = System.currentTimeMillis()
        play(current, resume = false)
    }

    private suspend fun refreshNowNext() {
        val channel = item?.item?.takeIf { it.kind == MediaKind.Live }
        val epg = channel?.epgChannelId
        if (channel == null || epg == null) {
            now = null; next = null
            return
        }
        val t = Instant.now()
        val list = withContext(Dispatchers.IO) { Services.library.getProgrammes(channel.sourceId, epg, t.minus(Duration.ofHours(6)), t.plus(Duration.ofHours(12))) }
        now = list.firstOrNull { it.isOnAt(t) }
        next = list.firstOrNull { !it.start.isBefore(now?.stop ?: t) }
    }

    private fun showControls(stay: Long = 4000) {
        if (inPip) return
        controls = true
        hideAt = System.currentTimeMillis() + stay
    }

    // ---- Picture-in-picture ----

    private fun pipParams(): PictureInPictureParams? {
        if (Build.VERSION.SDK_INT < 26) return null
        val vt = player.currentVideoTrack
        // Android only accepts ratios between 1:2.39 and 2.39:1.
        val ratio = (if (vt != null && vt.width > 0 && vt.height > 0) vt.width.toDouble() / vt.height else 16.0 / 9).coerceIn(1 / 2.38, 2.38)
        val builder = PictureInPictureParams.Builder().setAspectRatio(Rational((ratio * 1000).toInt(), 1000))
        if (Build.VERSION.SDK_INT >= 31) builder.setAutoEnterEnabled(casting == null).setSeamlessResizeEnabled(true)
        return builder.build()
    }

    private fun updatePipParams() {
        if (Build.VERSION.SDK_INT >= 26) runCatching { pipParams()?.let(::setPictureInPictureParams) }
    }

    private fun enterPip() {
        if (Build.VERSION.SDK_INT >= 26) runCatching { pipParams()?.let(::enterPictureInPictureMode) }
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // Before Android 12 there's no auto-enter: go into picture-in-picture as the user leaves.
        if (Build.VERSION.SDK_INT in 26..30 && playing && casting == null) enterPip()
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        inPip = isInPictureInPictureMode
        if (inPip) {
            controls = false
            guide = null
        } else {
            hideSystemBars()
            showControls()
        }
    }

    // ---- Chromecast ----

    /** Looks for Chromecasts on the Wi-Fi while the player is open (libVLC's mDNS renderer discovery). */
    private fun startDiscovery() {
        try {
            (applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager)?.let { wifi ->
                // Android drops multicast (mDNS) packets unless an app holds this lock.
                multicast = wifi.createMulticastLock("antenna-cast").apply { setReferenceCounted(false); acquire() }
            }
            val name = RendererDiscoverer.list(vlc).firstOrNull()?.name ?: return
            discoverer = RendererDiscoverer(vlc, name).apply {
                setEventListener { e ->
                    when (e.type) {
                        RendererDiscoverer.Event.ItemAdded -> renderers += e.item
                        RendererDiscoverer.Event.ItemDeleted -> renderers.removeAll { it.name == e.item.name }
                    }
                }
                start()
            }
        } catch (e: Exception) {
            // Casting is optional; playback works without it.
            Log.info("Chromecast discovery unavailable: ${e.message}")
        }
    }

    /** Switching renderer takes effect on the next play, so playback restarts (films resume where they were). */
    private fun castTo(renderer: RendererItem?) {
        val current = item ?: return
        if (renderer == casting) return
        if (!current.isLive && player.length > 0 && player.time > 0) remember(current, player.time, player.length)
        casting = renderer
        Log.info(if (renderer == null) "Casting stopped" else "Casting to ${renderer.displayName}")
        player.stop()
        player.setRenderer(renderer)
        attempts = 0
        play(current)
        status = if (renderer == null) "Connecting…" else "Connecting to ${renderer.displayName}…"
        updatePipParams()
    }

    // ---- UI ----

    private fun hideSystemBars() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }
    }

    @Composable
    private fun Screen() {
        noneBroadcasting?.let { text ->
            AlertDialog(onDismissRequest = { noneBroadcasting = null; finish() }, title = { Text("Not broadcasting yet") },
                text = { Text(text) },
                confirmButton = { TextButton(onClick = { noneBroadcasting = null; finish() }) { Text("OK") } })
        }
        Box(Modifier.fillMaxSize().background(Color.Black)) {
            AndroidView({ videoLayout }, Modifier.fillMaxSize())
            // Taps and swipes over the video.
            Box(Modifier.fillMaxSize()
                .pointerInput(Unit) { detectTapGestures { if (controls && guide == null) controls = false else showControls() } }
                .pointerInput(Unit) {
                    var dx = 0f
                    detectHorizontalDragGestures(onDragStart = { dx = 0f }, onDragEnd = {
                        if (dx < -150) step(1) else if (dx > 150) step(-1)
                    }) { _, amount -> dx += amount }
                })
            status?.takeIf { !inPip }?.let {
                Text(it, color = Color.White, modifier = Modifier.align(Alignment.Center)
                    .background(Color(0x99000000), RoundedCornerShape(8.dp)).padding(16.dp, 8.dp))
            }
            if (controls && !inPip) Controls()
        }
        if (castPicker) CastPicker()
    }

    @Composable
    private fun Controls() {
        val current = item
        Box(Modifier.fillMaxSize().background(Color(0x80000000)).padding(horizontal = 16.dp, vertical = 8.dp)
            .pointerInput(Unit) { detectTapGestures { if (guide == null) controls = false else guide = null } }) {
            // Top bar
            Row(Modifier.fillMaxWidth().align(Alignment.TopCenter), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = { finish() }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back", tint = Color.White) }
                Column(Modifier.weight(1f)) {
                    Text(current?.title ?: "", color = Color.White, fontWeight = FontWeight.Bold, style = MaterialTheme.typography.titleMedium,
                        maxLines = 1, overflow = TextOverflow.Ellipsis)
                    current?.subtitle?.let { Text(it, color = Color(0xCCFFFFFF), style = MaterialTheme.typography.bodySmall, maxLines = 1) }
                }
                if (current?.item != null) IconButton(onClick = {
                    favourite = !favourite
                    val it = current.item
                    lifecycleScope.launch(Dispatchers.IO) { Services.library.setFavourite(it, favourite); Services.notifyChanged() }
                    showControls()
                }) { Icon(if (favourite) Icons.Default.Star else Icons.Default.StarBorder, "Favourite", tint = Color.White) }
                TextButton(onClick = {
                    scale = (scale + 1) % scales.size
                    player.videoScale = scales[scale].second
                    showControls()
                }) {
                    Icon(Icons.Default.AspectRatio, null, tint = Color.White)
                    Text(" ${scales[scale].first}", color = Color.White)
                }
                if (current?.isLive == true && current.item?.epgChannelId != null) IconButton(onClick = {
                    val channel = current.item
                    lifecycleScope.launch {
                        val t = Instant.now()
                        guide = withContext(Dispatchers.IO) { Services.library.getProgrammes(channel.sourceId, channel.epgChannelId!!, t, t.plus(Duration.ofHours(24))) }
                        showControls(60_000)
                    }
                }) { Icon(Icons.Default.EventNote, "Guide", tint = Color.White) }
                if (Build.VERSION.SDK_INT >= 26) IconButton(onClick = { enterPip() }) { Icon(Icons.Default.PictureInPictureAlt, "Picture in picture", tint = Color.White) }
                IconButton(onClick = { castPicker = true; showControls(60_000) }) {
                    Icon(if (casting != null) Icons.Default.CastConnected else Icons.Default.Cast, "Cast", tint = if (casting != null) Accent else Color.White)
                }
            }

            // Middle
            Row(Modifier.align(Alignment.Center), horizontalArrangement = Arrangement.spacedBy(24.dp), verticalAlignment = Alignment.CenterVertically) {
                if (Playback.queue.size > 1) BigButton(Icons.Default.SkipPrevious, "Previous") { step(-1) }
                if (current?.isLive == false) BigButton(Icons.Default.Replay10, "Back 10 seconds") { skip(-10_000) }
                BigButton(when { failedHard -> Icons.Default.Refresh; playing -> Icons.Default.Pause; else -> Icons.Default.PlayArrow }, "Play or pause", 56) {
                    when {
                        failedHard || !playing && player.playerState in listOf(org.videolan.libvlc.interfaces.IMedia.State.Stopped,
                            org.videolan.libvlc.interfaces.IMedia.State.Ended, org.videolan.libvlc.interfaces.IMedia.State.Error) -> { attempts = 0; play(current) }
                        else -> {
                            // Resuming after a pause on purpose: count lag from here, don't jump.
                            if (!playing) drift.reset()
                            player.pause()
                        }
                    }
                    showControls()
                }
                if (current?.isLive == false) BigButton(Icons.Default.Forward30, "Forward 30 seconds") { skip(30_000) }
                if (Playback.queue.size > 1) BigButton(Icons.Default.SkipNext, "Next") { step(1) }
            }

            // Bottom: now and next, or the seek bar
            Column(Modifier.align(Alignment.BottomCenter).fillMaxWidth()) {
                if (current?.isLive == true) {
                    now?.let {
                        Text("Now  ${hhmm.format(it.start)}  ${it.title}", color = Color.White, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        ThinProgress(it.progress(), Modifier.padding(vertical = 4.dp))
                    }
                    next?.let { Text("Next  ${hhmm.format(it.start)}  ${it.title}", color = Color(0xCCFFFFFF), style = MaterialTheme.typography.bodySmall, maxLines = 1) }
                } else if (current != null) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(clock(seeking?.let { (it * length).toLong() } ?: position), color = Color.White)
                        Slider(value = seeking ?: if (length > 0) position.toFloat() / length else 0f,
                            onValueChange = { seeking = it; showControls(60_000) },
                            onValueChangeFinished = {
                                seeking?.let { if (length > 0) player.time = (it * length).toLong() }
                                seeking = null
                                showControls()
                            },
                            colors = SliderDefaults.colors(thumbColor = Accent, activeTrackColor = Accent),
                            modifier = Modifier.weight(1f).padding(horizontal = 8.dp))
                        Text(clock(length), color = Color.White)
                    }
                }
            }

            // This channel's guide
            guide?.let { list ->
                Box(Modifier.align(Alignment.CenterEnd).width(300.dp).fillMaxHeight().padding(top = 56.dp)
                    .background(Color(0xE6101318), RoundedCornerShape(8.dp)).padding(8.dp)) {
                    if (list.isEmpty()) Text("No guide for this channel.", color = Color(0xCCFFFFFF))
                    LazyColumn {
                        items(list) { p ->
                            Column(Modifier.padding(4.dp, 6.dp)) {
                                Text("${hhmm.format(p.start)}–${hhmm.format(p.stop)}", color = Color.White, fontWeight = FontWeight.Bold)
                                Text(p.title, color = Color.White)
                                p.description?.let { Text(it, color = Color(0xCCFFFFFF), style = MaterialTheme.typography.bodySmall, maxLines = 2, overflow = TextOverflow.Ellipsis) }
                            }
                        }
                    }
                }
            }
        }
        LaunchedEffect(Unit) { hideSystemBars() }
    }

    @Composable
    private fun BigButton(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, size: Int = 40, onClick: () -> Unit) {
        IconButton(onClick = onClick, modifier = Modifier.size((size + 16).dp)) { Icon(icon, label, tint = Color.White, modifier = Modifier.size(size.dp)) }
    }

    @Composable
    private fun CastPicker() {
        LaunchedEffect(Unit) {
            // Give discovery a moment if nothing has turned up yet.
            repeat(16) { if (renderers.isEmpty()) delay(250) }
        }
        val devices = renderers.toList()
        AlertDialog(onDismissRequest = { castPicker = false }, title = { Text("Play on") },
            text = {
                Column {
                    if (discoverer == null) Text("Casting isn't available on this phone.")
                    else if (devices.isEmpty()) Text("Looking for Chromecasts… Make sure the phone and the Chromecast (or Google TV) are on the same Wi-Fi.")
                    TextButton(onClick = { castPicker = false; castTo(null) }) { Text((if (casting == null) "✓ " else "") + "This phone") }
                    devices.forEach { d -> TextButton(onClick = { castPicker = false; castTo(d) }) { Text((if (d == casting) "✓ " else "") + d.displayName) } }
                }
            },
            confirmButton = { TextButton(onClick = { castPicker = false }) { Text("Close") } })
    }

    private fun skip(ms: Long) {
        if (item?.isLive != false) return
        player.time = (player.time + ms).coerceIn(0, (player.length - 1000).coerceAtLeast(0))
        showControls()
    }

    private fun clock(ms: Long): String {
        val s = (ms / 1000).coerceAtLeast(0)
        return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
    }
}
