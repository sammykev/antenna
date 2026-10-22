package io.github.sammykev.antenna.ui

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.Programme
import io.github.sammykev.antenna.sports.Fixture
import io.github.sammykev.antenna.sports.FixtureMatch

/*
 * What each screen shows, kept while the screen is in the back stack (or a tab in the background). Composable state
 * alone is dropped when a screen is left, so Back reloaded lists and jumped to the top; with this, Back returns to the
 * same category, list and scroll position. The "loaded for" keys say what the lists were loaded for, so a screen only
 * reloads when something actually changed (another category, a search, a library update).
 */

class LiveTvState : ViewModel() {
    var chips by mutableStateOf(emptyList<GroupChip>())
    var chip by mutableStateOf<GroupChip?>(null)
    val rows = mutableStateListOf<Pair<LibraryEntry, Programme?>>()
    var exhausted = false
    var chipsFor = -1
    var loadedFor: Any? = null
}

class MediaGridState : ViewModel() {
    var chips by mutableStateOf(emptyList<GroupChip>())
    var chip by mutableStateOf<GroupChip?>(null)
    val rows = mutableStateListOf<Pair<LibraryEntry, Float>>()
    var exhausted = false
    var chipsFor = -1
    var loadedFor: Any? = null
}

class HomeState : ViewModel() {
    var data by mutableStateOf<Any?>(null)
    var loadedFor = -1
}

class MatchesState : ViewModel() {
    var day by mutableStateOf<java.time.LocalDate?>(null)
    var reload by androidx.compose.runtime.mutableIntStateOf(0)
    var fixtures by mutableStateOf(emptyList<Fixture>())
    val matches = mutableStateMapOf<String, FixtureMatch>()
    var status by mutableStateOf("")
    var loadedFor: Any? = null
}
