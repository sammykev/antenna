package io.github.sammykev.antenna.sync

import io.github.sammykev.antenna.data.LibraryStore
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.PlaylistSource
import io.github.sammykev.antenna.data.SourceKind
import io.github.sammykev.antenna.data.WatchRecord
import kotlinx.serialization.KSerializer
import kotlinx.serialization.Serializable
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.Json
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.time.OffsetDateTime as Odt
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** Where the shared sync file lives: a document the user picked, or a file in tests. */
interface SyncTarget {
    /** The file's bytes, or null when it's empty or doesn't exist yet. */
    fun read(): ByteArray?
    fun write(data: ByteArray)
}

class SyncPassphraseException : Exception("The sync passphrase doesn't match the one the sync file was made with.")

class SyncFileException(message: String) : Exception(message)

/** What a sync changed on this device. */
data class SyncOutcome(val newSources: List<PlaylistSource>, val changes: Int, val wrote: Boolean)

/** Dates as ISO 8601 with offset, as .NET writes DateTimeOffset. */
object OffsetInstantSerializer : KSerializer<Instant> {
    override val descriptor = PrimitiveSerialDescriptor("Instant", PrimitiveKind.STRING)
    override fun serialize(encoder: Encoder, value: Instant) = encoder.encodeString(OffsetDateTime.ofInstant(value, ZoneOffset.UTC).toString())
    override fun deserialize(decoder: Decoder): Instant = Odt.parse(decoder.decodeString()).toInstant()
}

/**
 * Keeps playlists, favourites and resume points the same on every device through one encrypted file in the user's own
 * cloud storage: the same file and the same rules as the Windows app (SyncService.cs). Each sync is a three-way merge
 * of this device, the file, and both as they were after the last sync (kept as hashes), so additions, edits and
 * deletions carry over. Where both sides changed the same playlist, this device wins.
 */
object SyncService {
    const val BASE_SETTING = "sync.base"

    /** Marks a base written by this app: the earlier app hashed differently, so its base is set aside once. */
    private const val BASE_FORMAT_SETTING = "sync.base.kotlin"
    private const val HISTORY_KEPT = 500
    private val magic = "ANTSYNC1".toByteArray(Charsets.US_ASCII)
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = true; encodeDefaults = true }

    @Serializable
    data class SyncSource(val id: String, val name: String, val kind: SourceKind, val location: String, val username: String?,
                          val password: String?, val epgUrl: String?, val color: String, val icon: String)

    @Serializable
    data class SyncFavourite(val sourceId: String, val externalId: String, val kind: MediaKind)

    @Serializable
    data class SyncWatch(val sourceId: String, val externalId: String, val kind: MediaKind, val title: String, val streamUrl: String,
                         val logoUrl: String?, val playerOptions: String?, val positionMs: Long, val lengthMs: Long,
                         @Serializable(OffsetInstantSerializer::class) val watchedAt: Instant)

    @Serializable
    data class SyncData(
        val version: Int = 1,
        @Serializable(OffsetInstantSerializer::class) val updated: Instant = Instant.EPOCH,
        val device: String = "",
        val sources: List<SyncSource> = emptyList(),
        val favourites: List<SyncFavourite> = emptyList(),
        val history: List<SyncWatch> = emptyList(),
    )

    @Serializable
    data class SyncBase(val sources: Map<String, String> = emptyMap(), val favourites: List<String> = emptyList())

    fun sync(store: LibraryStore, target: SyncTarget, passphrase: String, device: String): SyncOutcome {
        val bytes = target.read()
        val remote = bytes?.let { json.decodeFromString(SyncData.serializer(), open(it, passphrase)) }
        if ((remote?.version ?: 1) > 1) throw SyncFileException("The sync file was made by a newer version of Antenna. Update this one first.")

        val (merged, outcome) = merge(store, remote, device)
        val changed = remote == null || merged.copy(updated = Instant.EPOCH, device = "") != remote.copy(updated = Instant.EPOCH, device = "")
        if (changed) target.write(seal(json.encodeToString(SyncData.serializer(), merged), passphrase))
        saveBase(store, merged)
        return outcome.copy(wrote = changed)
    }

    // ---- Merging ----

    internal fun merge(store: LibraryStore, remote: SyncData?, device: String): Pair<SyncData, SyncOutcome> {
        val last = loadBase(store)
        var changes = 0
        val added = mutableListOf<PlaylistSource>()

        // Playlists: three-way, by id.
        val local = store.getSources().associateBy { it.id }
        val theirs = (remote?.sources ?: emptyList()).associateBy { it.id }
        val sources = mutableListOf<SyncSource>()
        for (id in (local.keys + theirs.keys + last.sources.keys).toList()) {
            val l = local[id]
            val mine = l?.let(::toSync)
            val other = theirs[id]
            val hash = last.sources[id]
            if (hash != null && (mine == null || other == null)) {
                // Deleted on one side since the last sync: delete it everywhere.
                if (mine != null) {
                    store.deleteSource(id)
                    changes++
                }
                continue
            }
            when {
                mine != null && other != null -> {
                    val keepMine = hash == null || hash(mine) != hash
                    val pick = if (keepMine) mine else other
                    if (pick != mine) {
                        store.saveSource(fromSync(other, l.lastRefreshed))
                        changes++
                    }
                    sources += pick
                }
                mine != null -> sources += mine
                other != null -> {
                    val source = fromSync(other, null)
                    store.saveSource(source)
                    added += source
                    changes++
                    sources += other
                }
            }
        }
        val kept = sources.map { it.id }.toSet()

        // Favourites: three-way on the set of keys.
        val myFavs = store.getFavouriteKeys().map { SyncFavourite(it.first, it.second, it.third) }.toSet()
        val theirFavs = (remote?.favourites ?: emptyList()).toSet()
        val lastFavs = last.favourites.toSet()
        val favourites = mutableListOf<SyncFavourite>()
        for (f in (myFavs + theirFavs + lastFavs.mapNotNull(::parseKey)).toList()) {
            if (f.sourceId !in kept) continue
            val mine = f in myFavs
            val other = f in theirFavs
            val keep = if (key(f) in lastFavs) mine && other else mine || other
            if (keep != mine) {
                store.setFavourite(f.sourceId, f.externalId, f.kind, keep)
                changes++
            }
            if (keep) favourites += f
        }

        // History: the latest of each, so resume points follow you.
        val history = linkedMapOf<Pair<String, String>, SyncWatch>()
        for (w in store.getHistory(HISTORY_KEPT).map(::toSync)) history[w.sourceId to w.externalId] = w
        for (w in remote?.history ?: emptyList()) {
            if (w.sourceId !in kept) continue
            val mine = history[w.sourceId to w.externalId]
            if (mine != null && !mine.watchedAt.isBefore(w.watchedAt)) continue
            history[w.sourceId to w.externalId] = w
            store.recordWatch(fromSync(w))
            changes++
        }

        val merged = SyncData(
            updated = Instant.now(),
            device = device,
            sources = sources,
            favourites = favourites,
            history = history.values.filter { it.sourceId in kept }.sortedByDescending { it.watchedAt }.take(HISTORY_KEPT),
        )
        return merged to SyncOutcome(added, changes, wrote = false)
    }

    private fun loadBase(store: LibraryStore): SyncBase {
        // A base from the earlier app used different hashes: drop it, so this sync only adds (never deletes) once.
        if (store.getSetting(BASE_FORMAT_SETTING) == null) return SyncBase()
        val text = store.getSetting(BASE_SETTING) ?: return SyncBase()
        return runCatching { json.decodeFromString(SyncBase.serializer(), text) }.getOrDefault(SyncBase())
    }

    private fun saveBase(store: LibraryStore, merged: SyncData) {
        store.setSetting(BASE_SETTING, json.encodeToString(SyncBase.serializer(),
            SyncBase(merged.sources.associate { it.id to hash(it) }, merged.favourites.map(::key))))
        store.setSetting(BASE_FORMAT_SETTING, "1")
    }

    /** Forgets the last sync, e.g. when switching to another sync file: the next sync only adds. */
    fun resetBase(store: LibraryStore) = store.setSetting(BASE_SETTING, null)

    private fun hash(s: SyncSource) = MessageDigest.getInstance("SHA-256")
        .digest(json.encodeToString(SyncSource.serializer(), s).toByteArray()).joinToString("") { "%02X".format(it) }

    private fun key(f: SyncFavourite) = "${f.kind.ordinal}|${f.sourceId}|${f.externalId}"

    private fun parseKey(key: String): SyncFavourite? {
        val parts = key.split('|', limit = 3)
        val kind = parts.getOrNull(0)?.toIntOrNull() ?: return null
        return if (parts.size == 3 && kind in MediaKind.entries.indices) SyncFavourite(parts[1], parts[2], MediaKind.entries[kind]) else null
    }

    private fun toSync(s: PlaylistSource) = SyncSource(s.id, s.name, s.kind, s.location, s.username, s.password, s.epgUrl, s.color, s.icon)

    private fun fromSync(s: SyncSource, lastRefreshed: OffsetDateTime?) =
        PlaylistSource(s.id, s.name, s.kind, s.location, s.username, s.password, s.epgUrl, s.color, s.icon, lastRefreshed)

    private fun toSync(w: WatchRecord) =
        SyncWatch(w.sourceId, w.externalId, w.kind, w.title, w.streamUrl, w.logoUrl, w.playerOptions, w.positionMs, w.lengthMs, w.watchedAt)

    private fun fromSync(w: SyncWatch) =
        WatchRecord(w.sourceId, w.externalId, w.kind, w.title, w.streamUrl, w.logoUrl, w.playerOptions, w.positionMs, w.lengthMs, w.watchedAt)

    // ---- Encryption: magic, salt (16), nonce (12), tag (16), ciphertext; AES-256-GCM, PBKDF2-SHA256 200 000 rounds ----

    fun seal(text: String, passphrase: String): ByteArray {
        val random = SecureRandom()
        val salt = ByteArray(16).also(random::nextBytes)
        val nonce = ByteArray(12).also(random::nextBytes)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(deriveKey(passphrase, salt), "AES"), GCMParameterSpec(128, nonce))
        val sealed = cipher.doFinal(text.toByteArray(Charsets.UTF_8))
        // Java appends the tag; the file keeps it before the ciphertext.
        val body = sealed.copyOfRange(0, sealed.size - 16)
        val tag = sealed.copyOfRange(sealed.size - 16, sealed.size)
        return magic + salt + nonce + tag + body
    }

    fun open(data: ByteArray, passphrase: String): String {
        if (data.size < magic.size + 44 || !data.copyOfRange(0, magic.size).contentEquals(magic))
            throw SyncFileException("That file isn't an Antenna sync file.")
        var at = magic.size
        val salt = data.copyOfRange(at, at + 16); at += 16
        val nonce = data.copyOfRange(at, at + 12); at += 12
        val tag = data.copyOfRange(at, at + 16); at += 16
        val body = data.copyOfRange(at, data.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(deriveKey(passphrase, salt), "AES"), GCMParameterSpec(128, nonce))
        return try {
            String(cipher.doFinal(body + tag), Charsets.UTF_8)
        } catch (_: AEADBadTagException) {
            throw SyncPassphraseException()
        }
    }

    /** PBKDF2-HMAC-SHA256 over the passphrase's UTF-8 bytes, exactly as .NET's Rfc2898DeriveBytes.Pbkdf2 does. */
    internal fun deriveKey(passphrase: String, salt: ByteArray, iterations: Int = 200_000, length: Int = 32): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(passphrase.toByteArray(Charsets.UTF_8).let { if (it.isEmpty()) ByteArray(1) else it }, "HmacSHA256"))
        val out = ByteArray(length)
        var block = 1
        var offset = 0
        while (offset < length) {
            mac.update(salt)
            mac.update(byteArrayOf((block ushr 24).toByte(), (block ushr 16).toByte(), (block ushr 8).toByte(), block.toByte()))
            var u = mac.doFinal()
            val t = u.copyOf()
            repeat(iterations - 1) {
                u = mac.doFinal(u)
                for (i in t.indices) t[i] = (t[i].toInt() xor u[i].toInt()).toByte()
            }
            val n = minOf(t.size, length - offset)
            t.copyInto(out, offset, 0, n)
            offset += n
            block++
        }
        return out
    }
}
