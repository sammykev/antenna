package io.github.sammykev.antenna.sports

import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.LibraryStore
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.Programme
import io.github.sammykev.antenna.data.TextMatch
import io.github.sammykev.antenna.data.array
import io.github.sammykev.antenna.data.obj
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.doubleOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.Callable
import java.util.concurrent.Executors

enum class FixtureState { Scheduled, Live, Finished }

/** One match (or race, fight night, tennis match) from a fixtures feed (Fixture.cs). */
data class Fixture(
    val id: String,
    val sport: String,
    val league: String,
    val start: Instant,
    val state: FixtureState,
    /** "FT", "65'", "Q3 4:12": the feed's short status. */
    val status: String = "",
    val home: String? = null,
    val away: String? = null,
    val homeLogo: String? = null,
    val awayLogo: String? = null,
    val homeScore: String? = null,
    val awayScore: String? = null,
    val name: String,
    val broadcasts: List<String> = emptyList(),
) {
    val title get() = if (home != null && away != null) "$home vs $away" else name

    /** The feed it came from ("soccer/eng.1"): ids are "{feed}/{event id}". */
    val feedPath get() = id.lastIndexOf('/').let { if (it > 0) id.substring(0, it) else "" }
}

data class FixtureFeed(val path: String, val sport: String, val league: String)

/**
 * Fixtures, live scores and results from ESPN's public scoreboard (EspnFixtures.cs). It isn't an officially documented
 * API, so everything is defensive: a feed that fails or changes shape is skipped.
 */
class EspnFixtures(private val http: OkHttpClient) {
    companion object {
        private const val BASE = "https://site.api.espn.com/apis/site/v2/sports/"
        private val json = Json { ignoreUnknownKeys = true }

        /** The feed dates that can hold games starting on this local day: the day and its neighbours. */
        fun feedDays(day: LocalDate) = listOf(day.minusDays(1), day, day.plusDays(1))

        val feeds = listOf(
            FixtureFeed("soccer/eng.1", "Football", "Premier League"),
            FixtureFeed("soccer/uefa.champions", "Football", "Champions League"),
            FixtureFeed("soccer/uefa.europa", "Football", "Europa League"),
            FixtureFeed("soccer/uefa.europa.conf", "Football", "Conference League"),
            FixtureFeed("soccer/eng.2", "Football", "Championship"),
            FixtureFeed("soccer/eng.fa", "Football", "FA Cup"),
            FixtureFeed("soccer/eng.league_cup", "Football", "League Cup"),
            FixtureFeed("soccer/esp.1", "Football", "La Liga"),
            FixtureFeed("soccer/ger.1", "Football", "Bundesliga"),
            FixtureFeed("soccer/ita.1", "Football", "Serie A"),
            FixtureFeed("soccer/fra.1", "Football", "Ligue 1"),
            FixtureFeed("soccer/sco.1", "Football", "Scottish Premiership"),
            FixtureFeed("soccer/usa.1", "Football", "MLS"),
            FixtureFeed("basketball/nba", "Basketball", "NBA"),
            FixtureFeed("football/nfl", "American football", "NFL"),
            FixtureFeed("hockey/nhl", "Ice hockey", "NHL"),
            FixtureFeed("baseball/mlb", "Baseball", "MLB"),
            FixtureFeed("tennis/atp", "Tennis", "ATP"),
            FixtureFeed("tennis/wta", "Tennis", "WTA"),
            FixtureFeed("racing/f1", "Motorsport", "Formula 1"),
            FixtureFeed("mma/ufc", "MMA", "UFC"),
        )

        fun parse(root: JsonElement, feed: FixtureFeed): List<Fixture> {
            val list = mutableListOf<Fixture>()
            for (e in root.array("events")) {
                val name = e.s("name") ?: ""
                // Tennis: an event is a tournament, its matches are in groupings.
                val groupings = e.array("groupings")
                if (groupings.isNotEmpty()) {
                    for (g in groupings) for (c in g.array("competitions")) fromCompetition(c, c, feed, "${feed.league} · $name", name)?.let(list::add)
                    continue
                }
                val first = e.array("competitions").firstOrNull() as? JsonObject ?: continue
                fromCompetition(e, first, feed, feed.league, name)?.let(list::add)
            }
            return list
        }

        private fun fromCompetition(timed: JsonElement, competition: JsonElement, feed: FixtureFeed, league: String, name: String): Fixture? {
            val id = timed.s("id") ?: return null
            val start = timed.s("date")?.let { parseDate(it) } ?: return null
            val (state, status) = status(timed.obj("status") ?: competition.obj("status"))
            var home: String? = null; var away: String? = null; var homeLogo: String? = null; var awayLogo: String? = null
            var homeScore: String? = null; var awayScore: String? = null
            val sides = competition.array("competitors")
            if (sides.size == 2) {
                // Team sports say which side is home; tennis just lists two players.
                var h = sides.firstOrNull { it.s("homeAway") == "home" }
                var a = sides.firstOrNull { it.s("homeAway") == "away" }
                if (h == null || a == null) { h = sides[0]; a = sides[1] }
                side(h).let { home = it.first; homeLogo = it.second }
                side(a).let { away = it.first; awayLogo = it.second }
                if (state != FixtureState.Scheduled) {
                    homeScore = score(h)
                    awayScore = score(a)
                }
            }
            // Undecided draw slots ("TBD vs TBD") and tennis entries without two players aren't matches yet.
            if (home.equals("TBD", true) && away.equals("TBD", true) || feed.sport == "Tennis" && (home == null || away == null)) return null
            val broadcasts = competition.array("broadcasts").flatMap { b -> b.array("names").mapNotNull { (it as? JsonPrimitive)?.content } }.distinct()
            return Fixture("${feed.path}/$id", feed.sport, league, start, state, status, home, away, homeLogo, awayLogo, homeScore, awayScore, name, broadcasts)
        }

        private fun parseDate(s: String): Instant? = runCatching { Instant.parse(s) }.getOrNull()
            ?: runCatching { Instant.parse(s.replace("Z", ":00Z")) }.getOrNull()

        private fun side(side: JsonElement): Pair<String?, String?> {
            side.obj("team")?.let { return (it.s("displayName") ?: it.s("name")) to it.s("logo") }
            side.obj("athlete")?.let { return it.s("displayName") to it.obj("flag").s("href") }
            return null to null
        }

        /** A side's score; for tennis (no overall score) its games in each set, "6 3 7". */
        private fun score(side: JsonElement): String? {
            when (val s = side.obj("score")) {
                is JsonPrimitive -> s.content.takeIf { it.isNotEmpty() }?.let { return it }
                is JsonObject -> s.s("displayValue")?.let { return it }
                else -> Unit
            }
            val games = side.array("linescores").mapNotNull { (it.obj("value") as? JsonPrimitive)?.doubleOrNull?.toLong()?.toString() }
            return if (games.isNotEmpty()) games.joinToString(" ") else null
        }

        private fun status(status: JsonElement?): Pair<FixtureState, String> {
            val type = status.obj("type") ?: return FixtureState.Scheduled to ""
            val state = when (type.s("state")) { "in" -> FixtureState.Live; "post" -> FixtureState.Finished; else -> FixtureState.Scheduled }
            return state to (type.s("shortDetail") ?: type.s("description") ?: "")
        }

        private fun JsonElement?.s(name: String): String? =
            ((this as? JsonObject)?.get(name) as? JsonPrimitive)?.takeIf { it.isString }?.content?.takeIf { it.isNotEmpty() }
    }

    /** Every feed's fixtures on one local day, in start order. Failed feeds are left out. */
    fun getDay(day: LocalDate, feeds: List<FixtureFeed> = EspnFixtures.feeds): List<Fixture> {
        val pool = Executors.newFixedThreadPool(8)
        try {
            // The feed files games under their US date: an NBA game at 8pm in New York is the next morning in Lagos or
            // London. Ask for the days either side too and keep what starts on this local day.
            val requests = feeds.flatMap { feed -> feedDays(day).map { feed to it.format(DateTimeFormatter.BASIC_ISO_DATE) } }
            val tasks = requests.map { (feed, date) ->
                pool.submit(Callable {
                    try {
                        http.newCall(Request.Builder().url("$BASE${feed.path}/scoreboard?dates=$date").build()).execute().use { r ->
                            if (!r.isSuccessful) emptyList() else parse(json.parseToJsonElement(r.body!!.string()), feed)
                        }
                    } catch (_: Exception) {
                        emptyList()
                    }
                })
            }
            val zone = ZoneId.systemDefault()
            return tasks.flatMap { it.get() }
                .filter { it.start.atZone(zone).toLocalDate() == day }
                .distinctBy { it.id }
                .sortedWith(compareBy({ it.start }, { it.league }))
        } finally {
            pool.shutdown()
        }
    }
}

/**
 * A fixture and where to watch it in your own playlists. [isCoverage]: the channels show this sport or tournament around
 * kick-off ("ATP Tennis", "NBA TV") but the guide doesn't name this exact match, so it may be another game.
 */
data class FixtureMatch(
    val fixture: Fixture,
    val channels: List<LibraryEntry>,
    val programme: Programme?,
    val isCoverage: Boolean = false,
) {
    val onYourChannels get() = channels.isNotEmpty()
}

/**
 * Finds a fixture on the user's channels (FixtureMatcher.cs), most certain first: guide programmes around kick-off that
 * name both sides, per-event channels named after the match, channels of the networks the feed lists, then coverage of
 * the tournament or sport on air at the time, or channels given over to it.
 */
object FixtureMatcher {
    private val generic = setOf(
        "fc", "afc", "cf", "sc", "ac", "as", "cd", "ud", "sv", "fk", "bk", "club", "de", "la", "the",
        "town", "city", "county", "united", "athletic", "rovers", "wanderers", "albion", "real", "sporting",
    )

    fun queries(f: Fixture): List<String> {
        val queries = mutableListOf<String>()
        if (f.home != null && f.away != null) {
            queries += "${f.home} ${f.away}"
            val h = distinctive(f.home)
            val a = distinctive(f.away)
            for (hw in h) for (aw in a) queries += "$hw $aw"
            // Some guides only name one side ("Arsenal: Live"); the time window keeps this from straying far.
            if (h.isNotEmpty() && f.sport == "Football") queries += "${h[0]} ${f.league.split(' ')[0]}"
        } else {
            val words = words(f.name).filter { it !in generic }
            if (words.isNotEmpty()) {
                queries += words.takeLast(3).joinToString(" ")
                if (words.size > 3) queries += words.take(2).joinToString(" ")
            }
        }
        return queries.distinct()
    }

    fun find(store: LibraryStore, f: Fixture): FixtureMatch {
        val from = f.start.minus(Duration.ofMinutes(20))
        val to = f.start.plus(Duration.ofHours(3))
        for (query in queries(f)) {
            // A programme must actually be on around kick-off (not one that ended an hour before).
            val onAir = store.searchEvents(query, from, to, 10)
                .filter { it.programme.stop.isAfter(f.start.plus(Duration.ofMinutes(10))) && it.programme.start.isBefore(f.start.plus(Duration.ofMinutes(30))) }
            if (onAir.isNotEmpty()) {
                val channels = onAir.flatMap { it.channels }.distinctBy { it.item.sourceId to it.item.externalId }.take(8)
                return FixtureMatch(f, channels, onAir[0].programme)
            }
        }
        // Per-event channels ("EPL 03: Nott'm Forest v Ipswich 20:00") aren't in the guide.
        if (f.home != null && f.away != null) {
            for (query in queries(f).drop(1)) {
                val channels = store.search(query, MediaKind.Live, 8)
                if (channels.isNotEmpty()) return FixtureMatch(f, channels, null)
            }
        }
        // The networks the feed lists ("ESPN", "TNT"), when the playlist has them.
        for (network in f.broadcasts.filter { it.length >= 3 }.take(3)) {
            val channels = store.search(network, MediaKind.Live, 4)
                .filter { TextMatch.fold(it.item.title).contains(TextMatch.fold(network)) }
            if (channels.isNotEmpty()) return FixtureMatch(f, channels, null)
        }
        // Coverage of the tournament or sport on air around the start ("ATP Tennis: European Open").
        for (query in coverageQueries(f)) {
            val onAir = store.searchEvents(query, from, to, 20)
                .filter { it.programme.stop.isAfter(f.start.plus(Duration.ofMinutes(10))) && it.programme.start.isBefore(f.start.plus(Duration.ofMinutes(30))) }
            if (onAir.isNotEmpty()) {
                val channels = onAir.flatMap { it.channels }.distinctBy { it.item.sourceId to it.item.externalId }.take(8)
                return FixtureMatch(f, channels, onAir[0].programme, isCoverage = true)
            }
        }
        // Channels given over to the sport ("Tennis Channel", "NBA TV", "F1 TV").
        for (name in channelNames(f)) {
            val channels = store.search(name, MediaKind.Live, 6)
            if (channels.isNotEmpty()) return FixtureMatch(f, channels, null, isCoverage = true)
        }
        return FixtureMatch(f, emptyList(), null)
    }

    /** Searches for coverage that may not name this match: the tournament (tennis, races, fight nights), then the sport. */
    fun coverageQueries(f: Fixture): List<String> {
        val queries = mutableListOf<String>()
        if (f.sport == "Tennis" || f.sport == "Motorsport" || f.sport == "MMA") {
            // "BNP Paribas Fortis European Open" → "european open"; sponsors come first, the name last.
            val words = words(f.name).filter { it !in generic && it.length >= 3 }
            if (words.size >= 2) queries += words.takeLast(2).joinToString(" ")
        }
        queries += when (f.feedPath) {
            "tennis/atp" -> listOf("atp tennis", "atp", "tennis")
            "tennis/wta" -> listOf("wta tennis", "wta", "tennis")
            "basketball/nba" -> listOf("nba")
            "football/nfl" -> listOf("nfl")
            "hockey/nhl" -> listOf("nhl")
            "baseball/mlb" -> listOf("mlb")
            "racing/f1" -> listOf("formula 1", "f1", "grand prix")
            "mma/ufc" -> listOf("ufc")
            // Football names both teams, found above; the league is the fallback.
            else -> if (f.sport == "Football") listOf(f.league) else emptyList()
        }
        return queries.distinct()
    }

    /** Names of channels dedicated to the fixture's sport or competition. */
    fun channelNames(f: Fixture): List<String> = when (f.feedPath) {
        "tennis/atp", "tennis/wta" -> listOf("tennis")
        "basketball/nba" -> listOf("nba")
        "football/nfl" -> listOf("nfl")
        "hockey/nhl" -> listOf("nhl")
        "baseball/mlb" -> listOf("mlb")
        "racing/f1" -> listOf("f1", "formula 1")
        "mma/ufc" -> listOf("ufc")
        else -> emptyList()
    }

    /** Up to two words that identify a team: "Nottingham Forest" → nottingham, forest. */
    internal fun distinctive(team: String): List<String> {
        var words = words(team).filter { it !in generic && it.length >= 3 }
        if (words.isEmpty()) words = words(team).filter { it.length >= 2 }
        return words.sortedByDescending { it.length }.take(2)
    }

    private fun words(s: String) = TextMatch.fold(s).split(' ').filter { it.isNotEmpty() && (!it.all(Char::isDigit) || it.length > 2) }
}

/** A team the user follows, and the feed its matches are in. */
data class FollowedTeam(val name: String, val feedPath: String)

enum class ScoreEventKind { KickOff, Score, FullTime }

data class ScoreEvent(val fixture: Fixture, val kind: ScoreEventKind, val title: String, val text: String)

/** Followed teams (kept in the library's settings) and what changed in their matches between updates (ScoreAlerts.cs). */
object ScoreAlerts {
    const val FOLLOW_SETTING = "sports.follow"

    /** Sports where each score is a moment worth an alert (not basketball's every basket, or tennis points). */
    private val scoreSports = setOf("Football", "Ice hockey", "American football", "Baseball")

    fun followed(store: LibraryStore): List<FollowedTeam> =
        (store.getSetting(FOLLOW_SETTING) ?: "").split('\n').mapNotNull { line ->
            val parts = line.split('|', limit = 2)
            if (parts.size == 2) FollowedTeam(parts[1], parts[0]) else null
        }

    fun isFollowed(store: LibraryStore, team: String) = followed(store).any { same(it.name, team) }

    /** Follows or unfollows a team; returns whether it's now followed. */
    fun toggle(store: LibraryStore, team: String, feedPath: String): Boolean {
        val teams = followed(store).toMutableList()
        val removed = teams.removeAll { same(it.name, team) }
        if (!removed) teams += FollowedTeam(team, feedPath)
        store.setSetting(FOLLOW_SETTING, if (teams.isEmpty()) null else teams.joinToString("\n") { "${it.feedPath}|${it.name}" })
        return !removed
    }

    fun involves(f: Fixture, teams: List<FollowedTeam>) = teams.any { same(it.name, f.home) || same(it.name, f.away) }

    /** Kick-offs, scores and full times in followed matches since the last update. A match first seen now is the baseline. */
    fun changes(before: Map<String, Fixture>, now: List<Fixture>, teams: List<FollowedTeam>): List<ScoreEvent> {
        val events = mutableListOf<ScoreEvent>()
        for (f in now) {
            val was = before[f.id] ?: continue
            if (!involves(f, teams)) continue
            val score = "${f.home} ${f.homeScore ?: "0"}–${f.awayScore ?: "0"} ${f.away}"
            if (was.state == FixtureState.Scheduled && f.state == FixtureState.Live)
                events += ScoreEvent(f, ScoreEventKind.KickOff, "Kick-off: ${f.title}", "${f.league} has started.")
            if (f.state != FixtureState.Scheduled && f.sport in scoreSports &&
                (number(was.homeScore) != number(f.homeScore) || number(was.awayScore) != number(f.awayScore))) {
                val scorer = when {
                    number(f.homeScore) > number(was.homeScore) -> f.home
                    number(f.awayScore) > number(was.awayScore) -> f.away
                    else -> null
                }
                val clock = if (f.state == FixtureState.Live && f.status.isNotEmpty()) " (${f.status})" else ""
                val title = when {
                    scorer == null -> "Score update: ${f.title}"
                    f.sport == "Football" -> "⚽ GOAL! $scorer"
                    else -> "$scorer score"
                }
                events += ScoreEvent(f, ScoreEventKind.Score, title, score + clock)
            }
            if (was.state != FixtureState.Finished && f.state == FixtureState.Finished)
                events += ScoreEvent(f, ScoreEventKind.FullTime, "Full time: ${f.title}", score)
        }
        return events
    }

    private fun number(score: String?) = score?.toIntOrNull() ?: 0

    private fun same(a: String, b: String?) = b != null && a.trim().equals(b.trim(), ignoreCase = true)
}
