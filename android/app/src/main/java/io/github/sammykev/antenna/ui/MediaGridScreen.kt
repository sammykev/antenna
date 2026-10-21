package io.github.sammykev.antenna.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.WatchRecord
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private const val GRID_PAGE = 120

/** Movies or series as a poster grid, with category chips (continue watching and favourites first) and search. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MediaGridScreen(kind: MediaKind, open: (LibraryEntry) -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val version by Services.libraryVersion.collectAsState()
    val noun = if (kind == MediaKind.Movie) "movies" else "series"
    val prefKey = if (kind == MediaKind.Movie) "movies.group" else "series.group"
    val prefs = remember { context.getSharedPreferences("antenna", 0) }
    var query by rememberSaveable { mutableStateOf("") }
    val search = debounced(query)
    var chips by remember { mutableStateOf(emptyList<GroupChip>()) }
    var chip by remember { mutableStateOf<GroupChip?>(null) }
    val rows = remember { mutableStateListOf<Pair<LibraryEntry, Float>>() }
    var loading by remember { mutableStateOf(false) }
    var exhausted by remember { mutableStateOf(false) }

    LaunchedEffect(version) {
        val lib = Services.library
        val (groups, favs, resumable) = withContext(Dispatchers.IO) {
            Triple(lib.getGroups(kind), lib.getFavourites(kind).isNotEmpty(),
                kind == MediaKind.Movie && lib.getHistory(200).any { it.kind == MediaKind.Movie && it.isResumable })
        }
        chips = buildList {
            if (resumable) add(GroupChip("Continue watching", null, continueWatching = true))
            if (favs) add(GroupChip("★ Favourites", null, favourites = true))
            add(GroupChip(if (kind == MediaKind.Movie) "All movies" else "All series", null))
            groups.forEach { add(GroupChip(it.ifEmpty { "Other" }, it)) }
        }
        chip = chips.firstOrNull { it.name == (chip?.name ?: prefs.getString(prefKey, null)) } ?: chips.first()
    }

    fun load(reset: Boolean) {
        if (loading || (!reset && exhausted)) return
        loading = true
        val current = chip
        val offset = if (reset) 0 else rows.size
        scope.launch {
            val page = withContext(Dispatchers.IO) {
                val lib = Services.library
                // Films show how far you got; series progress is per episode, on the series page.
                val history: Map<Pair<String, String>, WatchRecord> = if (kind == MediaKind.Movie)
                    lib.getHistory(500).filter { it.kind == MediaKind.Movie }.associateBy { it.sourceId to it.externalId } else emptyMap()
                val entries = when {
                    search.isNotBlank() -> lib.search(search, kind, GRID_PAGE)
                    current?.continueWatching == true -> history.values.filter { it.isResumable }.sortedByDescending { it.watchedAt }
                        .mapNotNull { lib.getItem(it.sourceId, it.externalId, kind) }
                    current?.favourites == true -> lib.getFavourites(kind)
                    else -> lib.getItems(kind, group = current?.group, limit = GRID_PAGE, offset = offset)
                }
                entries.map { e -> e to (history[e.item.sourceId to e.item.externalId]?.takeIf { it.isResumable }?.progress ?: 0f) }
            }
            if (reset) rows.clear()
            rows.addAll(page)
            exhausted = page.size < GRID_PAGE || search.isNotBlank() || current?.favourites == true || current?.continueWatching == true
            loading = false
        }
    }

    LaunchedEffect(chip, search, version) {
        if (chip == null) return@LaunchedEffect
        loading = false
        load(reset = true)
    }

    val gridState = rememberLazyGridState()
    LoadMoreEffect(gridState) { load(reset = false) }

    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text(if (kind == MediaKind.Movie) "Movies" else "Series") })
        SearchField(query, { query = it }, "Search $noun")
        if (chips.isNotEmpty()) ChipRow(chips, chip, { it.name }) {
            chip = it
            prefs.edit().putString(prefKey, it.name).apply()
        }
        Box(Modifier.fillMaxSize()) {
            LazyVerticalGrid(GridCells.Adaptive(110.dp), state = gridState, contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp), verticalArrangement = Arrangement.spacedBy(14.dp),
                modifier = Modifier.fillMaxSize()) {
                items(rows, key = { it.first.item.sourceId + "|" + it.first.item.externalId }) { (entry, progress) ->
                    Column(Modifier.clickable { open(entry) }) {
                        Box {
                            Artwork(entry.item.logoUrl, entry.item.title, Modifier.fillMaxWidth().height(165.dp), crop = true)
                            if (progress > 0) ThinProgress(progress, Modifier.align(Alignment.BottomCenter))
                        }
                        Text(entry.item.title, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.padding(top = 4.dp))
                    }
                }
            }
            if (rows.isEmpty() && !loading) EmptyText(when {
                search.isNotBlank() -> "No $noun match “$search”."
                chips.size > 1 -> "Nothing in this category."
                else -> "No $noun yet. Xtream Codes logins bring their $noun in; M3U playlists may have some too."
            })
            if (loading && rows.isEmpty()) CircularProgressIndicator(Modifier.align(Alignment.TopCenter).padding(24.dp))
        }
    }
}
