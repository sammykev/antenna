package io.github.sammykev.antenna

import android.content.Context
import android.content.Intent
import io.github.sammykev.antenna.data.Episode
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.WatchRecord
import io.github.sammykev.antenna.player.PlayerActivity

/** Something the player can play: a channel, a movie or an episode. */
data class PlayItem(
    val title: String,
    val url: String,
    val kind: MediaKind,
    val sourceId: String,
    /** The history key: the item's id, or "ep:{id}" for episodes (as on Windows). */
    val historyId: String,
    val subtitle: String? = null,
    val logoUrl: String? = null,
    val playerOptions: String? = null,
    /** The library item (channels and movies), for favourites and the guide. Null for episodes. */
    val item: MediaItem? = null,
) {
    val isLive get() = kind == MediaKind.Live
    val options get() = playerOptions?.split('\n')?.filter { it.isNotBlank() } ?: emptyList()

    companion object {
        fun of(item: MediaItem) = PlayItem(item.title, item.streamUrl ?: "", item.kind, item.sourceId, item.externalId, item.group, item.logoUrl,
            item.playerOptions, item)

        fun of(series: MediaItem, episode: Episode) = PlayItem(
            "S%02dE%02d %s".format(episode.season, episode.number, episode.title), episode.streamUrl, MediaKind.Series, series.sourceId,
            "ep:${episode.id}", series.title, episode.imageUrl ?: series.logoUrl, series.playerOptions)
    }
}

/** What the player plays: the list it was picked from, so next/previous step through it. */
object Playback {
    var queue: List<PlayItem> = emptyList()
        private set
    var index = 0

    val current get() = queue.getOrNull(index)

    /**
     * Set when a match is being watched: the queue is its channels, and the player moves on to the next by itself when
     * one isn't broadcasting (fails or shows nothing).
     */
    var matchTitle: String? = null
        private set

    fun play(context: Context, queue: List<PlayItem>, index: Int, matchTitle: String? = null) {
        this.matchTitle = matchTitle
        this.queue = queue
        this.index = index.coerceIn(0, (queue.size - 1).coerceAtLeast(0))
        context.startActivity(Intent(context, PlayerActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP))
    }

    fun playItems(context: Context, items: List<MediaItem>, index: Int) = play(context, items.map(PlayItem::of), index)

    /** Watches a match on [channels], the chosen one first; the player checks each is broadcasting. */
    fun playMatch(context: Context, channels: List<MediaItem>, title: String) =
        play(context, channels.map { PlayItem.of(it).copy(subtitle = title) }, 0, title)

    /**
     * Plays a history entry: a film or episode from where it was left (the player resumes from history), a channel
     * with its category around it for next/previous. Reads the library: call off the main thread, then [play].
     */
    fun queueFor(record: WatchRecord): Pair<List<PlayItem>, Int> {
        if (record.kind == MediaKind.Live) {
            val entry = Services.library.getItem(record.sourceId, record.externalId, MediaKind.Live)
            if (entry != null) {
                val channels = Services.library.getItems(MediaKind.Live, entry.item.sourceId, entry.item.group).map { it.item }
                val index = channels.indexOfFirst { it.externalId == entry.item.externalId }
                if (index >= 0) return channels.map(PlayItem::of) to index
            }
        }
        val item = Services.library.getItem(record.sourceId, record.externalId, record.kind)?.item
        return listOf(PlayItem(record.title, record.streamUrl, record.kind, record.sourceId, record.externalId, null, record.logoUrl,
            record.playerOptions, item)) to 0
    }
}
