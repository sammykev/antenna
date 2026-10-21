package io.github.sammykev.antenna

import io.github.sammykev.antenna.sports.Fixture
import io.github.sammykev.antenna.sports.FixtureState
import io.github.sammykev.antenna.sports.FollowedTeam
import io.github.sammykev.antenna.sports.ScoreAlerts
import io.github.sammykev.antenna.sports.ScoreEventKind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class ScoreAlertTests {
    private val arsenal = listOf(FollowedTeam("Arsenal", "soccer/eng.1"))

    private fun match(state: FixtureState, home: String? = null, away: String? = null, status: String = "", sport: String = "Football") =
        Fixture("soccer/eng.1/1", sport, "Premier League", Instant.now(), state, status, "Arsenal", "Chelsea", homeScore = home, awayScore = away, name = "A v C")

    private fun between(a: Fixture, b: Fixture, teams: List<FollowedTeam> = arsenal) = ScoreAlerts.changes(mapOf(a.id to a), listOf(b), teams)

    @Test
    fun kickOffGoalFullTime() {
        assertEquals(ScoreEventKind.KickOff, between(match(FixtureState.Scheduled), match(FixtureState.Live, "0", "0", "1'")).single().kind)
        val goal = between(match(FixtureState.Live, "0", "0"), match(FixtureState.Live, "0", "1", "23'")).single()
        assertEquals("⚽ GOAL! Chelsea", goal.title)
        assertEquals("Arsenal 0–1 Chelsea (23')", goal.text)
        assertEquals(ScoreEventKind.FullTime, between(match(FixtureState.Live, "2", "1"), match(FixtureState.Finished, "2", "1", "FT")).single().kind)
        assertEquals("soccer/eng.1", match(FixtureState.Live).feedPath)
    }

    @Test
    fun nothingForOthers() {
        assertTrue(between(match(FixtureState.Live, "0", "0"), match(FixtureState.Live, "1", "0"), listOf(FollowedTeam("Spurs", "x"))).isEmpty())
        assertTrue(between(match(FixtureState.Live, "80", "78", sport = "Basketball"), match(FixtureState.Live, "82", "78", sport = "Basketball")).isEmpty())
    }
}
