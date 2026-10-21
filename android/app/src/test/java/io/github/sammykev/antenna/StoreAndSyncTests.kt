package io.github.sammykev.antenna

import io.github.sammykev.antenna.data.LibraryStore
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.PlaylistSource
import io.github.sammykev.antenna.data.Programme
import io.github.sammykev.antenna.data.SourceKind
import io.github.sammykev.antenna.data.WatchRecord
import io.github.sammykev.antenna.sync.SyncPassphraseException
import io.github.sammykev.antenna.sync.SyncService
import io.github.sammykev.antenna.sync.SyncTarget
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.time.Duration
import java.time.Instant

class StoreAndSyncTests {
    private val files = mutableListOf<File>()
    private val stores = mutableListOf<LibraryStore>()

    private fun newStore(): LibraryStore {
        val f = File.createTempFile("antenna", ".db").also { it.delete() }
        files += f
        return LibraryStore(f.path).also { stores += it }
    }

    @After
    fun cleanUp() {
        stores.forEach { it.close() }
        files.forEach { f -> listOf("", "-wal", "-shm").forEach { File(f.path + it).delete() } }
    }

    private class MemoryTarget : SyncTarget {
        var data: ByteArray? = null
        var writes = 0
        override fun read() = data
        override fun write(data: ByteArray) {
            this.data = data
            writes++
        }
    }

    private val file = MemoryTarget()
    private val pass = "correct horse"

    private fun source(id: String, name: String = "Provider") =
        PlaylistSource(id, name, SourceKind.Xtream, "http://h:8080", "me", "secret")

    private fun item(source: String, id: String, kind: MediaKind = MediaKind.Live, title: String = "Title $id", epg: String? = null) =
        MediaItem(source, id, kind, title, "G", streamUrl = "http://h:8080/live/me/secret/$id.ts", epgChannelId = epg)

    private fun sync(store: LibraryStore) = SyncService.sync(store, file, pass, "test")

    @Test
    fun storeBrowsesSearchesAndMasksCredentials() {
        val s = newStore()
        s.saveSource(source("a"))
        s.replaceItems("a", listOf(item("a", "1", title = "BBC News HD"), item("a", "2", title = "Sky Sports"), item("a", "3", MediaKind.Movie, "Film")))
        assertEquals(listOf("BBC News HD", "Sky Sports"), s.getItems(MediaKind.Live).map { it.item.title })
        assertEquals("BBC News HD", s.search("bbc new").single().item.title)
        assertEquals("http://h:8080/live/me/secret/1.ts", s.getItem("a", "1", MediaKind.Live)!!.item.streamUrl)
        assertEquals(2, s.countItems("a").live)
        assertEquals(listOf("G"), s.getGroups(MediaKind.Live))
    }

    @Test
    fun guideImportAndEventSearch() {
        val s = newStore()
        s.saveSource(source("a"))
        s.replaceItems("a", listOf(item("a", "1", title = "Sky Sports Main Event")))
        val now = Instant.now()
        s.beginGuideImport("a").use { w ->
            w.setEpgChannel("1", "sky.main")
            w.add(Programme("sky.main", now.minus(Duration.ofMinutes(30)), now.plus(Duration.ofMinutes(60)), "Live: Manchester United v Arsenal"))
            w.commit()
        }
        val hit = s.searchEvents("man utd v arsenal", now.minus(Duration.ofHours(1)), now.plus(Duration.ofHours(2))).single()
        assertEquals("Sky Sports Main Event", hit.channels.single().item.title)
        assertTrue(hit.isLive(now))
        assertEquals(1, s.getNowPlaying(now).size)
    }

    @Test
    fun playlistsFavouritesAndResumePointsSync() {
        val pc = newStore()
        val phone = newStore()
        pc.saveSource(source("a"))
        pc.saveSource(source("b"))
        pc.setFavourite(item("a", "1"), true)
        sync(pc)
        val outcome = sync(phone)
        assertEquals(setOf("a", "b"), outcome.newSources.map { it.id }.toSet())
        assertEquals("secret", phone.getSources().first().password)

        phone.deleteSource("b")
        phone.setFavourite(item("a", "1"), false)
        phone.saveSource(source("a", "Renamed"))
        val start = Instant.now().minusSeconds(3600)
        phone.recordWatch(WatchRecord("a", "m1", MediaKind.Movie, "Film", "http://h:8080/movie/me/secret/m1.mp4", positionMs = 1_800_000, lengthMs = 6_000_000, watchedAt = start))
        sync(phone)
        sync(pc)

        assertEquals(listOf("a"), pc.getSources().map { it.id })
        assertEquals("Renamed", pc.getSources().single().name)
        assertTrue(pc.getFavouriteKeys().isEmpty())
        assertEquals(1_800_000, pc.getWatch("a", "m1")!!.positionMs)
        val writes = file.writes
        sync(pc)
        assertEquals(writes, file.writes)
    }

    @Test(expected = SyncPassphraseException::class)
    fun wrongPassphraseIsRefused() {
        val pc = newStore()
        pc.saveSource(source("a"))
        sync(pc)
        assertFalse(String(file.data!!).contains("secret"))
        SyncService.sync(newStore(), file, "wrong", "x")
    }
}
