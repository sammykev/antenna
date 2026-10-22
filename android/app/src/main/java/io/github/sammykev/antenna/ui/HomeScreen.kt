package io.github.sammykev.antenna.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.EventNote
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.PlaylistPlay
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import io.github.sammykev.antenna.Playback
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.Programme
import io.github.sammykev.antenna.data.WatchRecord
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.LocalTime

private const val SHELF = 20

/** Everything Home shows, read in one go off the main thread. */
private data class HomeData(
    val playlists: Int,
    val resumable: List<WatchRecord>,
    val recent: List<WatchRecord>,
    val onNow: List<Pair<LibraryEntry, Programme?>>,
    val movies: List<LibraryEntry>,
    val series: List<LibraryEntry>,
)

/**
 * Home, UHF style (as on Windows): a featured hero (what you were watching, a favourite channel that's on, or a film),
 * then shelves that scroll sideways.
 */
@Composable
fun HomeScreen(openDetails: (LibraryEntry) -> Unit, openPlaylists: () -> Unit, openGuideSearch: () -> Unit, openTab: (String) -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val version by Services.libraryVersion.collectAsState()
    // Kept while Home is in the back stack, so Back returns to the same place on it.
    val state: HomeState = viewModel()
    var data: HomeData? by remember { mutableStateOf(state.data as? HomeData) }

    LaunchedEffect(version) {
        if (state.loadedFor == version && data != null) return@LaunchedEffect
        state.loadedFor = version
        data = withContext(Dispatchers.IO) {
            val lib = Services.library
            val history = lib.getHistory(60)
            val now = Services.nowPlaying()
            HomeData(
                playlists = lib.getSources().size,
                resumable = history.filter { it.isResumable }.take(SHELF),
                recent = history.filter { !it.isResumable }.take(SHELF),
                onNow = lib.getFavourites(MediaKind.Live).take(SHELF).map { e -> e to e.item.epgChannelId?.let { now[e.item.sourceId to it] } },
                movies = lib.getItems(MediaKind.Movie, limit = SHELF),
                series = lib.getItems(MediaKind.Series, limit = SHELF),
            )
        }
        state.data = data
    }

    fun resume(record: WatchRecord) = scope.launch {
        val (queue, index) = withContext(Dispatchers.IO) { Playback.queueFor(record) }
        Playback.play(context, queue, index)
    }

    val d = data
    LazyColumn(Modifier.fillMaxSize()) {
        item {
            LargeHeader(greeting(), subtitle = d?.playlists?.takeIf { it > 0 }?.let { if (it == 1) "1 playlist" else "$it playlists" }) {
                IconButton(onClick = openGuideSearch) { Icon(Icons.Default.EventNote, "Search the guide") }
                IconButton(onClick = openPlaylists) { Icon(Icons.Default.PlaylistPlay, "Playlists") }
            }
        }
        if (d == null) {
            item { Box(Modifier.fillMaxWidth().padding(48.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() } }
            return@LazyColumn
        }
        if (d.playlists == 0) {
            item { Welcome(openPlaylists) }
            return@LazyColumn
        }

        item {
            val channel = d.onNow.firstOrNull { it.second != null }
            when {
                d.resumable.isNotEmpty() -> {
                    val r = d.resumable[0]
                    Hero("CONTINUE WATCHING", r.title, "${watchDetails(r)}", r.logoUrl, crop = true, progress = r.progress,
                        play = "Resume", onPlay = { resume(r) })
                }
                channel != null -> {
                    val (entry, now) = channel
                    Hero("ON NOW", now!!.title, "${entry.item.title} · ${hhmm.format(now.start)} – ${hhmm.format(now.stop)}", entry.item.logoUrl,
                        crop = false, progress = now.progress(), play = "Watch live",
                        onPlay = { Playback.playItems(context, d.onNow.map { it.first.item }, d.onNow.indexOf(channel)) })
                }
                d.movies.isNotEmpty() -> {
                    val movie = d.movies[0]
                    Hero("FEATURED FILM", movie.item.title, movie.item.group.ifEmpty { movie.sourceName }, movie.item.logoUrl, crop = true,
                        play = "Play", onPlay = { Playback.playItems(context, listOf(movie.item), 0) },
                        more = "Details", onMore = { openDetails(movie) })
                }
            }
        }
        if (d.resumable.isNotEmpty()) item {
            Shelf("Continue watching") { items(d.resumable) { r -> WatchCard(r) { resume(r) } } }
        }
        if (d.onNow.isNotEmpty()) item {
            Shelf("Your channels, on now", seeAll = { openTab("live") }) {
                items(d.onNow) { (entry, now) ->
                    ChannelCard(entry, now, favourite = true) { Playback.playItems(context, d.onNow.map { it.first.item }, d.onNow.indexOfFirst { it.first == entry }) }
                }
            }
        }
        if (d.movies.isNotEmpty()) item {
            Shelf("Movies", seeAll = { openTab("movies") }) { items(d.movies) { m -> PosterCard(m, 132.dp) { openDetails(m) } } }
        }
        if (d.series.isNotEmpty()) item {
            Shelf("Series", seeAll = { openTab("series") }) { items(d.series) { s -> PosterCard(s, 132.dp) { openDetails(s) } } }
        }
        if (d.recent.isNotEmpty()) item {
            Shelf("Recently watched") { items(d.recent) { r -> WatchCard(r) { resume(r) } } }
        }
        if (d.resumable.isEmpty() && d.onNow.isEmpty() && d.recent.isEmpty() && d.movies.isEmpty() && d.series.isEmpty()) item {
            EmptyText("Watch something, or star a channel in the player, and it will show up here.")
        }
        item { BottomSpace() }
    }
}

private fun greeting() = when (LocalTime.now().hour) {
    in 5..11 -> "Good morning"
    in 12..17 -> "Good afternoon"
    else -> "Good evening"
}

/** The featured item: artwork behind a fade into the page, then what it is and what to do. */
@Composable
private fun Hero(kicker: String, title: String, details: String, imageUrl: String?, crop: Boolean, progress: Float = 0f,
                 play: String, onPlay: () -> Unit, more: String? = null, onMore: (() -> Unit)? = null) {
    Box(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp).height(400.dp).clip(RoundedCornerShape(20.dp)).background(Surface)) {
        if (!imageUrl.isNullOrBlank())
            AsyncImage(model = imageUrl, contentDescription = null, contentScale = if (crop) ContentScale.Crop else ContentScale.Fit,
                alpha = if (crop) 0.7f else 1f,
                modifier = if (crop) Modifier.fillMaxSize() else Modifier.align(Alignment.TopCenter).padding(top = 56.dp).size(width = 220.dp, height = 124.dp))
        Box(Modifier.fillMaxSize().background(Brush.verticalGradient(0.25f to androidx.compose.ui.graphics.Color.Transparent, 0.95f to Background)))
        Column(Modifier.align(Alignment.BottomStart).padding(20.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(kicker, color = Accent, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold, letterSpacing = MaterialTheme.typography.labelMedium.letterSpacing * 1.5f)
            Text(title, style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold, maxLines = 2, overflow = TextOverflow.Ellipsis)
            if (details.isNotBlank()) Text(details, color = Muted, maxLines = 2, overflow = TextOverflow.Ellipsis)
            if (progress > 0) ThinProgress(progress, Modifier.width(220.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.padding(top = 6.dp)) {
                Button(onClick = onPlay) {
                    Icon(Icons.Default.PlayArrow, null, Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(play, fontWeight = FontWeight.SemiBold)
                }
                if (more != null && onMore != null) FilledTonalButton(onClick = onMore) { Text(more) }
            }
        }
    }
}

@Composable
private fun Welcome(openPlaylists: () -> Unit) {
    Column(Modifier.fillMaxWidth().padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Welcome to Antenna", style = MaterialTheme.typography.titleLarge)
        Text("Add an M3U playlist or Xtream Codes login to start watching. Your channels, films and series will show up here.", color = Muted)
        Button(onClick = openPlaylists) { Text("Add a playlist") }
    }
}
