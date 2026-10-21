package io.github.sammykev.antenna.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import io.github.sammykev.antenna.Playback
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.EventHit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

private val dayTime = DateTimeFormatter.ofPattern("EEE d MMM HH:mm").withZone(ZoneId.systemDefault())

fun whenText(hit: EventHit, now: Instant = Instant.now()): String {
    val p = hit.programme
    val today = p.start.atZone(ZoneId.systemDefault()).toLocalDate() == java.time.LocalDate.now()
    return if (today) "${hhmm.format(p.start)}–${hhmm.format(p.stop)}" else dayTime.format(p.start)
}

/** Finds programmes by title across every channel's guide, today and over the next week. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun GuideSearchScreen(back: () -> Unit) {
    val context = LocalContext.current
    var query by rememberSaveable { mutableStateOf("") }
    val search = debounced(query)
    var hits by remember { mutableStateOf(emptyList<EventHit>()) }
    var busy by remember { mutableStateOf(false) }
    var open by remember { mutableStateOf<EventHit?>(null) }

    LaunchedEffect(search) {
        if (search.trim().length < 2) {
            hits = emptyList()
            return@LaunchedEffect
        }
        busy = true
        val now = Instant.now()
        hits = withContext(Dispatchers.IO) {
            Services.library.searchEvents(search, now.minus(Duration.ofHours(4)), now.plus(Duration.ofDays(7)), 300, keepAllWords = true)
                .filter { it.programme.stop.isAfter(now) && it.channels.isNotEmpty() }
        }
        busy = false
    }

    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("Search the guide") }, navigationIcon = {
            IconButton(onClick = back) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") }
        })
        SearchField(query, { query = it }, "Programmes, e.g. Arsenal or Match of the Day")
        Box(Modifier.fillMaxSize()) {
            LazyColumn(Modifier.fillMaxSize()) {
                items(hits) { hit ->
                    val now = Instant.now()
                    Column(Modifier.fillMaxWidth().clickable {
                        if (hit.isLive(now)) Playback.playItems(context, listOf(hit.channels[0].item), 0) else open = hit
                    }.padding(horizontal = 16.dp, vertical = 10.dp)) {
                        Text(hit.programme.title, fontWeight = FontWeight.Bold)
                        Text("${whenText(hit)}  ·  ${if (hit.isLive(now)) "On now · " else ""}${hit.channels[0].item.title}",
                            color = Muted, style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
            if (hits.isEmpty() && !busy) EmptyText(if (search.length >= 2) "Nothing coming up matches “$search”."
                else "Find programmes on all your channels, today and over the next week.")
            if (busy) CircularProgressIndicator(Modifier.align(Alignment.TopCenter).padding(24.dp))
        }
    }

    open?.let { hit ->
        AlertDialog(onDismissRequest = { open = null },
            title = { Text(hit.programme.title) },
            text = { Text("${whenText(hit)} on ${hit.channels[0].item.title}.\n\n${hit.programme.description ?: ""}") },
            confirmButton = { TextButton(onClick = { open = null; Playback.playItems(context, listOf(hit.channels[0].item), 0) }) { Text("Watch channel now") } },
            dismissButton = { TextButton(onClick = { open = null }) { Text("Close") } })
    }
}
