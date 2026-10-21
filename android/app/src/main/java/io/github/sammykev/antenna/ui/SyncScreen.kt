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
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import io.github.sammykev.antenna.sync.MobileSync
import kotlinx.coroutines.launch

/** Sync with the PC: the same passphrase and the same file (in Google Drive or OneDrive) as on Windows. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SyncScreen(back: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var pass by remember { mutableStateOf(MobileSync.passphrase(context) ?: "") }
    var tick by remember { mutableIntStateOf(0) }
    var busy by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }
    val status by MobileSync.status.collectAsState()
    val hasFile = remember(tick) { MobileSync.hasFile(context) }

    fun savePass(): Boolean {
        if (pass.length < 8) {
            problem = "Enter a passphrase of at least 8 characters, the same one as on your PC."
            return false
        }
        problem = null
        MobileSync.setPassphrase(context, pass)
        return true
    }

    fun syncNow() = scope.launch {
        busy = true
        MobileSync.run(context)
        busy = false
    }

    val onPicked: (Uri?) -> Unit = { uri ->
        if (uri != null) {
            MobileSync.useFile(context, uri)
            tick++
            syncNow()
        }
    }
    val open = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument(), onPicked)
    val create = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream"), onPicked)

    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("Sync with your PC") }, navigationIcon = { IconButton(onClick = back) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") } })
        Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Text("Keeps playlists, favourites and where you stopped films and episodes the same here and in Antenna on your PC, through one encrypted file in your Google Drive or OneDrive.", color = Muted)
            Text("On the PC: Settings → Sync with your phone, choose a folder inside Google Drive or OneDrive, enter a passphrase and Sync now. Then here: enter the same passphrase and choose that file (Antenna sync.antenna).", color = Muted)
            OutlinedTextField(pass, { pass = it }, label = { Text("Sync passphrase (the same on every device)") }, singleLine = true,
                visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth())
            Button(onClick = { if (savePass()) open.launch(arrayOf("*/*")) }, modifier = Modifier.fillMaxWidth()) { Text("Use the file from my PC…") }
            FilledTonalButton(onClick = { if (savePass()) create.launch("Antenna sync.antenna") }, modifier = Modifier.fillMaxWidth()) { Text("Create a new sync file…") }
            Text(if (hasFile) "Sync file: ${MobileSync.fileName(context)}" else "No sync file chosen yet.", fontWeight = FontWeight.Bold)
            Button(onClick = { if (savePass()) syncNow() }, enabled = hasFile && !busy, modifier = Modifier.fillMaxWidth()) { Text("Sync now") }
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            (problem ?: status ?: if (hasFile) "Syncs when Antenna opens and every few minutes." else null)?.let { Text(it, color = if (problem != null) Live else Muted) }
            if (hasFile) TextButton(onClick = { MobileSync.turnOff(context); pass = ""; tick++ }) { Text("Turn sync off", color = Muted) }
        }
    }
}
