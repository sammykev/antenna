package io.github.sammykev.antenna

import android.content.Context
import io.github.sammykev.antenna.data.KeystoreProtector
import io.github.sammykev.antenna.data.LibraryStore
import io.github.sammykev.antenna.data.Programme
import io.github.sammykev.antenna.data.Refresher
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import okhttp3.OkHttpClient
import java.io.File
import java.time.Instant
import java.util.concurrent.TimeUnit

/** App-wide singletons. */
object Services {
    lateinit var library: LibraryStore
        private set
    lateinit var refresher: Refresher
        private set
    lateinit var filesDir: File
        private set

    val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(30, TimeUnit.SECONDS)
        .readTimeout(2, TimeUnit.MINUTES)
        // Some panels reject requests without a player-like user agent.
        .addInterceptor { chain -> chain.proceed(chain.request().newBuilder().header("User-Agent", "Antenna/0.1 (Android) VLC/3.0").build()) }
        .build()

    private val changes = MutableStateFlow(0)

    /** Bumps whenever playlists, the guide, favourites or history change, so screens reload. */
    val libraryVersion: StateFlow<Int> = changes

    fun notifyChanged() {
        nowPlaying = null
        changes.value++
    }

    fun init(context: Context) {
        if (::library.isInitialized) return
        filesDir = context.filesDir
        // The same file the earlier phone app used, so everything carries over.
        library = LibraryStore(File(context.filesDir, "library.db").path, KeystoreProtector())
        refresher = Refresher(http, library, context.cacheDir).also { it.onSaved = ::notifyChanged }
    }

    private var nowPlaying: Map<Pair<String, String>, Programme>? = null
    private var nowPlayingAt = Instant.EPOCH

    /** What's on now on every channel, cached for a minute. */
    fun nowPlaying(): Map<Pair<String, String>, Programme> {
        val now = Instant.now()
        val cached = nowPlaying
        if (cached != null && now.epochSecond - nowPlayingAt.epochSecond < 60) return cached
        return library.getNowPlaying(now).also {
            nowPlaying = it
            nowPlayingAt = now
        }
    }
}
