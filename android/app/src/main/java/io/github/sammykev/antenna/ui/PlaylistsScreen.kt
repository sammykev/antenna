package io.github.sammykev.antenna.ui

import android.content.Intent
import androidx.compose.foundation.background
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import io.github.sammykev.antenna.BuildConfig
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.LibraryCounts
import io.github.sammykev.antenna.data.PlaylistSource
import io.github.sammykev.antenna.data.SourceKind
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.time.format.DateTimeFormatter

/** Refreshes playlists one at a time, reporting progress; shared by the list and the add screen. */
object Refreshing {
    val progress = kotlinx.coroutines.flow.MutableStateFlow<String?>(null)

    /** Returns the problems, if any. */
    suspend fun run(sources: List<PlaylistSource>): List<String> = withContext(Dispatchers.IO) {
        val problems = mutableListOf<String>()
        for (source in sources) {
            progress.value = "Loading ${source.name}… (the guide can take a minute)"
            try {
                val result = Services.refresher.refresh(source)
                Log.info("Refreshed ${source.name}: ${result.items} items, ${result.programmes} programmes")
                result.guideError?.let { problems += "${source.name}: channels loaded, but the guide failed ($it)" }
            } catch (e: Exception) {
                Log.error("Refreshing ${source.name} failed", e)
                problems += "${source.name}: ${e.message}"
            }
            Services.notifyChanged()
        }
        progress.value = null
        problems
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PlaylistsScreen(add: () -> Unit, openSync: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val version by Services.libraryVersion.collectAsState()
    val progress by Refreshing.progress.collectAsState()
    var sources by remember { mutableStateOf(emptyList<Pair<PlaylistSource, LibraryCounts>>()) }
    var menu by remember { mutableStateOf(false) }
    var problems by remember { mutableStateOf<List<String>>(emptyList()) }
    var deleting by remember { mutableStateOf<PlaylistSource?>(null) }

    LaunchedEffect(version) {
        sources = withContext(Dispatchers.IO) { Services.library.getSources().map { it to Services.library.countItems(it.id) } }
    }

    fun refresh(list: List<PlaylistSource>) = scope.launch { if (progress == null) problems = Refreshing.run(list) }

    Scaffold(
        contentWindowInsets = WindowInsets(0, 0, 0, 0),
        topBar = {
            TopAppBar(windowInsets = WindowInsets(0, 0, 0, 0), title = { Text("Playlists") }, actions = {
                IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, "More") }
                DropdownMenu(menu, { menu = false }) {
                    DropdownMenuItem(text = { Text("Sync with PC") }, onClick = { menu = false; openSync() })
                    DropdownMenuItem(text = { Text("Refresh all") }, onClick = { menu = false; refresh(sources.map { it.first }) })
                    DropdownMenuItem(text = { Text("Share log") }, onClick = { menu = false; shareLog(context) })
                }
            })
        },
        floatingActionButton = { ExtendedFloatingActionButton(onClick = add, icon = { Icon(Icons.Default.Add, null) }, text = { Text("Add") }) },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            progress?.let {
                Text(it, color = Muted, modifier = Modifier.padding(16.dp, 8.dp))
                LinearProgressIndicator(Modifier.fillMaxWidth())
            }
            if (sources.isEmpty()) Column(Modifier.fillMaxWidth().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                EmptyText("Add your M3U link or Xtream Codes login from your TV provider.")
                Button(onClick = add) { Text("Add a playlist") }
            }
            LazyColumn(Modifier.weight(1f)) {
                items(sources, key = { it.first.id }) { (source, counts) ->
                    Row(Modifier.fillMaxWidth().padding(12.dp, 6.dp).clip(RoundedCornerShape(10.dp)).background(Surface).padding(12.dp),
                        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Box(Modifier.width(6.dp).height(40.dp).clip(RoundedCornerShape(3.dp))
                            .background(runCatching { Color(android.graphics.Color.parseColor(source.color)) }.getOrDefault(Accent)))
                        Column(Modifier.weight(1f)) {
                            Text(source.name, fontWeight = FontWeight.Bold)
                            val updated = source.lastRefreshed?.atZoneSameInstant(java.time.ZoneId.systemDefault())
                                ?.format(DateTimeFormatter.ofPattern("d MMM HH:mm"))?.let { "updated $it" } ?: "not loaded yet"
                            Text("${if (source.kind == SourceKind.Xtream) "Xtream Codes" else "M3U"} · ${"%,d".format(counts.live)} channels · $updated",
                                color = Muted, style = MaterialTheme.typography.bodySmall)
                        }
                        TextButton(onClick = { refresh(listOf(source)) }) { Text("Refresh") }
                        TextButton(onClick = { deleting = source }) { Text("Delete", color = Muted) }
                    }
                }
            }
            Text("Antenna ${BuildConfig.VERSION_NAME}", color = Muted, style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.align(Alignment.CenterHorizontally).padding(8.dp))
        }
    }

    if (problems.isNotEmpty()) AlertDialog(onDismissRequest = { problems = emptyList() }, title = { Text("Some playlists had problems") },
        text = { Text(problems.joinToString("\n\n")) }, confirmButton = { TextButton(onClick = { problems = emptyList() }) { Text("OK") } })

    deleting?.let { source ->
        AlertDialog(onDismissRequest = { deleting = null }, title = { Text("Delete playlist?") },
            text = { Text("${source.name} and its channels and guide will be removed from this phone.") },
            confirmButton = {
                TextButton(onClick = {
                    deleting = null
                    scope.launch(Dispatchers.IO) {
                        Services.library.deleteSource(source.id)
                        File(Services.filesDir, "playlists/${source.id}.m3u").delete()
                        Services.notifyChanged()
                    }
                }) { Text("Delete") }
            },
            dismissButton = { TextButton(onClick = { deleting = null }) { Text("Cancel") } })
    }
}

private fun shareLog(context: android.content.Context) {
    val log = io.github.sammykev.antenna.Log.file
    if (!log.exists()) return
    val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", log)
    context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_STREAM, uri)
        .putExtra(Intent.EXTRA_SUBJECT, "Antenna log").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), "Share the Antenna log"))
}
