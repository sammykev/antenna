package io.github.sammykev.antenna.ui

import android.Manifest
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.Playback
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.alerts.ScoreAlertScheduler
import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.StreamProbe
import io.github.sammykev.antenna.sports.EspnFixtures
import io.github.sammykev.antenna.sports.Fixture
import io.github.sammykev.antenna.sports.FixtureMatch
import io.github.sammykev.antenna.sports.FixtureMatcher
import io.github.sammykev.antenna.sports.FixtureState
import io.github.sammykev.antenna.sports.ScoreAlerts
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.format.DateTimeFormatter

private const val ALL_SPORTS = "All sports"

/** Matches found on your channels, kept across visits until the library changes (a refreshed guide). */
private val found = mutableMapOf<String, FixtureMatch>()
private var foundForVersion = -1

/**
 * Today's and the next days' fixtures with live scores (ESPN's public scoreboard), each matched to your own channels,
 * as on Windows. Filter by day, sport and "My channels"; tap a match to watch it or follow a team for goal alerts.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MatchesScreen() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val prefs = remember { context.getSharedPreferences("antenna", 0) }
    val feed = remember { EspnFixtures(Services.http) }
    val days = remember { (0..2).map { LocalDate.now().plusDays(it.toLong()) } }
    // Kept while the screen is in the back stack or a background tab, so Back returns to the same day and list.
    val state: MatchesState = viewModel()
    if (state.day == null) state.day = days[0]
    var day: java.time.LocalDate by remember { mutableStateOf(state.day!!) }
    var sport by remember { mutableStateOf(prefs.getString("matches.sport", ALL_SPORTS)!!) }
    var mine by remember { mutableStateOf(prefs.getBoolean("matches.mine", false)) }
    var fixtures by state::fixtures
    val matches = state.matches
    var status by state::status
    var busy by remember { mutableStateOf(false) }
    var reload by state::reload
    var followTick by remember { mutableIntStateOf(0) }
    var open by remember { mutableStateOf<Fixture?>(null) }
    var followedNote by remember { mutableStateOf<String?>(null) }
    val notificationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    val teams = remember(followTick) { ScoreAlerts.followed(Services.library) }
    val version by Services.libraryVersion.collectAsState()
    var checking by remember { mutableStateOf<String?>(null) }

    /** Plays the chosen channel if it's really broadcasting, otherwise the next of the match's channels that is. */
    fun watchChecked(match: FixtureMatch, chosen: LibraryEntry) {
        if (checking != null) return
        checking = chosen.item.title
        scope.launch {
            val order = listOf(chosen) + match.channels.filter { it !== chosen }
            val (onAir, dark) = withContext(Dispatchers.IO) { StreamProbe.firstOnAir(Services.http, order, Duration.ofSeconds(8)) }
            checking = null
            val toast = { text: String -> android.widget.Toast.makeText(context, text, android.widget.Toast.LENGTH_LONG).show() }
            if (onAir == null) {
                toast(if (dark.size == 1) "${chosen.item.title} isn't broadcasting right now. Try again closer to the start."
                      else "None of the ${dark.size} channels listing ${match.fixture.title} are broadcasting right now.")
                return@launch
            }
            open = null
            if (dark.isNotEmpty()) toast("${dark.joinToString { it.item.title }} wasn't broadcasting, so playing ${onAir.item.title}.")
            Playback.playItems(context, listOf(onAir.item), 0)
        }
    }

    LaunchedEffect(day, reload, version) {
        state.day = day
        val key = listOf(day, reload, version)
        if (state.loadedFor == key && fixtures.isNotEmpty()) return@LaunchedEffect
        state.loadedFor = key
        // A refreshed guide can find matches the old one couldn't.
        if (foundForVersion != version) {
            found.clear()
            foundForVersion = version
        }
        busy = true
        status = "Loading fixtures…"
        fixtures = withContext(Dispatchers.IO) { feed.getDay(day) }
        if (fixtures.isEmpty()) {
            status = ""
            busy = false
            return@LaunchedEffect
        }
        status = "Finding matches on your channels…"
        matches.clear()
        fixtures.forEach { f -> found[f.id]?.let { matches[f.id] = it } }
        for (f in fixtures.filter { it.id !in matches }.sortedBy { when (it.state) { FixtureState.Live -> 0; FixtureState.Scheduled -> 1; else -> 2 } }) {
            val m = withContext(Dispatchers.IO) {
                runCatching { FixtureMatcher.find(Services.library, f) }.onFailure { Log.error("matches: finding a fixture", it) }
                    .getOrDefault(FixtureMatch(f, emptyList(), null))
            }
            found[f.id] = m
            matches[f.id] = m
        }
        status = "${fixtures.size} fixtures · ${matches.values.count { it.onYourChannels }} on your channels · scores from ESPN"
        busy = false
    }

    // Scores refresh every minute (today only).
    LaunchedEffect(day) {
        while (true) {
            delay(60_000)
            if (day == LocalDate.now() && fixtures.any { it.state != FixtureState.Finished })
                fixtures = withContext(Dispatchers.IO) { feed.getDay(day) }.ifEmpty { fixtures }
        }
    }

    val shown = fixtures.filter { (sport == ALL_SPORTS || it.sport == sport) && (!mine || matches[it.id]?.onYourChannels == true) }
    val groups = buildList {
        val followed = shown.filter { ScoreAlerts.involves(it, teams) }
        if (followed.isNotEmpty()) add("★ Your teams" to followed)
        shown.filterNot { ScoreAlerts.involves(it, teams) }.groupBy { it.league }.forEach { (k, v) -> add(k to v) }
    }

    Column(Modifier.fillMaxSize()) {
        LargeHeader("Matches") {
            IconButton(onClick = {
                // A refresh also re-checks the channels: the guide may have changed.
                fixtures.forEach { found.remove(it.id) }
                reload++
            }) { Icon(Icons.Default.Refresh, "Refresh") }
        }
        ChipRow(days, day, { d -> when (d) { days[0] -> "Today"; days[1] -> "Tomorrow"; else -> d.format(DateTimeFormatter.ofPattern("EEE d MMM")) } }) { day = it }
        // Sports as chips, like the days above (and Live events on Windows).
        ChipRow(listOf(ALL_SPORTS) + EspnFixtures.feeds.map { it.sport }.distinct(), sport, { it }) {
            sport = it
            prefs.edit().putString("matches.sport", it).apply()
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            Spacer(Modifier.weight(1f))
            Text("Only my channels", Modifier.padding(end = 8.dp))
            Switch(mine, { mine = it; prefs.edit().putBoolean("matches.mine", it).apply() })
        }
        Text(status, color = Muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
        Box(Modifier.fillMaxSize()) {
            LazyColumn(Modifier.fillMaxSize()) {
                for ((league, list) in groups) {
                    item(key = "h:$league") { Text(league, fontWeight = FontWeight.Bold, style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(16.dp, 14.dp, 16.dp, 4.dp)) }
                    items(list, key = { it.id }) { f -> MatchCard(f, matches[f.id], ScoreAlerts.involves(f, teams)) { open = f } }
                }
            }
            if (groups.isEmpty() && !busy) EmptyText(when {
                fixtures.isEmpty() -> "Couldn't load fixtures for this day, or there are none. Check your connection and refresh."
                mine -> "None of these fixtures are on your channels (yet: the guide may not list them until closer to the day)."
                else -> "No fixtures for this sport on this day."
            })
            if (busy && fixtures.isEmpty()) CircularProgressIndicator(Modifier.align(Alignment.TopCenter).padding(24.dp))
        }
    }

    open?.let { f ->
        val match = matches[f.id]
        ModalBottomSheet(onDismissRequest = { open = null }) {
            Column(Modifier.padding(16.dp, 0.dp, 16.dp, 24.dp)) {
                Text(f.title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
                val now = Instant.now()
                val soon = f.state == FixtureState.Live || f.state == FixtureState.Scheduled && Duration.between(now, f.start) < Duration.ofMinutes(20)
                val whenText = when { f.state == FixtureState.Finished -> "Finished"; soon -> "On now"; else -> "Starts ${hhmm.format(f.start)}" }
                Text("${f.league} · $whenText", color = Muted)
                if (match != null && !match.onYourChannels)
                    Text("Not on your channels" + if (f.broadcasts.isNotEmpty()) " (on TV: ${f.broadcasts.joinToString()})." else ".", color = Muted)
                if (match?.isCoverage == true)
                    Text("Your guide doesn't name this match. These channels show ${f.sport.lowercase()} around then, so it may be another game.", color = Muted)
                checking?.let { name ->
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(vertical = 8.dp)) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Text("Checking $name is broadcasting…", color = Muted, modifier = Modifier.padding(start = 10.dp))
                    }
                }
                match?.channels?.take(6)?.forEach { c ->
                    TextButton(enabled = checking == null, onClick = { watchChecked(match, c) }) {
                        // Each channel with what its guide says it's showing, so it's clear they're all this match.
                        Column {
                            Text("▶ Watch on ${c.item.title}")
                            match.programmeOn(c)?.let { Text(it.title, color = Muted, style = MaterialTheme.typography.bodySmall) }
                        }
                    }
                }
                for (team in listOfNotNull(f.home, f.away)) {
                    val followed = ScoreAlerts.isFollowed(Services.library, team)
                    TextButton(onClick = {
                        val now2 = ScoreAlerts.toggle(Services.library, team, f.feedPath)
                        followTick++
                        open = null
                        if (now2 && Build.VERSION.SDK_INT >= 33) notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                        scope.launch(Dispatchers.IO) { ScoreAlertScheduler.schedule(context.applicationContext) }
                        if (now2) followedNote = team
                    }) { Text(if (followed) "★ Stop following $team" else "☆ Follow $team (goal alerts)") }
                }
            }
        }
    }

    followedNote?.let { team ->
        AlertDialog(onDismissRequest = { followedNote = null }, title = { Text("Following $team") },
            text = { Text("You'll get a notification at kick-off, for each goal and at full time in $team's matches, even with Antenna closed.") },
            confirmButton = { TextButton(onClick = { followedNote = null }) { Text("OK") } })
    }
}

@Composable
private fun MatchCard(f: Fixture, match: FixtureMatch?, followed: Boolean, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp).clip(RoundedCornerShape(10.dp)).background(Surface)
        .clickable(onClick = onClick).padding(12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Column(Modifier.width(60.dp)) {
            Text(when (f.state) {
                FixtureState.Live -> "● ${f.status}"
                FixtureState.Finished -> f.status.ifEmpty { "FT" }
                else -> hhmm.format(f.start)
            }, color = if (f.state == FixtureState.Live) Live else androidx.compose.ui.graphics.Color.White, fontWeight = FontWeight.Bold,
                style = MaterialTheme.typography.bodySmall)
            if (followed) Text("★", color = Gold)
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            if (f.home != null && f.away != null) {
                Side(f.home, f.homeLogo, f.homeScore)
                Side(f.away, f.awayLogo, f.awayScore)
            } else {
                Text(f.name, fontWeight = FontWeight.Bold)
            }
            Text(when {
                match == null -> "Looking on your channels…"
                !match.onYourChannels -> if (f.broadcasts.isNotEmpty()) "Not on your channels (TV: ${f.broadcasts.take(2).joinToString()})" else "Not on your channels"
                // Coverage of the sport or tournament may be another game: say so rather than promise this one.
                match.isCoverage -> "▶ ${f.sport} coverage on ${match.channels[0].item.title}" + if (match.channels.size > 1) " +${match.channels.size - 1}" else ""
                else -> "▶ ${match.channels[0].item.title}" + if (match.channels.size > 1) " +${match.channels.size - 1}" else ""
            }, color = if (match?.onYourChannels == true) Accent else Muted, style = MaterialTheme.typography.bodySmall, maxLines = 1,
                overflow = TextOverflow.Ellipsis)
        }
    }
}

@Composable
private fun Side(name: String, logo: String?, score: String?) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        AsyncImage(logo, null, Modifier.size(20.dp))
        Text(name, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(score ?: "", fontWeight = FontWeight.Bold)
    }
}
