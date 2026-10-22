package io.github.sammykev.antenna

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.LiveTv
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.SportsSoccer
import androidx.compose.material.icons.filled.VideoLibrary
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import io.github.sammykev.antenna.alerts.ScoreAlertScheduler
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.sync.MobileSync
import io.github.sammykev.antenna.ui.AddPlaylistScreen
import io.github.sammykev.antenna.ui.AntennaTheme
import io.github.sammykev.antenna.ui.DetailsScreen
import io.github.sammykev.antenna.ui.GuideSearchScreen
import io.github.sammykev.antenna.ui.HomeScreen
import io.github.sammykev.antenna.ui.LiveTvScreen
import io.github.sammykev.antenna.ui.MatchesScreen
import io.github.sammykev.antenna.ui.MediaGridScreen
import io.github.sammykev.antenna.ui.PlaylistsScreen
import io.github.sammykev.antenna.ui.SyncScreen
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private data class Tab(val route: String, val label: String, val icon: ImageVector)

// Five at most; playlists open from Home's header.
private val tabs = listOf(
    Tab("home", "Home", Icons.Default.Home),
    Tab("live", "Live TV", Icons.Default.LiveTv),
    Tab("movies", "Movies", Icons.Default.Movie),
    Tab("series", "Series", Icons.Default.VideoLibrary),
    Tab("matches", "Matches", Icons.Default.SportsSoccer),
)

class MainActivity : ComponentActivity() {
    companion object {
        const val FIXTURE_EXTRA = "antenna.fixture"
        const val WATCH_EXTRA = "antenna.watch"
    }

    private var openMatches by mutableStateOf(false)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { AntennaTheme { AppContent() } }
        handleIntent(intent)

        // Sync with the PC while Antenna is open: now, and every five minutes.
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                while (true) {
                    MobileSync.run(this@MainActivity)
                    delay(5 * 60_000L)
                }
            }
        }
        lifecycleScope.launch(Dispatchers.IO) { ScoreAlertScheduler.schedule(applicationContext) }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    /** A tapped score notification: Watch opens the channel, anything else the Matches page. */
    private fun handleIntent(intent: Intent?) {
        val watch = intent?.getStringExtra(WATCH_EXTRA)
        val fixture = intent?.getStringExtra(FIXTURE_EXTRA)
        if (watch == null && fixture == null) return
        intent.removeExtra(WATCH_EXTRA)
        intent.removeExtra(FIXTURE_EXTRA)
        val parts = watch?.split('|', limit = 2)
        if (parts?.size == 2) {
            lifecycleScope.launch {
                val entry = withContext(Dispatchers.IO) { Services.library.getItem(parts[0], parts[1], MediaKind.Live) }
                if (entry != null) Playback.playItems(this@MainActivity, listOf(entry.item), 0) else openMatches = true
            }
        } else {
            openMatches = true
        }
    }

    @Composable
    private fun AppContent() {
        val nav = rememberNavController()
        val entry by nav.currentBackStackEntryAsState()
        val route = entry?.destination?.route
        var update by remember { mutableStateOf<Update?>(null) }

        LaunchedEffect(Unit) { update = withContext(Dispatchers.IO) { Updates.check() } }
        LaunchedEffect(openMatches) {
            if (openMatches) {
                openMatches = false
                nav.switchTab("matches")
            }
        }

        Scaffold(bottomBar = {
            if (tabs.any { it.route == route }) NavigationBar {
                for (tab in tabs) NavigationBarItem(selected = route == tab.route, onClick = { nav.switchTab(tab.route) },
                    icon = { Icon(tab.icon, null) }, label = { Text(tab.label) })
            }
        }) { padding ->
            NavHost(nav, startDestination = "home", modifier = Modifier.padding(padding).fillMaxSize()) {
                composable("home") {
                    HomeScreen(openDetails = { nav.navigate(details(it.item.kind, it.item.sourceId, it.item.externalId)) },
                        openPlaylists = { nav.navigate("playlists") }, openGuideSearch = { nav.navigate("guide") },
                        openTab = { nav.switchTab(it) })
                }
                composable("live") { LiveTvScreen(openGuideSearch = { nav.navigate("guide") }) }
                composable("guide") { GuideSearchScreen(back = { nav.popBackStack() }) }
                composable("movies") { MediaGridScreen(MediaKind.Movie) { nav.navigate(details(it.item.kind, it.item.sourceId, it.item.externalId)) } }
                composable("series") { MediaGridScreen(MediaKind.Series) { nav.navigate(details(it.item.kind, it.item.sourceId, it.item.externalId)) } }
                composable("details/{kind}/{source}/{id}") { e ->
                    val args = e.arguments!!
                    DetailsScreen(args.getString("source")!!, Uri.decode(args.getString("id")!!), MediaKind.valueOf(args.getString("kind")!!),
                        back = { nav.popBackStack() })
                }
                composable("matches") { MatchesScreen() }
                composable("playlists") { PlaylistsScreen(add = { nav.navigate("add") }, openSync = { nav.navigate("sync") }) }
                composable("add") { AddPlaylistScreen(back = { nav.popBackStack() }, done = { nav.switchTab("home") }) }
                composable("sync") { SyncScreen(back = { nav.popBackStack() }) }
            }
        }

        update?.let { u ->
            AlertDialog(onDismissRequest = { update = null }, title = { Text("Update available") },
                text = { Text("Antenna ${u.version} is out. Download and install it now?") },
                confirmButton = { TextButton(onClick = { update = null; startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(u.url))) }) { Text("Download") } },
                dismissButton = { TextButton(onClick = { update = null }) { Text("Later") } })
        }
    }

    private fun details(kind: MediaKind, source: String, id: String) = "details/${kind.name}/${Uri.encode(source)}/${Uri.encode(id)}"

    private fun NavHostController.switchTab(route: String) = navigate(route) {
        popUpTo(graph.findStartDestination().id) { saveState = true }
        launchSingleTop = true
        restoreState = true
    }
}
