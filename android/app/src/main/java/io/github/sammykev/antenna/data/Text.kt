package io.github.sammykev.antenna.data

import java.text.Normalizer

/** Forgiving name matching, as in the Windows app's TextMatch: case, accents and word order don't matter. */
object TextMatch {
    /** Lower case, accents removed, punctuation turned into spaces. */
    fun fold(s: String): String {
        val sb = StringBuilder(s.length)
        for (ch in Normalizer.normalize(s, Normalizer.Form.NFKD)) {
            if (Character.getType(ch) == Character.NON_SPACING_MARK.toInt()) continue
            sb.append(if (ch.isLetterOrDigit()) ch.lowercaseChar() else ' ')
        }
        return sb.toString()
    }

    fun words(query: String): List<String> = fold(query).split(' ').filter { it.isNotBlank() }

    /** "Sport" matches "UK | SPORTS"; every typed word must appear. */
    fun containsAll(text: String, query: String): Boolean {
        val haystack = fold(text)
        return words(query).all { haystack.contains(it) }
    }
}

/**
 * Recognises the same channel across playlists despite provider naming (ChannelMatch.cs):
 * "SKYGO: SKY SPORT TENNIS HD" and "UK: SKY SPORTS TENNIS ᴴᴰ ◉" both become "sky sport tennis".
 */
object ChannelMatch {
    private val prefix = Regex("""^\s*(\[[^\]]*\]|\|[^|]*\||[A-Za-z0-9]{2,8}\s*[:|])\s*""")
    private val noise = setOf(
        "uhd", "fhd", "hd", "sd", "4k", "8k", "hevc", "h265", "h264", "1080p", "1080", "720p", "720", "2160p",
        "50fps", "60fps", "fps", "backup", "vip", "raw", "multi",
    )

    fun key(title: String): String {
        val s = Normalizer.normalize(prefix.replace(title, ""), Normalizer.Form.NFKD).lowercase()
        val words = mutableListOf<String>()
        val word = StringBuilder()
        for (ch in "$s ") {
            if (Character.getType(ch) == Character.NON_SPACING_MARK.toInt()) continue
            if (ch.isLetterOrDigit()) {
                word.append(ch)
                continue
            }
            if (word.isNotEmpty()) {
                var w = word.toString()
                word.clear()
                if (w in noise) continue
                // "sports" -> "sport", but not "tennis", "news", "bus" or "class".
                if (w.length > 4 && w.last() == 's' && w[w.length - 2] !in "siu") w = w.dropLast(1)
                words += w
            }
        }
        return words.joinToString(" ")
    }
}

/** RFC 3986 escaping, the same as .NET's Uri.EscapeDataString, so URLs match the Windows app's. */
fun escapeData(value: String): String {
    val sb = StringBuilder()
    for (b in value.toByteArray(Charsets.UTF_8)) {
        val c = b.toInt() and 0xFF
        if (c in 'A'.code..'Z'.code || c in 'a'.code..'z'.code || c in '0'.code..'9'.code || c.toChar() in "-_.~") sb.append(c.toChar())
        else sb.append('%').append("0123456789ABCDEF"[c shr 4]).append("0123456789ABCDEF"[c and 15])
    }
    return sb.toString()
}

/** Percent-decoding (Uri.UnescapeDataString): "+" stays a plus. */
fun unescapeData(value: String): String =
    try { java.net.URLDecoder.decode(value.replace("+", "%2B"), "UTF-8") } catch (_: IllegalArgumentException) { value }
