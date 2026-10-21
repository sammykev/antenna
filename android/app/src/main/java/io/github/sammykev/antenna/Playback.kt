package io.github.sammykev.antenna

import android.content.Context
import android.content.Intent
import io.github.sammykev.antenna.data.Episode
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.data.MediaKind
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

    fun play(context: Context, queue: List<PlayItem>, index: Int) {
        this.queue = queue
        this.index = index.coerceIn(0, (queue.size - 1).coerceAtLeast(0))
        context.startActivity(Intent(context, PlayerActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP))
    }

    fun playItems(context: Context, items: List<MediaItem>, index: Int) = play(context, items.map(PlayItem::of), index)
}
