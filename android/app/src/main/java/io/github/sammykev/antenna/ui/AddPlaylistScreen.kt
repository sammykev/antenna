package io.github.sammykev.antenna.ui

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.PlaylistSource
import io.github.sammykev.antenna.data.SourceKind
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.net.URI
import java.util.UUID

private val colours = listOf("#4F7CFF", "#2EB67D", "#E8A33D", "#D9534F", "#9B6BFF", "#1FB5C9")

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AddPlaylistScreen(back: () -> Unit, done: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var type by remember { mutableIntStateOf(0) }  // 0 link, 1 file, 2 Xtream
    var name by remember { mutableStateOf("") }
    var location by remember { mutableStateOf("") }
    var user by remember { mutableStateOf("") }
    var pass by remember { mutableStateOf("") }
    var epg by remember { mutableStateOf("") }
    var file by remember { mutableStateOf<Uri?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var guideError by remember { mutableStateOf<String?>(null) }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            file = uri
            if (name.isBlank()) name = uri.lastPathSegment?.substringAfterLast('/')?.substringBeforeLast('.') ?: ""
        }
    }

    fun save() {
        error = when {
            type == 1 && file == null -> "Choose a playlist file."
            type != 1 && !(location.trim().startsWith("http://") || location.trim().startsWith("https://")) && !(type == 2 && location.isNotBlank()) ->
                "Enter the full link, starting with http:// or https://."
            type == 2 && (user.isBlank() || pass.isEmpty()) -> "Enter your username and password."
            else -> null
        }
        if (error != null) return
        busy = true
        scope.launch {
            val id = UUID.randomUUID().toString().replace("-", "")
            try {
                val source = withContext(Dispatchers.IO) {
                    var loc = location.trim()
                    if (type == 1) {
                        // Keep a copy: the original may be moved or deleted.
                        val copy = File(Services.filesDir, "playlists/$id.m3u").apply { parentFile?.mkdirs() }
                        context.contentResolver.openInputStream(file!!)!!.use { input -> copy.outputStream().use { input.copyTo(it) } }
                        loc = copy.path
                    }
                    val existing = Services.library.getSources().size
                    PlaylistSource(
                        id = id,
                        name = name.trim().ifEmpty { if (type == 2) runCatching { URI(if ("://" in loc) loc else "http://$loc").host }.getOrNull() ?: "Playlist" else "Playlist" },
                        kind = if (type == 2) SourceKind.Xtream else SourceKind.M3u,
                        location = loc.trimEnd('/'),
                        username = if (type == 2) user.trim() else null,
                        password = if (type == 2) pass else null,
                        epgUrl = epg.trim().ifEmpty { null },
                        color = colours[existing % colours.size],
                    ).also { Services.library.saveSource(it) }
                }
                Refreshing.progress.value = "Loading ${source.name}…"
                val result = withContext(Dispatchers.IO) { Services.refresher.refresh(source) }
                Refreshing.progress.value = null
                Services.notifyChanged()
                Log.info("Added ${source.name}: ${result.items} items, ${result.programmes} programmes")
                if (result.guideError != null) guideError = result.guideError else done()
            } catch (e: Exception) {
                Refreshing.progress.value = null
                Log.error("Adding a playlist failed", e)
                // Keep nothing half-added: the user can fix the details and try again.
                withContext(Dispatchers.IO) {
                    Services.library.deleteSource(id)
                    File(Services.filesDir, "playlists/$id.m3u").delete()
                }
                error = "Couldn't load the playlist: ${e.message}"
            }
            busy = false
        }
    }

    Column(Modifier.fillMaxSize()) {
        TopAppBar(windowInsets = WindowInsets(0, 0, 0, 0), title = { Text("Add playlist") }, navigationIcon = { IconButton(onClick = back) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") } })
        Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                listOf("M3U link", "M3U file", "Xtream Codes").forEachIndexed { i, label ->
                    SegmentedButton(type == i, { type = i }, SegmentedButtonDefaults.itemShape(i, 3)) { Text(label) }
                }
            }
            OutlinedTextField(name, { name = it }, label = { Text("Name (e.g. My provider)") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            if (type == 1) {
                OutlinedButton(onClick = { pick.launch(arrayOf("*/*")) }) { Text(if (file == null) "Choose M3U file…" else "File chosen ✓") }
            } else {
                OutlinedTextField(location, { location = it }, label = { Text(if (type == 2) "Server (e.g. http://example.com:8080)" else "M3U link (http://…)") },
                    singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri), modifier = Modifier.fillMaxWidth())
            }
            if (type == 2) {
                OutlinedTextField(user, { user = it }, label = { Text("Username") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(pass, { pass = it }, label = { Text("Password") }, singleLine = true, visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), modifier = Modifier.fillMaxWidth())
            }
            OutlinedTextField(epg, { epg = it }, label = { Text("Guide (XMLTV) link, optional") }, singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri), modifier = Modifier.fillMaxWidth())
            error?.let { Text(it, color = Live) }
            Button(onClick = ::save, enabled = !busy, modifier = Modifier.fillMaxWidth()) { Text("Add and load") }
            if (busy) {
                LinearProgressIndicator(Modifier.fillMaxWidth())
                Text("Loading… big playlists and guides can take a minute.", color = Muted)
            }
        }
    }

    guideError?.let {
        AlertDialog(onDismissRequest = { guideError = null; done() }, title = { Text("Guide not loaded") },
            text = { Text("The channels loaded, but the guide didn't: $it") },
            confirmButton = { TextButton(onClick = { guideError = null; done() }) { Text("OK") } })
    }
}
