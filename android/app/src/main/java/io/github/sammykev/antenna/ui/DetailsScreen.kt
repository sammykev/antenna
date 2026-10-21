package io.github.sammykev.antenna.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.PlayItem
import io.github.sammykev.antenna.Playback
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.Episode
import io.github.sammykev.antenna.data.MediaDetails
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.SourceKind
import io.github.sammykev.antenna.data.WatchRecord
import io.github.sammykev.antenna.data.XtreamClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * A movie (artwork, plot, Play or Continue) or a series (the same, plus seasons and episodes). Details come from the
 * provider for Xtream Codes accounts; M3U entries show what the playlist has.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DetailsScreen(sourceId: String, externalId: String, kind: MediaKind, back: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val version by Services.libraryVersion.collectAsState()
    var item by remember { mutableStateOf<MediaItem?>(null) }
    var details by remember { mutableStateOf(MediaDetails()) }
    var episodes by remember { mutableStateOf(emptyList<Episode>()) }
    var watches by remember { mutableStateOf(emptyMap<String, WatchRecord>()) }
    var season by remember { mutableIntStateOf(-1) }
    var busy by remember { mutableStateOf(true) }
    var error by remember { mutableStateOf<String?>(null) }
    var favourite by remember { mutableStateOf(false) }
    var resumeTick by remember { mutableIntStateOf(0) }
    val isSeries = kind == MediaKind.Series

    LaunchedEffect(Unit) {
        val entry = withContext(Dispatchers.IO) { Services.library.getItem(sourceId, externalId, kind) }
        item = entry?.item
        if (entry == null) {
            busy = false
            error = "This is no longer in your playlists."
            return@LaunchedEffect
        }
        favourite = withContext(Dispatchers.IO) { Services.library.isFavourite(entry.item) }
        val source = withContext(Dispatchers.IO) { Services.library.getSources().firstOrNull { it.id == sourceId } }
        if (source?.kind != SourceKind.Xtream) {
            busy = false
            if (isSeries) error = "This playlist doesn't list episodes. Search for them under Movies, or use an Xtream Codes login."
            return@LaunchedEffect
        }
        try {
            val client = XtreamClient.forSource(Services.http, source)
            if (isSeries) {
                val (d, eps) = withContext(Dispatchers.IO) { client.getSeriesInfo(externalId) }
                details = d
                episodes = eps
                if (eps.isEmpty()) error = "The provider lists no episodes for this series."
            } else {
                details = withContext(Dispatchers.IO) { client.getVodInfo(externalId) }
            }
        } catch (e: Exception) {
            Log.info("Details for ${entry.item.title} failed: ${e.message}")
            error = if (isSeries) "Couldn't load the episodes: ${e.message}" else "Couldn't load the details: ${e.message}"
        }
        busy = false
    }

    // Progress, refreshed when coming back from the player.
    LaunchedEffect(episodes, version, resumeTick) {
        watches = withContext(Dispatchers.IO) {
            if (isSeries) episodes.mapNotNull { ep -> Services.library.getWatch(sourceId, "ep:${ep.id}")?.let { ep.id to it } }.toMap()
            else listOfNotNull(Services.library.getWatch(sourceId, externalId)?.let { externalId to it }).toMap()
        }
    }
    androidx.lifecycle.compose.LifecycleResumeEffect(Unit) {
        resumeTick++
        onPauseOrDispose { }
    }

    val seasons = episodes.map { it.season }.distinct().sorted()
    val next = nextEpisode(episodes, watches)
    LaunchedEffect(seasons, next) { if (season !in seasons && seasons.isNotEmpty()) season = next?.season ?: seasons.first() }

    fun playEpisode(ep: Episode) {
        val series = item ?: return
        Playback.play(context, episodes.map { PlayItem.of(series, it) }, episodes.indexOf(ep))
    }

    LazyColumn(Modifier.fillMaxSize()) {
        item {
            Box(Modifier.fillMaxWidth().height(230.dp)) {
                val backdrop = details.backdropUrl ?: details.posterUrl ?: item?.logoUrl
                if (backdrop != null) AsyncImage(backdrop, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize().alpha(0.45f))
                TopAppBar(title = {}, navigationIcon = { IconButton(onClick = back) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") } },
                    colors = TopAppBarDefaults.topAppBarColors(containerColor = Color.Transparent))
                Row(Modifier.align(Alignment.BottomStart).padding(16.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                    Artwork(details.posterUrl ?: item?.logoUrl, "", Modifier.width(100.dp).height(150.dp), crop = true)
                    Column(Modifier.align(Alignment.Bottom)) {
                        Text(item?.title ?: "", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                        val meta = listOfNotNull(details.released?.split('-')?.first(), details.genre, details.duration,
                            details.rating?.let { "★ $it" }).filter { it.isNotBlank() }
                        Text(if (meta.isEmpty()) item?.group ?: "" else meta.joinToString(" · "), color = Muted, style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
        item {
            Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                val movieWatch = watches[externalId]?.takeIf { it.isResumable }
                Button(enabled = item != null && (!isSeries || episodes.isNotEmpty()), onClick = {
                    val it = item ?: return@Button
                    if (isSeries) (next ?: episodes.firstOrNull())?.let(::playEpisode) else Playback.playItems(context, listOf(it), 0)
                }) {
                    Text(when {
                        isSeries -> next?.let { "Continue S%02dE%02d".format(it.season, it.number) } ?: "Play first episode"
                        movieWatch != null -> "Continue (${(movieWatch.progress * 100).toInt()}%)"
                        else -> "Play"
                    })
                }
                if (!isSeries && movieWatch != null) FilledTonalButton(onClick = {
                    val it = item ?: return@FilledTonalButton
                    scope.launch {
                        withContext(Dispatchers.IO) { Services.library.removeWatch(sourceId, externalId) }
                        Playback.playItems(context, listOf(it), 0)
                    }
                }) { Text("Start over") }
                FilledTonalButton(onClick = {
                    val it = item ?: return@FilledTonalButton
                    favourite = !favourite
                    scope.launch(Dispatchers.IO) { Services.library.setFavourite(it, favourite); Services.notifyChanged() }
                }) {
                    Icon(if (favourite) Icons.Default.Star else Icons.Default.StarBorder, null)
                    Text(" Favourite")
                }
            }
        }
        if (busy) item { Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() } }
        details.plot?.let { plot -> item { Text(plot, Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) } }
        val credits = listOfNotNull(details.director?.let { "Director: $it" }, details.cast?.let { "Cast: $it" })
        if (credits.isNotEmpty()) item {
            Text(credits.joinToString("\n"), color = Muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(16.dp, 4.dp))
        }
        error?.let { item { Text(it, color = Live, modifier = Modifier.padding(16.dp)) } }
        if (seasons.isNotEmpty()) {
            item {
                Box(Modifier.padding(vertical = 8.dp)) {
                    ChipRow(seasons, season, { if (it == 0) "Specials" else "Season $it" }) { season = it }
                }
            }
            items(episodes.filter { it.season == season }, key = { it.id }) { ep ->
                val w = watches[ep.id]
                val done = w != null && !w.isResumable && (w.lengthMs == 0L || w.positionMs >= w.lengthMs * 0.95)
                Row(Modifier.fillMaxWidth().clickable { playEpisode(ep) }.padding(horizontal = 16.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("${ep.number}. ${ep.title}", fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        ep.plot?.let { Text(it, color = Muted, style = MaterialTheme.typography.bodySmall, maxLines = 2, overflow = TextOverflow.Ellipsis) }
                        if (w?.isResumable == true) ThinProgress(w.progress, Modifier.padding(top = 4.dp))
                    }
                    if (done) Text("✓", color = Accent, modifier = Modifier.padding(start = 8.dp))
                }
            }
        }
    }
}

/** The episode to continue: a part-watched one, else the one after the last watched. */
private fun nextEpisode(episodes: List<Episode>, watches: Map<String, WatchRecord>): Episode? {
    val last = episodes.withIndex().mapNotNull { (i, ep) -> watches[ep.id]?.let { Triple(ep, i, it) } }.maxByOrNull { it.third.watchedAt } ?: return null
    return if (last.third.isResumable) last.first else episodes.getOrNull(last.second + 1) ?: last.first
}
