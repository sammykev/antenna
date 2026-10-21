package io.github.sammykev.antenna.data

/**
 * Turns how people type sports searches into a guide query (EventSearch.cs): "man utd vs man city live" becomes
 * Manchester AND United AND City (each also matching its short form), ignoring filler. Every word is a prefix.
 */
object EventSearch {
    private val stopWords = setOf(
        "vs", "v", "versus", "against", "live", "match", "matches", "game", "games", "the", "at", "and",
        "watch", "today", "tonight", "stream", "streaming", "fixture", "fixtures", "free", "hd",
    )

    private val synonyms = mapOf(
        "man" to listOf("manchester"), "utd" to listOf("united"), "mufc" to listOf("manchester", "united"),
        "mcfc" to listOf("manchester", "city"), "spurs" to listOf("tottenham"), "wolves" to listOf("wolverhampton"),
        "boro" to listOf("middlesbrough"), "forest" to listOf("nottingham"), "toon" to listOf("newcastle"), "gunners" to listOf("arsenal"),
        "reds" to listOf("liverpool"), "blues" to listOf("chelsea"), "barca" to listOf("barcelona"), "psg" to listOf("paris"),
        "bayern" to listOf("munchen", "munich"), "inter" to listOf("internazionale"), "juve" to listOf("juventus"), "atleti" to listOf("atletico"),
        "epl" to listOf("premier"), "prem" to listOf("premier"), "ucl" to listOf("champions"), "uel" to listOf("europa"), "f1" to listOf("formula"),
        "motogp" to listOf("moto"), "ufc" to listOf("mma"), "nba" to listOf("basketball"), "nfl" to listOf("american football"),
        "nhl" to listOf("hockey"), "mlb" to listOf("baseball"), "atp" to listOf("tennis"), "wta" to listOf("tennis"), "ipl" to listOf("cricket"),
    )

    /** An FTS5 expression, or null when nothing searchable is left. [keepAllWords] keeps "the", "match"… (programme titles). */
    fun buildExpression(query: String, keepAllWords: Boolean = false): String? {
        val alternatives = mutableListOf<MutableList<String>>()
        var current = mutableListOf<String>()
        for (raw in Regex("[\\p{L}\\p{N}]+").findAll(query).map { it.value }) {
            if (raw == "OR") {
                if (current.isNotEmpty()) alternatives += current
                current = mutableListOf()
                continue
            }
            val word = raw.lowercase()
            // Single letters match nearly everything and make searches slow; skip them.
            if ((!keepAllWords && word in stopWords) || word.length < 2) continue
            current += term(word)
        }
        if (current.isNotEmpty()) alternatives += current
        val groups = alternatives.filter { it.isNotEmpty() }.map { if (it.size == 1) it[0] else "(" + it.joinToString(" AND ") + ")" }
        return when (groups.size) {
            0 -> null
            1 -> groups[0]
            else -> groups.joinToString(" OR ")
        }
    }

    private fun term(word: String): String {
        val options = mutableListOf(quote(word))
        synonyms[word]?.forEach { form ->
            val parts = form.split(' ')
            options += if (parts.size == 1) quote(form) else "(" + parts.joinToString(" AND ") { quote(it) } + ")"
        }
        return if (options.size == 1) options[0] else "(" + options.joinToString(" OR ") + ")"
    }

    private fun quote(word: String) = "\"$word\"*"
}
