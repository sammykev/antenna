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
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
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
    var chips by remember { mutableStateOf(emptyList<GroupChip>()) }
    var chip by remember { mutableStateOf<GroupChip?>(null) }
    val rows = remember { mutableStateListOf<Pair<LibraryEntry, Programme?>>() }
    var loading by remember { mutableStateOf(false) }
    var exhausted by remember { mutableStateOf(false) }
    val prefs = remember { context.getSharedPreferences("antenna", 0) }

    LaunchedEffect(version) {
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
        loading = false
        load(reset = true)
    }

    val listState = rememberLazyListState()
    LoadMoreEffect(listState) { load(reset = false) }

    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("Live TV") }, actions = {
            IconButton(onClick = openGuideSearch) { Icon(Icons.Default.EventNote, "Search the guide") }
        })
        SearchField(query, { query = it }, "Search channels")
        if (chips.isNotEmpty()) ChipRow(chips, chip, { it.name }) {
            chip = it
            prefs.edit().putString("live.group", it.name).apply()
        }
        Box(Modifier.fillMaxSize()) {
            LazyColumn(state = listState, modifier = Modifier.fillMaxSize()) {
                itemsIndexed(rows, key = { _, r -> r.first.item.sourceId + "|" + r.first.item.externalId }) { index, (entry, now) ->
                    ChannelRow(entry, now) { Playback.playItems(context, rows.map { it.first.item }, index) }
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
fun ChannelRow(entry: LibraryEntry, now: Programme?, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 12.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
        Artwork(entry.item.logoUrl, initials(entry.item.title), Modifier.size(width = 56.dp, height = 40.dp))
        Column(Modifier.weight(1f)) {
            Text(entry.item.title, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(now?.let { "${hhmm.format(it.start)}  ${it.title}" } ?: entry.item.group, color = Muted, maxLines = 1,
                overflow = TextOverflow.Ellipsis, style = androidx.compose.material3.MaterialTheme.typography.bodySmall)
            if (now != null) ThinProgress(now.progress(), Modifier.padding(top = 3.dp))
        }
    }
}
