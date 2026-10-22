package io.github.sammykev.antenna.data

import androidx.sqlite.SQLiteConnection
import androidx.sqlite.SQLiteStatement
import androidx.sqlite.driver.bundled.BundledSQLiteDriver
import androidx.sqlite.execSQL
import java.io.Closeable
import java.time.Instant
import java.time.OffsetDateTime

/** Encrypts secrets at rest: the Android Keystore in the app, a fake in tests. */
interface SecretProtector {
    fun protect(plaintext: String): String
    fun unprotect(ciphertext: String): String
}

/**
 * The local library: every playlist's items with an FTS5 index, the guide, favourites, history and settings. The
 * schema is the Windows app's (LibraryStore.cs), and it opens the database the earlier phone app left behind as is.
 * Safe from any thread: writes are serialised on one connection; searches read on their own.
 */
class LibraryStore(private val path: String, private val protector: SecretProtector? = null) : Closeable {
    private val driver = BundledSQLiteDriver()
    private val db: SQLiteConnection = open()
    private val gate = Any()
    private var credentialCache: Map<String, Pair<String, String>>? = null

    init {
        db.execSQL("PRAGMA journal_mode = WAL")
        migrate()
    }

    private fun open(): SQLiteConnection = driver.open(path).also {
        it.execSQL("PRAGMA busy_timeout = 10000")
        it.execSQL("PRAGMA foreign_keys = ON")
    }

    private fun migrate() = synchronized(gate) {
        exec("""
            CREATE TABLE IF NOT EXISTS sources (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, kind INTEGER NOT NULL, location TEXT NOT NULL, username TEXT, password TEXT,
                epg_url TEXT, color TEXT NOT NULL, icon TEXT NOT NULL, last_refreshed TEXT, position INTEGER NOT NULL DEFAULT 0)""")
        exec("""
            CREATE TABLE IF NOT EXISTS items (
                rowid INTEGER PRIMARY KEY, source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE, external_id TEXT NOT NULL,
                kind INTEGER NOT NULL, title TEXT NOT NULL, grp TEXT NOT NULL, logo TEXT, stream_url TEXT, epg_id TEXT,
                catchup_days INTEGER NOT NULL, sort_index INTEGER NOT NULL)""")
        exec("CREATE INDEX IF NOT EXISTS items_browse ON items(kind, source_id, grp, sort_index)")
        exec("CREATE INDEX IF NOT EXISTS items_key ON items(source_id, external_id)")
        exec("CREATE INDEX IF NOT EXISTS items_epg ON items(source_id, epg_id)")
        exec("CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(title, grp, tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3 4')")
        exec("""
            CREATE TABLE IF NOT EXISTS programmes (
                source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE, channel_id TEXT NOT NULL, start INTEGER NOT NULL,
                stop INTEGER NOT NULL, title TEXT NOT NULL, description TEXT)""")
        exec("CREATE INDEX IF NOT EXISTS programmes_channel ON programmes(source_id, channel_id, start)")
        exec("CREATE INDEX IF NOT EXISTS programmes_time ON programmes(start, stop)")
        // The guide index reads its text from programmes rather than keeping a second copy, and is rebuilt in one pass
        // after each import (LibraryStore.cs): far faster than indexing row by row, which took minutes on a phone.
        val rebuildGuideIndex = dropIfMissing("programmes_fts", "content")
        exec("""
            CREATE VIRTUAL TABLE IF NOT EXISTS programmes_fts USING fts5(title, description, content = 'programmes', detail = none,
                tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3 4')""")
        if (rebuildGuideIndex) exec("INSERT INTO programmes_fts (programmes_fts) VALUES ('rebuild')")
        addColumnIfMissing("items", "catchup_mode", "TEXT")
        addColumnIfMissing("items", "catchup_source", "TEXT")
        addColumnIfMissing("items", "player_options", "TEXT")
        exec("""
            CREATE TABLE IF NOT EXISTS favourites (
                source_id TEXT NOT NULL, external_id TEXT NOT NULL, kind INTEGER NOT NULL, added INTEGER NOT NULL,
                PRIMARY KEY (source_id, external_id, kind))""")
        exec("""
            CREATE TABLE IF NOT EXISTS history (
                source_id TEXT NOT NULL, external_id TEXT NOT NULL, kind INTEGER NOT NULL, title TEXT NOT NULL, stream_url TEXT NOT NULL,
                logo TEXT, player_options TEXT, position_ms INTEGER NOT NULL, length_ms INTEGER NOT NULL, watched_at INTEGER NOT NULL,
                PRIMARY KEY (source_id, external_id))""")
        exec("CREATE INDEX IF NOT EXISTS history_recent ON history(watched_at DESC)")
        exec("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        exec("CREATE TABLE IF NOT EXISTS group_flags (kind INTEGER NOT NULL, grp TEXT NOT NULL, flag TEXT NOT NULL, PRIMARY KEY (kind, grp, flag))")
    }

    /** Drops [table] if its definition lacks [option]; true when it did (so it needs filling again once recreated). */
    private fun dropIfMissing(table: String, option: String): Boolean {
        val sql = db.prepare("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = ?1").use { if (it.apply { bind(table) }.step()) it.getText(0) else null }
        if (sql == null || sql.contains(option, ignoreCase = true)) return false
        exec("DROP TABLE $table")
        return true
    }

    private fun addColumnIfMissing(table: String, column: String, type: String) {
        val count = db.prepare("SELECT COUNT(*) FROM pragma_table_info('$table') WHERE name = ?").use { it.bindText(1, column); it.step(); it.getLong(0) }
        if (count == 0L) exec("ALTER TABLE $table ADD COLUMN $column $type")
    }

    // ---- Sources ----

    fun saveSource(source: PlaylistSource) = synchronized(gate) {
        db.prepare("""
            INSERT INTO sources (id, name, kind, location, username, password, epg_url, color, icon, last_refreshed, position)
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, (SELECT COALESCE(MAX(position) + 1, 0) FROM sources))
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, kind = excluded.kind, location = excluded.location,
                username = excluded.username, password = excluded.password, epg_url = excluded.epg_url, color = excluded.color,
                icon = excluded.icon, last_refreshed = excluded.last_refreshed""").use {
            it.bind(source.id, source.name, source.kind.ordinal, source.location, source.username, protect(source.password), source.epgUrl,
                source.color, source.icon, source.lastRefreshed?.toString())
            it.step()
        }
        credentialCache = null
    }

    fun getSources(): List<PlaylistSource> = synchronized(gate) { readSources() }

    private fun readSources(): List<PlaylistSource> =
        db.prepare("SELECT id, name, kind, location, username, password, epg_url, color, icon, last_refreshed FROM sources ORDER BY position").use { st ->
            buildList {
                while (st.step()) add(PlaylistSource(
                    id = st.getText(0), name = st.getText(1), kind = SourceKind.entries[st.getInt(2)], location = st.getText(3),
                    username = st.textOrNull(4), password = st.textOrNull(5)?.let(::unprotect), epgUrl = st.textOrNull(6),
                    color = st.getText(7), icon = st.getText(8),
                    lastRefreshed = st.textOrNull(9)?.let { runCatching { OffsetDateTime.parse(it) }.getOrNull() }))
            }
        }

    fun deleteSource(sourceId: String) = synchronized(gate) {
        transaction {
            deleteItems(sourceId, null)
            for (sql in listOf(
                "INSERT INTO programmes_fts (programmes_fts, rowid, title, description) SELECT 'delete', rowid, title, description FROM programmes WHERE source_id = ?1",
                "DELETE FROM programmes WHERE source_id = ?1", "DELETE FROM favourites WHERE source_id = ?1",
                "DELETE FROM history WHERE source_id = ?1", "DELETE FROM sources WHERE id = ?1",
            )) db.prepare(sql).use { it.bind(sourceId); it.step() }
        }
        credentialCache = null
    }

    private fun protect(secret: String?) = if (secret == null || protector == null) secret else ENCRYPTED_PREFIX + protector.protect(secret)

    private fun unprotect(stored: String): String? {
        if (!stored.startsWith(ENCRYPTED_PREFIX)) return stored
        // Encrypted with a key this device no longer has: the user re-enters it.
        return runCatching { protector?.unprotect(stored.removePrefix(ENCRYPTED_PREFIX)) }.getOrNull()
    }

    /** Xtream credentials per source, for masking/unmasking stream URLs. */
    private val credentials: Map<String, Pair<String, String>>
        get() = credentialCache ?: readSources()
            .filter { it.kind == SourceKind.Xtream && !it.username.isNullOrEmpty() && it.password != null }
            .associate { it.id to (it.username!! to it.password!!) }
            .also { credentialCache = it }

    private fun credentialSegment(c: Pair<String, String>) = "/${escapeData(c.first)}/${escapeData(c.second)}/"

    private fun mask(sourceId: String, url: String?, creds: Map<String, Pair<String, String>> = credentials): String? {
        val c = creds[sourceId] ?: return url
        return url?.replace(credentialSegment(c), CREDENTIAL_TOKEN)
    }

    private fun unmask(sourceId: String, url: String?, creds: Map<String, Pair<String, String>>): String? {
        val c = creds[sourceId] ?: return url
        return if (url != null && url.contains(CREDENTIAL_TOKEN)) url.replace(CREDENTIAL_TOKEN, credentialSegment(c)) else url
    }

    // ---- Items ----

    /** Replaces a source's catalogue, or only one kind of it. */
    fun replaceItems(sourceId: String, items: List<MediaItem>, kind: MediaKind? = null) = synchronized(gate) {
        val creds = credentials
        transaction {
            deleteItems(sourceId, kind)
            val insert = db.prepare("""
                INSERT INTO items (source_id, external_id, kind, title, grp, logo, stream_url, epg_id, catchup_days, catchup_mode,
                                   catchup_source, player_options, sort_index)
                VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)""")
            try {
                for (item in items) {
                    insert.reset(); insert.clearBindings()
                    insert.bind(sourceId, item.externalId, item.kind.ordinal, item.title, item.group, item.logoUrl, mask(sourceId, item.streamUrl, creds),
                        item.epgChannelId, item.catchupDays, item.catchupMode, item.catchupSource, item.playerOptions, item.sortIndex)
                    insert.step()
                }
            } finally {
                insert.close()
            }
            // Index the new rows for search in one statement rather than one call per item.
            db.prepare("INSERT INTO items_fts (rowid, title, grp) SELECT rowid, title, grp FROM items WHERE source_id = ?1 AND (?2 IS NULL OR kind = ?2)")
                .use { it.bind(sourceId, kind?.ordinal); it.step() }
        }
    }

    private fun deleteItems(sourceId: String, kind: MediaKind?) {
        // The guide is kept: it's replaced separately, and a failed guide download shouldn't erase it.
        for (sql in listOf(
            "DELETE FROM items_fts WHERE rowid IN (SELECT rowid FROM items WHERE source_id = ?1 AND (?2 IS NULL OR kind = ?2))",
            "DELETE FROM items WHERE source_id = ?1 AND (?2 IS NULL OR kind = ?2)",
        )) db.prepare(sql).use { it.bind(sourceId, kind?.ordinal); it.step() }
    }

    /** Categories: pinned first, then provider order, minus hidden ones. */
    fun getGroups(kind: MediaKind, sourceId: String? = null): List<String> = synchronized(gate) {
        db.prepare("""
            SELECT grp FROM items i WHERE kind = ?1 AND (?2 IS NULL OR source_id = ?2)
              AND NOT EXISTS (SELECT 1 FROM group_flags gf WHERE gf.kind = i.kind AND gf.grp = i.grp AND gf.flag = 'hidden')
            GROUP BY grp
            ORDER BY EXISTS (SELECT 1 FROM group_flags gp WHERE gp.kind = ?1 AND gp.grp = i.grp AND gp.flag = 'pinned') DESC, MIN(sort_index)
            """).use { st -> st.bind(kind.ordinal, sourceId); buildList { while (st.step()) add(st.getText(0)) } }
    }

    fun getItems(kind: MediaKind, sourceId: String? = null, group: String? = null, limit: Int = 5000, offset: Int = 0): List<LibraryEntry> =
        readAside { conn, creds ->
            conn.prepare("""
                $SELECT_ENTRIES
                WHERE i.kind = ?1 AND (?2 IS NULL OR i.source_id = ?2) AND (?3 IS NULL OR i.grp = ?3) AND $VISIBLE
                ORDER BY s.position, i.sort_index LIMIT ?4 OFFSET ?5""").use { it.bind(kind.ordinal, sourceId, group, limit, offset); readEntries(it, creds) }
        }

    fun countItems(sourceId: String? = null): LibraryCounts = readAside { conn, _ ->
        val counts = IntArray(3)
        conn.prepare("SELECT i.kind, COUNT(*) FROM items i WHERE (?1 IS NULL OR i.source_id = ?1) AND $VISIBLE GROUP BY i.kind").use {
            it.bind(sourceId)
            while (it.step()) it.getInt(0).takeIf { k -> k in 0..2 }?.let { k -> counts[k] = it.getInt(1) }
        }
        val playlists = conn.prepare("SELECT COUNT(*) FROM sources").use { it.step(); it.getInt(0) }
        LibraryCounts(counts[0], counts[1], counts[2], playlists)
    }

    fun getItem(sourceId: String, externalId: String, kind: MediaKind): LibraryEntry? = readAside { conn, creds ->
        conn.prepare("$SELECT_ENTRIES WHERE i.source_id = ?1 AND i.external_id = ?2 AND i.kind = ?3 AND $VISIBLE LIMIT 1")
            .use { it.bind(sourceId, externalId, kind.ordinal); readEntries(it, creds).firstOrNull() }
    }

    /** Prefix search over titles and categories across all playlists: "bbc new" finds "BBC News HD". */
    fun search(query: String, kind: MediaKind? = null, limit: Int = 200, sourceId: String? = null, group: String? = null): List<LibraryEntry> {
        val match = buildMatchExpression(query) ?: return emptyList()
        return readAside { conn, creds ->
            conn.prepare("""
                $SELECT_ENTRIES JOIN items_fts f ON f.rowid = i.rowid
                WHERE items_fts MATCH ?1 AND (?2 IS NULL OR i.kind = ?2) AND (?3 IS NULL OR i.source_id = ?3) AND (?4 IS NULL OR i.grp = ?4) AND $VISIBLE
                ORDER BY bm25(items_fts, 10.0, 1.0), i.kind, s.position, i.sort_index LIMIT ?5""")
                .use { it.bind(match, kind?.ordinal, sourceId, group, limit); readEntries(it, creds) }
        }
    }

    // ---- Guide ----

    /** Replaces a source's guide on its own connection, so a big import doesn't block browsing. */
    fun beginGuideImport(sourceId: String) = ProgrammeWriter(open(), sourceId)

    fun getProgrammes(sourceId: String, channelId: String, from: Instant, to: Instant): List<Programme> = readAside { conn, _ ->
        conn.prepare("""
            SELECT channel_id, start, stop, title, description FROM programmes
            WHERE source_id = ?1 AND channel_id = ?2 AND stop > ?3 AND start < ?4 ORDER BY start""").use {
            it.bind(sourceId, channelId, from.epochSecond, to.epochSecond)
            buildList { while (it.step()) add(readProgramme(it, 0)) }
        }
    }

    /** What's on at [at] on every channel, keyed by (source id, channel id). */
    fun getNowPlaying(at: Instant): Map<Pair<String, String>, Programme> = readAside { conn, _ ->
        conn.prepare("SELECT source_id, channel_id, start, stop, title, description FROM programmes WHERE start <= ?1 AND stop > ?1").use {
            it.bind(at.epochSecond)
            buildMap { while (it.step()) put(it.getText(0) to it.getText(1), readProgramme(it, 1)) }
        }
    }

    /** Programmes whose title (or description) matches, with the channels showing them (LibraryStore.Events.cs). */
    fun searchEvents(query: String, from: Instant, to: Instant, limit: Int = 200, keepAllWords: Boolean = false): List<EventHit> {
        val match = EventSearch.buildExpression(query, keepAllWords) ?: return emptyList()
        val rows = readAside { conn, creds ->
            conn.prepare("""
                SELECT p.channel_id, p.start, p.stop, p.title, p.description,
                       i.source_id, i.external_id, i.kind, i.title, i.grp, i.logo, i.stream_url, i.epg_id, i.catchup_days, i.sort_index,
                       s.name, s.color, s.icon, i.catchup_mode, i.catchup_source, i.player_options
                FROM programmes p
                JOIN items i ON i.source_id = p.source_id AND i.epg_id = p.channel_id AND i.kind = 0
                JOIN sources s ON s.id = i.source_id
                WHERE p.stop > ?2 AND p.start < ?3
                  AND p.rowid IN (SELECT rowid FROM programmes_fts WHERE programmes_fts MATCH ?1) AND $VISIBLE
                ORDER BY p.start, s.position, i.sort_index LIMIT ?4""").use { st ->
                st.bind(match, from.epochSecond, to.epochSecond, limit * 4)
                buildList {
                    while (st.step()) {
                        val sourceId = st.getText(5)
                        val item = MediaItem(sourceId, st.getText(6), MediaKind.entries[st.getInt(7)], st.getText(8), st.getText(9), st.textOrNull(10),
                            unmask(sourceId, st.textOrNull(11), creds), st.textOrNull(12), st.getInt(13), st.textOrNull(18), st.textOrNull(19),
                            st.textOrNull(20), st.getInt(14))
                        add(readProgramme(st, 0) to LibraryEntry(item, st.getText(15), st.getText(16), st.getText(17)))
                    }
                }
            }
        }
        val now = Instant.now()
        return rows.groupBy { Triple(it.first.title.trim().lowercase(), it.first.start, it.first.stop) }
            .map { (_, g) -> EventHit(g.first().first, g.map { it.second }.distinctBy { it.item.sourceId to it.item.externalId }) }
            .sortedWith(compareByDescending<EventHit> { it.isLive(now) }.thenBy { it.programme.start })
            .take(limit)
    }

    // ---- Favourites ----

    fun setFavourite(item: MediaItem, favourite: Boolean) = setFavourite(item.sourceId, item.externalId, item.kind, favourite)

    fun setFavourite(sourceId: String, externalId: String, kind: MediaKind, favourite: Boolean) = synchronized(gate) {
        db.prepare(if (favourite) "INSERT OR IGNORE INTO favourites (source_id, external_id, kind, added) VALUES (?1, ?2, ?3, ?4)"
            else "DELETE FROM favourites WHERE source_id = ?1 AND external_id = ?2 AND kind = ?3").use {
            if (favourite) it.bind(sourceId, externalId, kind.ordinal, Instant.now().epochSecond) else it.bind(sourceId, externalId, kind.ordinal)
            it.step()
        }
    }

    fun isFavourite(item: MediaItem): Boolean = synchronized(gate) {
        db.prepare("SELECT 1 FROM favourites WHERE source_id = ?1 AND external_id = ?2 AND kind = ?3").use {
            it.bind(item.sourceId, item.externalId, item.kind.ordinal); it.step()
        }
    }

    /** Favourites that still exist in their playlist, newest first. */
    fun getFavourites(kind: MediaKind? = null): List<LibraryEntry> = readAside { conn, creds ->
        conn.prepare("""
            $SELECT_ENTRIES JOIN favourites fav ON fav.source_id = i.source_id AND fav.external_id = i.external_id AND fav.kind = i.kind
            WHERE (?1 IS NULL OR i.kind = ?1) AND $VISIBLE ORDER BY fav.added DESC""").use { it.bind(kind?.ordinal); readEntries(it, creds) }
    }

    /** Every favourite's key, including ones whose playlist hasn't been loaded on this device. */
    fun getFavouriteKeys(): List<Triple<String, String, MediaKind>> = synchronized(gate) {
        db.prepare("SELECT source_id, external_id, kind FROM favourites ORDER BY added DESC").use {
            buildList { while (it.step()) add(Triple(it.getText(0), it.getText(1), MediaKind.entries[it.getInt(2)])) }
        }
    }

    // ---- History ----

    fun recordWatch(record: WatchRecord) = synchronized(gate) {
        db.prepare("""
            INSERT INTO history (source_id, external_id, kind, title, stream_url, logo, player_options, position_ms, length_ms, watched_at)
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)
            ON CONFLICT(source_id, external_id) DO UPDATE SET kind = excluded.kind, title = excluded.title, stream_url = excluded.stream_url,
                logo = excluded.logo, player_options = excluded.player_options, position_ms = excluded.position_ms,
                length_ms = excluded.length_ms, watched_at = excluded.watched_at""").use {
            it.bind(record.sourceId, record.externalId, record.kind.ordinal, record.title, mask(record.sourceId, record.streamUrl), record.logoUrl,
                record.playerOptions, record.positionMs, record.lengthMs, record.watchedAt.toEpochMilli())
            it.step()
        }
    }

    fun getWatch(sourceId: String, externalId: String): WatchRecord? = synchronized(gate) {
        db.prepare("$SELECT_HISTORY WHERE h.source_id = ?1 AND h.external_id = ?2").use { it.bind(sourceId, externalId); readHistory(it).firstOrNull() }
    }

    fun getHistory(limit: Int = 50): List<WatchRecord> = synchronized(gate) {
        db.prepare("$SELECT_HISTORY ORDER BY h.watched_at DESC LIMIT ?1").use { it.bind(limit); readHistory(it) }
    }

    fun removeWatch(sourceId: String, externalId: String) = synchronized(gate) {
        db.prepare("DELETE FROM history WHERE source_id = ?1 AND external_id = ?2").use { it.bind(sourceId, externalId); it.step() }
    }

    private fun readHistory(st: SQLiteStatement): List<WatchRecord> {
        val creds = credentials
        return buildList {
            while (st.step()) {
                val sourceId = st.getText(0)
                add(WatchRecord(sourceId, st.getText(1), MediaKind.entries[st.getInt(2)], st.getText(3), unmask(sourceId, st.getText(4), creds)!!,
                    st.textOrNull(5), st.textOrNull(6), st.getLong(7), st.getLong(8), Instant.ofEpochMilli(st.getLong(9))))
            }
        }
    }

    // ---- Settings ----

    fun getSetting(key: String): String? = synchronized(gate) {
        db.prepare("SELECT value FROM settings WHERE key = ?1").use { it.bind(key); if (it.step()) it.getText(0) else null }
    }

    fun setSetting(key: String, value: String?) = synchronized(gate) {
        db.prepare(if (value == null) "DELETE FROM settings WHERE key = ?1"
            else "INSERT INTO settings (key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value").use {
            if (value == null) it.bind(key) else it.bind(key, value)
            it.step()
        }
    }

    // ---- Helpers ----

    private fun <T> readAside(query: (SQLiteConnection, Map<String, Pair<String, String>>) -> T): T {
        val creds = synchronized(gate) { credentials }
        val conn = open()
        try {
            return query(conn, creds)
        } finally {
            conn.close()
        }
    }

    private fun readEntries(st: SQLiteStatement, creds: Map<String, Pair<String, String>>): List<LibraryEntry> = buildList {
        while (st.step()) {
            val sourceId = st.getText(0)
            val item = MediaItem(sourceId, st.getText(1), MediaKind.entries[st.getInt(2)], st.getText(3), st.getText(4), st.textOrNull(5),
                unmask(sourceId, st.textOrNull(6), creds), st.textOrNull(7), st.getInt(8), st.textOrNull(13), st.textOrNull(14), st.textOrNull(15),
                st.getInt(9))
            add(LibraryEntry(item, st.getText(10), st.getText(11), st.getText(12)))
        }
    }

    private fun readProgramme(st: SQLiteStatement, first: Int) = Programme(
        st.getText(first), Instant.ofEpochSecond(st.getLong(first + 1)), Instant.ofEpochSecond(st.getLong(first + 2)),
        st.getText(first + 3), st.textOrNull(first + 4))

    private fun exec(sql: String) = db.execSQL(sql)

    private inline fun transaction(block: () -> Unit) {
        db.execSQL("BEGIN IMMEDIATE")
        try {
            block()
            db.execSQL("COMMIT")
        } catch (e: Throwable) {
            db.execSQL("ROLLBACK")
            throw e
        }
    }

    override fun close() = synchronized(gate) { db.close() }

    companion object {
        private const val ENCRYPTED_PREFIX = "enc:"

        /** Stands in for "/user/pass/" inside stored Xtream URLs so credentials live only in the sources table. */
        private const val CREDENTIAL_TOKEN = "/{xc}/"

        /** Hidden categories never appear (there's no parental lock on the phone yet). */
        private const val VISIBLE = "NOT EXISTS (SELECT 1 FROM group_flags gf WHERE gf.kind = i.kind AND gf.grp = i.grp AND gf.flag = 'hidden')"

        private const val SELECT_ENTRIES = """
            SELECT i.source_id, i.external_id, i.kind, i.title, i.grp, i.logo, i.stream_url, i.epg_id, i.catchup_days, i.sort_index,
                   s.name, s.color, s.icon, i.catchup_mode, i.catchup_source, i.player_options
            FROM items i JOIN sources s ON s.id = i.source_id"""

        private const val SELECT_HISTORY = """
            SELECT h.source_id, h.external_id, h.kind, h.title, h.stream_url, h.logo, h.player_options, h.position_ms, h.length_ms, h.watched_at
            FROM history h"""

        /** Free text to an FTS5 expression of quoted prefix terms, so typed input can't inject syntax. */
        fun buildMatchExpression(query: String): String? {
            val terms = Regex("[\\p{L}\\p{N}]+").findAll(query).map { it.value }.toList()
            return if (terms.isEmpty()) null else terms.joinToString(" ") { "\"$it\"*" }
        }
    }
}

/** Writes a source's guide in one transaction on its own connection (ProgrammeWriter.cs). */
class ProgrammeWriter internal constructor(private val db: SQLiteConnection, sourceId: String) : Closeable {
    private val insert: SQLiteStatement
    private val setEpg: SQLiteStatement
    private val source = sourceId
    var count = 0
        private set

    init {
        db.execSQL("BEGIN IMMEDIATE")
        db.prepare("DELETE FROM programmes WHERE source_id = ?1").use { it.bind(sourceId); it.step() }
        insert = db.prepare("INSERT INTO programmes (source_id, channel_id, start, stop, title, description) VALUES (?1, ?2, ?3, ?4, ?5, ?6)")
        setEpg = db.prepare("UPDATE items SET epg_id = ?1 WHERE source_id = ?2 AND external_id = ?3 AND kind = 0")
    }

    fun add(p: Programme) {
        insert.reset(); insert.clearBindings()
        insert.bind(source, p.channelId, p.start.epochSecond, p.stop.epochSecond, p.title, p.description)
        insert.step()
        count++
    }

    fun setEpgChannel(externalId: String, channelId: String) {
        setEpg.reset(); setEpg.clearBindings()
        setEpg.bind(channelId, source, externalId)
        setEpg.step()
    }

    private var committed = false

    fun commit() {
        insert.reset(); setEpg.reset()
        // Re-index the whole guide in one pass so live events can be searched across it.
        db.execSQL("INSERT INTO programmes_fts (programmes_fts) VALUES ('rebuild')")
        db.execSQL("COMMIT")
        committed = true
    }

    override fun close() {
        insert.close(); setEpg.close()
        if (!committed) runCatching { db.execSQL("ROLLBACK") }
        db.close()
    }
}

/** Binds positional values (null, text, numbers, booleans). */
internal fun SQLiteStatement.bind(vararg values: Any?) {
    values.forEachIndexed { i, v ->
        val index = i + 1
        when (v) {
            null -> bindNull(index)
            is String -> bindText(index, v)
            is Int -> bindLong(index, v.toLong())
            is Long -> bindLong(index, v)
            is Boolean -> bindLong(index, if (v) 1 else 0)
            is Double -> bindDouble(index, v)
            else -> bindText(index, v.toString())
        }
    }
}

internal fun SQLiteStatement.textOrNull(column: Int): String? = if (isNull(column)) null else getText(column)
