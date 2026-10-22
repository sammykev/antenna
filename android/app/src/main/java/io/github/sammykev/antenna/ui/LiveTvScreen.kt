package io.github.sammykev.antenna.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.EventNote
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.foundation.background
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import io.github.sammykev.antenna.Playback
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.Programme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** A category chip: a provider category, all channels, or favourites / continue watching. */
data class GroupChip(val name: String, val group: String?, val favourites: Boolean = false, val continueWatching: Boolean = false)

val hhmm: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm").withZone(ZoneId.systemDefault())

private const val PAGE = 300

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LiveTvScreen(openGuideSearch: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val version by Services.libraryVersion.collectAsState()
    var query by rememberSaveable { mutableStateOf("") }
    val search = debounced(query)
    // Kept while the screen is in the back stack, so Back returns to the same category and scroll position.
    val state: LiveTvState = viewModel()
    var chips by state::chips
    var chip by state::chip
    val rows = state.rows
    var loading by remember { mutableStateOf(false) }
    var exhausted by state::exhausted
    val prefs = remember { context.getSharedPreferences("antenna", 0) }

    // Tap a channel to preview it at the top; tap it again (or the video) for full screen.
    var previewOn by remember { mutableStateOf(prefs.getBoolean("live.preview", true)) }
    var previewing by remember { mutableStateOf<LibraryEntry?>(null) }
    var previewNow by remember { mutableStateOf<Programme?>(null) }
    var previewNext by remember { mutableStateOf<Programme?>(null) }
    val preview = remember { PreviewPlayer(context) }
    DisposableEffect(Unit) { onDispose { preview.release() } }
    LifecycleEventEffect(Lifecycle.Event.ON_STOP) { preview.stop() }
    LifecycleEventEffect(Lifecycle.Event.ON_START) { previewing?.let(preview::play) }

    fun watch(index: Int) {
        // Let go of the preview's stream first: many accounts allow one connection.
        preview.stop()
        Playback.playItems(context, rows.map { it.first.item }, index)
    }

    fun select(index: Int) {
        val entry = rows[index].first
        if (!previewOn || previewing?.item == entry.item) {
            watch(index)
            return
        }
        previewing = entry
        previewNow = rows[index].second
        previewNext = null
        preview.play(entry)
        val channel = entry.item.epgChannelId ?: return
        scope.launch {
            val upcoming = withContext(Dispatchers.IO) {
                val start = java.time.Instant.now()
                Services.library.getProgrammes(entry.item.sourceId, channel, start, start.plus(java.time.Duration.ofHours(8)))
            }
            if (previewing?.item != entry.item) return@launch
            previewNow = upcoming.firstOrNull { it.isOnAt(java.time.Instant.now()) } ?: previewNow
            previewNext = upcoming.firstOrNull { p -> p.start >= (previewNow?.stop ?: java.time.Instant.now()) }
        }
    }

    LaunchedEffect(version) {
        if (state.chipsFor == version) return@LaunchedEffect
        state.chipsFor = version
        val (groups, hasFavourites) = withContext(Dispatchers.IO) {
            Services.library.getGroups(MediaKind.Live) to Services.library.getFavourites(MediaKind.Live).isNotEmpty()
        }
        chips = buildList {
            if (hasFavourites) add(GroupChip("★ Favourites", null, favourites = true))
            add(GroupChip("All channels", null))
            groups.forEach { add(GroupChip(it.ifEmpty { "Other" }, it)) }
        }
        val last = prefs.getString("live.group", null)
        chip = chips.firstOrNull { it.name == (chip?.name ?: last) } ?: chips.first()
    }

    fun load(reset: Boolean) {
        if (loading || (!reset && exhausted)) return
        loading = true
        val current = chip
        val offset = if (reset) 0 else rows.size
        scope.launch {
            val page = withContext(Dispatchers.IO) {
                val lib = Services.library
                val entries = when {
                    search.isNotBlank() -> lib.search(search, MediaKind.Live, PAGE)
                    current?.favourites == true -> lib.getFavourites(MediaKind.Live)
                    else -> lib.getItems(MediaKind.Live, group = current?.group, limit = PAGE, offset = offset)
                }
                val now = Services.nowPlaying()
                entries.map { e -> e to e.item.epgChannelId?.let { now[e.item.sourceId to it] } }
            }
            if (reset) rows.clear()
            rows.addAll(page)
            exhausted = page.size < PAGE || search.isNotBlank() || current?.favourites == true
            loading = false
        }
    }

    LaunchedEffect(chip, search, version) {
        if (chip == null) return@LaunchedEffect
        val key = listOf(chip?.name, search, version)
        if (state.loadedFor == key && rows.isNotEmpty()) return@LaunchedEffect
        state.loadedFor = key
        loading = false
        load(reset = true)
    }

    val listState = rememberLazyListState()
    LoadMoreEffect(listState) { load(reset = false) }

    Column(Modifier.fillMaxSize()) {
        LargeHeader("Live TV") {
            IconButton(onClick = {
                previewOn = !previewOn
                prefs.edit().putBoolean("live.preview", previewOn).apply()
                if (!previewOn) {
                    preview.stop()
                    previewing = null
                }
            }) { Icon(if (previewOn) Icons.Default.Visibility else Icons.Default.VisibilityOff, if (previewOn) "Turn the preview off" else "Turn the preview on") }
            IconButton(onClick = openGuideSearch) { Icon(Icons.Default.EventNote, "Search the guide") }
        }
        previewing?.let { entry ->
            PreviewPanel(preview, entry, previewNow, previewNext) {
                val index = rows.indexOfFirst { it.first.item == entry.item }
                if (index >= 0) watch(index) else {
                    preview.stop()
                    Playback.playItems(context, listOf(entry.item), 0)
                }
            }
        }
        SearchField(query, { query = it }, "Search channels")
        if (chips.isNotEmpty()) ChipRow(chips, chip, { it.name }) {
            chip = it
            prefs.edit().putString("live.group", it.name).apply()
        }
        Box(Modifier.fillMaxSize()) {
            LazyColumn(state = listState, modifier = Modifier.fillMaxSize()) {
                itemsIndexed(rows, key = { _, r -> r.first.item.sourceId + "|" + r.first.item.externalId }) { index, (entry, now) ->
                    ChannelRow(entry, now, selected = previewing?.item == entry.item) { select(index) }
                }
            }
            if (rows.isEmpty() && !loading) EmptyText(when {
                search.isNotBlank() -> "No channels match “$search”."
                chips.size > 1 -> "Nothing in this category."
                else -> "Add a playlist on the Playlists tab to get started."
            })
            if (loading && rows.isEmpty()) CircularProgressIndicator(Modifier.align(Alignment.TopCenter).padding(24.dp))
        }
    }
}

@Composable
fun ChannelRow(entry: LibraryEntry, now: Programme?, selected: Boolean = false, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().background(if (selected) SurfaceHigh else androidx.compose.ui.graphics.Color.Transparent)
        .clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
        Artwork(entry.item.logoUrl, initials(entry.item.title), Modifier.size(width = 68.dp, height = 44.dp))
        Column(Modifier.weight(1f)) {
            Text(entry.item.title, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(now?.let { "${hhmm.format(it.start)}  ${it.title}" } ?: entry.item.group, color = Muted, maxLines = 1,
                overflow = TextOverflow.Ellipsis, style = androidx.compose.material3.MaterialTheme.typography.bodySmall)
            if (now != null) ThinProgress(now.progress(), Modifier.padding(top = 3.dp))
        }
    }
}
