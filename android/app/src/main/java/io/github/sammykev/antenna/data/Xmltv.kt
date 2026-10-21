package io.github.sammykev.antenna.data

import okhttp3.OkHttpClient
import okhttp3.Request
import org.xmlpull.v1.XmlPullParser
import org.xmlpull.v1.XmlPullParserFactory
import java.io.BufferedInputStream
import java.io.File
import java.io.InputStream
import java.text.Normalizer
import java.time.Duration
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.zip.GZIPInputStream

data class XmltvChannel(val id: String, val displayNames: List<String>)

/** Streaming XMLTV parser (XmltvParser.cs): channels first, then only the wanted programmes in the window. */
object XmltvParser {
    private val stamp = DateTimeFormatter.ofPattern("yyyyMMddHHmmss")

    fun parse(input: InputStream, channelsReady: (List<XmltvChannel>) -> (String) -> Boolean, onProgramme: (Programme) -> Unit,
              from: Instant, to: Instant) {
        val parser = XmlPullParserFactory.newInstance().newPullParser()
        parser.setFeature(XmlPullParser.FEATURE_PROCESS_NAMESPACES, false)
        parser.setInput(input, null)
        val channels = mutableListOf<XmltvChannel>()
        var accept: ((String) -> Boolean)? = null
        var event = parser.eventType
        while (event != XmlPullParser.END_DOCUMENT) {
            if (event == XmlPullParser.START_TAG) {
                when (parser.name) {
                    "channel" -> {
                        val id = parser.getAttributeValue(null, "id")?.trim()
                        val names = mutableListOf<String>()
                        readChildren(parser) { name -> if (name == "display-name") parser.nextText().trim().takeIf { it.isNotEmpty() }?.let(names::add) else skip(parser) }
                        if (!id.isNullOrEmpty()) channels += XmltvChannel(id, names)
                    }
                    "programme" -> {
                        val wanted = accept ?: channelsReady(channels).also { accept = it }
                        val channel = parser.getAttributeValue(null, "channel")?.trim()
                        val start = parseTime(parser.getAttributeValue(null, "start"))
                        val stop = parseTime(parser.getAttributeValue(null, "stop"))
                        if (channel == null || start == null || !wanted(channel) || !start.isBefore(to) || (stop ?: start).isBefore(from)) {
                            skip(parser)
                        } else {
                            var title: String? = null
                            var desc: String? = null
                            readChildren(parser) { name ->
                                when (name) {
                                    // Keep the first of each; extra ones are other languages.
                                    "title" -> parser.nextText().trim().let { if (title == null) title = it }
                                    "desc" -> parser.nextText().trim().let { if (desc == null) desc = if (it.length > 1000) it.take(1000) + "…" else it }
                                    else -> skip(parser)
                                }
                            }
                            // A missing stop time is legal; assume half an hour.
                            onProgramme(Programme(channel, start, stop ?: start.plus(Duration.ofMinutes(30)), title ?: "", desc))
                        }
                    }
                }
            }
            event = parser.next()
        }
        if (accept == null) channelsReady(channels)
    }

    /** Calls [handle] for each child element; [handle] must consume it (nextText or skip). */
    private fun readChildren(parser: XmlPullParser, handle: (String) -> Unit) {
        if (parser.isEmptyElementTag) {
            parser.next()
            return
        }
        val depth = parser.depth
        var event = parser.next()
        while (!(event == XmlPullParser.END_TAG && parser.depth == depth) && event != XmlPullParser.END_DOCUMENT) {
            if (event == XmlPullParser.START_TAG) handle(parser.name)
            event = parser.next()
        }
    }

    private fun skip(parser: XmlPullParser) {
        var depth = 1
        while (depth > 0) {
            when (parser.next()) {
                XmlPullParser.START_TAG -> depth++
                XmlPullParser.END_TAG -> depth--
                XmlPullParser.END_DOCUMENT -> return
            }
        }
    }

    /** "20240101120000 +0100" (seconds and offset optional). */
    fun parseTime(value: String?): Instant? {
        if (value.isNullOrBlank()) return null
        val parts = value.trim().split(' ', limit = 2).filter { it.isNotEmpty() }
        var digits = parts[0]
        if (digits.length < 8 || !digits.all { it in '0'..'9' }) return null
        digits = digits.padEnd(14, '0').take(14)
        val local = runCatching { LocalDateTime.parse(digits, stamp) }.getOrNull() ?: return null
        var offset = ZoneOffset.UTC
        if (parts.size == 2) {
            val z = parts[1].trim()
            if (z.length == 5 && (z[0] == '+' || z[0] == '-')) {
                val h = z.substring(1, 3).toIntOrNull()
                val m = z.substring(3, 5).toIntOrNull()
                if (h != null && m != null) offset = ZoneOffset.ofTotalSeconds((if (z[0] == '-') -1 else 1) * (h * 3600 + m * 60))
            }
        }
        return local.toInstant(offset)
    }

    /** Opens a guide (URL or file), spooling downloads to a temp file and unzipping .gz guides. */
    fun open(http: OkHttpClient, location: String, tempDir: File): InputStream {
        val raw: InputStream = if (location.startsWith("http://", true) || location.startsWith("https://", true)) {
            val temp = File.createTempFile("epg", ".tmp", tempDir).apply { deleteOnExit() }
            http.newCall(Request.Builder().url(location).build()).execute().use { response ->
                if (!response.isSuccessful) throw java.io.IOException("The guide server answered ${response.code}.")
                temp.outputStream().use { out -> response.body!!.byteStream().copyTo(out) }
            }
            object : java.io.FileInputStream(temp) {
                override fun close() {
                    super.close()
                    temp.delete()
                }
            }
        } else {
            File(location.removePrefix("file://")).inputStream()
        }
        val buffered = BufferedInputStream(raw, 1 shl 16)
        buffered.mark(2)
        val magic = byteArrayOf(buffered.read().toByte(), buffered.read().toByte())
        buffered.reset()
        return if (magic[0] == 0x1F.toByte() && magic[1] == 0x8B.toByte()) GZIPInputStream(buffered, 1 shl 16) else buffered
    }
}

/** Matches playlist channels to guide channels by id, then by normalised name (EpgMatcher.cs). */
object EpgMatcher {
    private val prefix = Regex("""^\s*(\[[^\]]*\]|\|[^|]*\||[A-Z]{2,4}\s*[:|\-])\s*""")
    private val quality = Regex("""\b(uhd|fhd|hd|sd|4k|hevc|h265|1080p?|720p?|50fps|backup|vip)\b""")

    fun match(liveItems: List<MediaItem>, channels: List<XmltvChannel>): Map<String, String> {
        val byId = sortedMapOf<String, String>(String.CASE_INSENSITIVE_ORDER)
        val byName = mutableMapOf<String, String>()
        for (c in channels) {
            byId.putIfAbsent(c.id, c.id)
            for (name in c.displayNames) byName.putIfAbsent(normalize(name), c.id)
            byName.putIfAbsent(normalize(c.id), c.id)
        }
        val result = mutableMapOf<String, String>()
        for (item in liveItems) {
            val exact = item.epgChannelId?.let { byId[it] }
            if (exact != null) result[item.externalId] = exact
            else normalize(item.title).takeIf { it.isNotEmpty() }?.let { byName[it] }?.let { result[item.externalId] = it }
        }
        return result
    }

    fun normalize(name: String): String {
        val s = quality.replace(prefix.replace(name, "").lowercase(), "")
        val sb = StringBuilder()
        for (ch in Normalizer.normalize(s, Normalizer.Form.NFD)) {
            if (Character.getType(ch) == Character.NON_SPACING_MARK.toInt()) continue
            if (ch.isLetterOrDigit()) sb.append(ch)
        }
        return sb.toString()
    }
}
