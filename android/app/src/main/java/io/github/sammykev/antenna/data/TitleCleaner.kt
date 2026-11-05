package io.github.sammykev.antenna.data

/**
 * Turns IPTV catalogue names into something a film database can match (TitleCleaner.cs):
 * "EN - The Matrix (1999) [4K]" → ("The Matrix", 1999); "|UK| Line of Duty S01 FHD" → ("Line of Duty", null).
 */
object TitleCleaner {
    private val prefix = Regex("""^\s*(\[[^\]]*\]|\|[^|]*\||[A-Za-z0-9]{2,6}\s*[:|]\s*|[A-Za-z]{2,3}\s+-\s+|(?:4k|uhd|fhd|hd|sd)\s*-\s*)""", RegexOption.IGNORE_CASE)
    private val year = Regex("""[(\[]\s*((?:19|20)\d{2})\s*[)\]]|\b((?:19|20)\d{2})\s*$""")
    private val noise = Regex("""\b(4k|uhd|fhd|hd|sd|hevc|h\.?26[45]|x26[45]|1080p|720p|2160p|multi|sub|dub|vostfr|s\d{1,2}(e\d{1,3})?)\b""", RegexOption.IGNORE_CASE)
    private val brackets = Regex("""[(\[][^)\]]*[)\]]""")
    private val spaces = Regex("""\s{2,}""")

    fun parse(name: String): Pair<String, Int?> {
        // "Dune.Part.Two.2024.1080p" style names: dots and underscores are word gaps.
        var s = name.replace('_', ' ').replace('.', ' ')
        // Prefixes can stack: "|EN| 4K - Movie".
        repeat(3) {
            val stripped = prefix.replaceFirst(s, "")
            if (stripped == s) return@repeat
            s = stripped
        }
        // A year in brackets wins ("Blade Runner 2049 (2017)"); otherwise a trailing year once quality tags are gone.
        var match = year.find(s)
        if (match == null || match.groups[1] == null) {
            s = noise.replace(s, " ").trimEnd()
            match = year.find(s)
        }
        var found: Int? = null
        if (match != null) {
            found = (match.groups[1] ?: match.groups[2])!!.value.toInt()
            s = s.removeRange(match.range)
        }
        s = brackets.replace(s, " ")
        s = noise.replace(s, " ")
        s = spaces.replace(s, " ").trim(' ', '-', ':', '|')
        return s to found
    }

    /** One key per film whatever the playlist calls it: "the matrix|1999". */
    fun key(name: String): String {
        val (title, y) = parse(name)
        return TextMatch.fold(title).split(' ').filter { it.isNotBlank() }.joinToString(" ") + "|" + (y ?: "")
    }
}
