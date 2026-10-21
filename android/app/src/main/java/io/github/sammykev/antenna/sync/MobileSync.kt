package io.github.sammykev.antenna.sync

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.KeystoreProtector
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import java.time.LocalTime
import java.time.format.DateTimeFormatter

/** The sync file as a document the user picked (Google Drive, OneDrive…), through Android's storage access. */
class DocumentSyncTarget(private val context: Context, private val uri: Uri) : SyncTarget {
    override fun read(): ByteArray? {
        val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: throw java.io.IOException("Couldn't open the sync file.")
        // A file just created by the picker is empty: nothing synced yet.
        return bytes.takeIf { it.isNotEmpty() }
    }

    override fun write(data: ByteArray) {
        val stream = try {
            context.contentResolver.openOutputStream(uri, "wt")
        } catch (_: Exception) {
            context.contentResolver.openOutputStream(uri, "w")  // some providers don't offer truncate
        }
        (stream ?: throw java.io.IOException("Couldn't write the sync file.")).use { it.write(data) }
    }
}

/** Syncs with the PC through the shared file: when Antenna opens, comes back, every few minutes and on demand. */
object MobileSync {
    private const val PREFS = "antenna.sync"
    private val mutex = Mutex()

    /** The last result for the Sync screen: a summary or an error. */
    val status = MutableStateFlow<String?>(null)

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, 0)

    fun fileName(context: Context): String? = prefs(context).getString("name", null)

    fun hasFile(context: Context) = prefs(context).contains("uri")

    fun passphrase(context: Context): String? =
        prefs(context).getString("pass", null)?.let { runCatching { KeystoreProtector().unprotect(it) }.getOrNull() }

    fun setPassphrase(context: Context, passphrase: String) =
        prefs(context).edit().putString("pass", KeystoreProtector().protect(passphrase)).apply()

    /** Keeps access to the picked file across restarts, and remembers it. */
    fun useFile(context: Context, uri: Uri) {
        context.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        if (prefs(context).getString("uri", null) != uri.toString())
            SyncService.resetBase(Services.library)  // a different file: merge into it, don't delete
        val name = context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        } ?: uri.lastPathSegment
        prefs(context).edit().putString("uri", uri.toString()).putString("name", name).apply()
    }

    fun turnOff(context: Context) {
        prefs(context).edit().clear().apply()
        SyncService.resetBase(Services.library)
        status.value = null
    }

    suspend fun run(context: Context) = withContext(Dispatchers.IO) {
        val uri = prefs(context).getString("uri", null) ?: return@withContext
        val passphrase = passphrase(context)?.takeIf { it.isNotEmpty() } ?: return@withContext
        if (!mutex.tryLock()) return@withContext
        try {
            val outcome = SyncService.sync(Services.library, DocumentSyncTarget(context, Uri.parse(uri)), passphrase, android.os.Build.MODEL)
            if (outcome.changes > 0 || outcome.wrote)
                Log.info("Sync: ${outcome.changes} change(s), ${outcome.newSources.size} new playlist(s), wrote=${outcome.wrote}")
            if (outcome.changes > 0) Services.notifyChanged()
            val at = LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm"))
            status.value = "Last synced $at. " + if (outcome.changes == 0) "Up to date." else "${outcome.changes} change(s) from your other devices."
            for (source in outcome.newSources) {
                try { Services.refresher.refresh(source) } catch (e: Exception) { Log.info("Sync: couldn't load ${source.name}: ${e.message}") }
            }
        } catch (e: SecurityException) {
            status.value = "Sync failed: Antenna lost access to the sync file. Choose it again."
        } catch (e: Exception) {
            Log.info("Sync failed: ${e.message}")
            status.value = "Sync failed: ${e.message}"
        } finally {
            mutex.unlock()
        }
    }
}
