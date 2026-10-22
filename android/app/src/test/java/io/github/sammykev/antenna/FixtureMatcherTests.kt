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
}
