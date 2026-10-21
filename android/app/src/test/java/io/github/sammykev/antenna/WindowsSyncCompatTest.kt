package io.github.sammykev.antenna

import io.github.sammykev.antenna.data.LibraryStore
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.PlaylistSource
import io.github.sammykev.antenna.data.SourceKind
import io.github.sammykev.antenna.sync.SyncService
import io.github.sammykev.antenna.sync.SyncTarget
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File
import java.time.Instant

/**
 * A sync file written by the Windows app (src/test/resources/windows-sync.antenna, passphrase "correct horse"):
 * the phone must read it, and write one the Windows app reads back (build/kotlin-sync.antenna).
 */
class WindowsSyncCompatTest {
    @Test
    fun readsTheWindowsFileAndWritesOneBack() {
        val db = File.createTempFile("compat", ".db").also { it.delete() }
        val copy = File("build/kotlin-sync.antenna")
        copy.parentFile.mkdirs()
        copy.writeBytes(javaClass.getResourceAsStream("/windows-sync.antenna")!!.readBytes())
        val target = object : SyncTarget {
            override fun read() = copy.readBytes()
            override fun write(data: ByteArray) = copy.writeBytes(data)
        }
        LibraryStore(db.path).use { store ->
            val outcome = SyncService.sync(store, target, "correct horse", "Phone")
            val source = store.getSources().single()
            assertEquals("From Windows", source.name)
            assertEquals("päss word", source.password)
            assertEquals("win1", outcome.newSources.single().id)
            assertEquals(listOf(Triple("win1", "42", MediaKind.Live)), store.getFavouriteKeys())
            val watch = store.getWatch("win1", "ep:7")!!
            assertEquals(900_000, watch.positionMs)
            assertEquals(Instant.parse("2026-10-20T20:15:30.1234567Z"), watch.watchedAt.let { Instant.ofEpochMilli(it.toEpochMilli()) }.plusNanos(4567 * 100))

            // A change on the phone, for the Windows side of the check.
            store.saveSource(PlaylistSource("phone1", "From Phone", SourceKind.M3u, "http://p/list.m3u"))
            store.setFavourite("win1", "43", MediaKind.Live, true)
            SyncService.sync(store, target, "correct horse", "Phone")
        }
        listOf("", "-wal", "-shm").forEach { File(db.path + it).delete() }
    }
}
