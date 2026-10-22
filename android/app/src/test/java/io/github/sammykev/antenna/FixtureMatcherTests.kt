package io.github.sammykev.antenna

import io.github.sammykev.antenna.data.LibraryStore
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.PlaylistSource
import io.github.sammykev.antenna.data.Programme
import io.github.sammykev.antenna.data.SourceKind
import io.github.sammykev.antenna.sports.EspnFixtures
import io.github.sammykev.antenna.sports.Fixture
import io.github.sammykev.antenna.sports.FixtureMatcher
import io.github.sammykev.antenna.sports.FixtureState
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.time.Duration
import java.time.Instant
import java.time.LocalDate

/** Mirrors FixtureTests.cs: matches found by tournament, sport, network and dedicated channels. */
class FixtureMatcherTests {
    private val file = File.createTempFile("antenna-fx", ".db").also { it.delete() }
    private val store = LibraryStore(file.path)
    private val kickoff = Instant.parse("2026-10-20T19:00:00Z")

    @After
    fun cleanUp() {
        store.close()
        listOf("", "-wal", "-shm").forEach { File(file.path + it).delete() }
    }

    private fun seed(vararg channels: Triple<String, String, String?>) {
        store.saveSource(PlaylistSource("s", "Provider", SourceKind.M3u, "x"))
        store.replaceItems("s", channels.mapIndexed { i, (id, title, epg) ->
            MediaItem("s", id, MediaKind.Live, title, "Sports", streamUrl = "http://h/$id", epgChannelId = epg, sortIndex = i)
        })
    }

    @Test
    fun tennisFallsBackToTheTournamentThenTennisChannels() {
        seed(Triple("1", "Sky Sports Tennis", "sst"), Triple("2", "Tennis Channel", null), Triple("3", "BBC One", "bbc1"))
        store.beginGuideImport("s").use { w ->
            w.add(Programme("sst", kickoff.minus(Duration.ofHours(1)), kickoff.plus(Duration.ofHours(4)), "Live ATP Tennis: European Open"))
            w.commit()
        }
        val match = Fixture(
            id = "tennis/atp/m1", sport = "Tennis", league = "ATP · BNP Paribas Fortis European Open", start = kickoff,
            state = FixtureState.Scheduled, name = "BNP Paribas Fortis European Open", home = "Federico Cina", away = "Joel Schwaerzler",
        )

        val found = FixtureMatcher.find(store, match)
        assertTrue(found.isCoverage)
        assertEquals(listOf("Sky Sports Tennis"), found.channels.map { it.item.title })
        assertEquals("european open", FixtureMatcher.coverageQueries(match)[0])

        val later = FixtureMatcher.find(store, match.copy(start = kickoff.plus(Duration.ofDays(1))))
        assertTrue(later.isCoverage)
        assertTrue(later.channels.any { it.item.title == "Tennis Channel" })
    }

    @Test
    fun usSportsUseTheListedNetworkThenLeagueChannels() {
        seed(Triple("1", "US: ESPN HD", null), Triple("2", "NBA TV", null), Triple("3", "BBC One", "bbc1"))
        val game = Fixture(
            id = "basketball/nba/1", sport = "Basketball", league = "NBA", start = kickoff, state = FixtureState.Scheduled,
            name = "Denver Nuggets at Oklahoma City Thunder", home = "Oklahoma City Thunder", away = "Denver Nuggets", broadcasts = listOf("ESPN"),
        )

        val onNetwork = FixtureMatcher.find(store, game)
        assertFalse(onNetwork.isCoverage)
        assertEquals(listOf("US: ESPN HD"), onNetwork.channels.map { it.item.title })

        val leagueOnly = FixtureMatcher.find(store, game.copy(broadcasts = emptyList()))
        assertTrue(leagueOnly.isCoverage)
        assertEquals(listOf("NBA TV"), leagueOnly.channels.map { it.item.title })
    }

    @Test
    fun asksTheFeedForTheDaysEitherSide() {
        val day = LocalDate.of(2026, 10, 23)
        assertEquals(listOf(day.minusDays(1), day, day.plusDays(1)), EspnFixtures.feedDays(day))
    }

    @Test
    fun onlyListsChannelsShowingThisMatchNotAnotherAtTheSameTime() {
        seed(Triple("1", "Sky Sports Main Event", "ssme"), Triple("2", "Sky Sports Premier League", "sspl"), Triple("3", "TNT Sports 1", "tnt1"))
        store.beginGuideImport("s").use { w ->
            w.add(Programme("ssme", kickoff.minus(Duration.ofMinutes(30)), kickoff.plus(Duration.ofHours(2)), "Live: Man Utd v Liverpool"))
            w.add(Programme("sspl", kickoff.minus(Duration.ofMinutes(30)), kickoff.plus(Duration.ofHours(2)), "Live: Man City v Arsenal"))
            w.add(Programme("tnt1", kickoff.minus(Duration.ofMinutes(15)), kickoff.plus(Duration.ofHours(2)), "Manchester City v Arsenal: Build-up and Live"))
            w.commit()
        }
        val city = Fixture(id = "soccer/eng.1/1", sport = "Football", league = "Premier League", start = kickoff, state = FixtureState.Scheduled,
            name = "Arsenal at Manchester City", home = "Manchester City", away = "Arsenal")

        val match = FixtureMatcher.find(store, city)
        assertFalse(match.isCoverage)
        assertEquals(listOf("Sky Sports Premier League", "TNT Sports 1"), match.channels.map { it.item.title }.sorted())
        assertTrue(match.channels.all { match.programmeOn(it)!!.title.contains("Arsenal") })
    }

    @Test
    fun recognisesThisMatchInATitle() {
        val city = Fixture(id = "soccer/eng.1/1", sport = "Football", league = "Premier League", start = kickoff, state = FixtureState.Scheduled,
            name = "Arsenal at Manchester City", home = "Manchester City", away = "Arsenal")
        assertTrue(FixtureMatcher.isThisMatch(city, "Live: Man City v Arsenal"))
        assertTrue(FixtureMatcher.isThisMatch(city, "Arsenal: Live"))
        assertFalse(FixtureMatcher.isThisMatch(city, "Live: Man Utd v Liverpool"))
        assertFalse(FixtureMatcher.isThisMatch(city, "Arsenal v Tottenham"))
        assertTrue(FixtureMatcher.isThisMatch(city, "MCFC v Arsenal"))
    }

    private fun tennis(tour: String, home: String, away: String, state: FixtureState = FixtureState.Scheduled) = Fixture(
        id = "tennis/$tour/$home", sport = "Tennis", league = "${tour.uppercase()} · European Open", start = kickoff, state = state,
        name = "European Open", home = home, away = away)

    @Test
    fun tennisCoverageKeepsToItsTourAndFindsPlayersInDescriptions() {
        seed(Triple("1", "Sky Sports Top Event", "sste"), Triple("2", "Sky Sports Tennis", "sst"))
        store.beginGuideImport("s").use { w ->
            w.add(Programme("sste", kickoff.minus(Duration.ofMinutes(30)), kickoff.plus(Duration.ofHours(4)), "Live ATP Tennis: European Open",
                "Terence Atmane v Mattia Bellucci."))
            w.add(Programme("sst", kickoff.minus(Duration.ofMinutes(30)), kickoff.plus(Duration.ofHours(4)), "Live WTA Tennis: Japan Open"))
            w.commit()
        }
        val atp = FixtureMatcher.find(store, tennis("atp", "Terence Atmane", "Mattia Bellucci"))
        assertFalse(atp.isCoverage)
        assertEquals(listOf("Sky Sports Top Event"), atp.channels.map { it.item.title })

        val wta = FixtureMatcher.find(store, tennis("wta", "Alexandra Eala", "Diana Shnaider"))
        assertTrue(wta.isCoverage)
        assertEquals(listOf("Sky Sports Tennis"), wta.channels.map { it.item.title })
    }

    @Test
    fun aMatchBeingPlayedIsFoundOnAirNowNotAtItsListedStart() {
        seed(Triple("1", "Sky Sports Top Event", "sste"))
        store.beginGuideImport("s").use { w ->
            w.add(Programme("sste", kickoff.plus(Duration.ofHours(2)), kickoff.plus(Duration.ofHours(5)), "Live ATP Tennis: Atmane v Bellucci"))
            w.commit()
        }
        val match = tennis("atp", "Terence Atmane", "Mattia Bellucci")
        assertFalse(FixtureMatcher.find(store, match).onYourChannels)
        val live = FixtureMatcher.find(store, match.copy(state = FixtureState.Live), now = kickoff.plus(Duration.ofMinutes(130)))
        assertEquals(listOf("Sky Sports Top Event"), live.channels.map { it.item.title })
    }
}
