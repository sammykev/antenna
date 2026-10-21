package io.github.sammykev.antenna.data

data class M3uPlaylist(val items: List<MediaItem>, val epgUrl: String?)

/**
 * Parses extended M3U (#EXTM3U / #EXTINF) playlists as served by IPTV providers (M3uParser.cs). Tolerant of quoted
 * or bare attributes, #EXTGRP and #EXTVLCOPT lines, CRLF endings and a missing header.
 */
object M3uParser {
    private val attribute = Regex("""([\w-]+)=(?:"([^"]*)"|([^\s,]+))""")
    private val catchupKeys = listOf("catchup", "catchup-type", "catchup-source", "catchup-days")

    fun parse(content: String, sourceId: String): M3uPlaylist {
        val items = mutableListOf<MediaItem>()
        var epgUrl: String? = null
        // Catch-up settings on the #EXTM3U line apply to every channel that doesn't override them.
        val defaults = sortedMapOf<String, String>(String.CASE_INSENSITIVE_ORDER)
        var attrs: MutableMap<String, String>? = null
        var title: String? = null
        var extGroup: String? = null
        var options = mutableListOf<String>()

        for (rawLine in content.split('\n')) {
            val line = rawLine.trim().trimStart('﻿')
            if (line.isEmpty()) continue
            when {
                line.startsWith("#EXTM3U", ignoreCase = true) -> {
                    val header = parseAttributes(line)
                    for (key in catchupKeys) header[key]?.let { defaults[key] = it }
                    // Some providers list several guides separated by commas; keep the first.
                    epgUrl = (header["url-tvg"] ?: header["x-tvg-url"])?.split(',')?.map { it.trim() }?.firstOrNull { it.isNotEmpty() }
                }
                line.startsWith("#EXTINF", ignoreCase = true) -> {
                    attrs = parseAttributes(line)
                    title = extractTitle(line)
                    extGroup = null
                    options = mutableListOf()
                }
                line.startsWith("#EXTGRP:", ignoreCase = true) -> extGroup = line.substring("#EXTGRP:".length).trim()
                line.startsWith("#EXTVLCOPT:", ignoreCase = true) -> {
                    // Per-stream player options, typically http-user-agent / http-referrer the server insists on.
                    val opt = line.substring("#EXTVLCOPT:".length).trim()
                    if (opt.isNotEmpty()) options += ":" + opt.trimStart(':')
                }
                line.startsWith("#") -> Unit
                else -> {
                    // A bare URL without #EXTINF is still playable.
                    val itemAttrs = attrs ?: sortedMapOf(String.CASE_INSENSITIVE_ORDER)
                    for ((k, v) in defaults) itemAttrs.putIfAbsent(k, v)
                    items += buildItem(sourceId, line, itemAttrs, title, extGroup, options, items.size)
                    attrs = null
                    title = null
                    extGroup = null
                    options = mutableListOf()
                }
            }
        }
        return M3uPlaylist(items, epgUrl)
    }

    /** Splits Kodi-style "url|User-Agent=x&Referer=y" into the URL and libVLC options; adds header attributes from #EXTINF. */
    internal fun extractOptions(rawUrl: String, attrs: Map<String, String>, vlcOptions: List<String>): Pair<String, List<String>> {
        val options = vlcOptions.toMutableList()
        var url = rawUrl
        val pipe = rawUrl.indexOf('|')
        if (pipe > 0) {
            url = rawUrl.substring(0, pipe).trim()
            for (pair in rawUrl.substring(pipe + 1).split('&').filter { it.isNotEmpty() }) {
                val eq = pair.indexOf('=')
                if (eq <= 0) continue
                val option = headerOption(pair.substring(0, eq).trim()) ?: continue
                options += ":$option=${unescapeData(pair.substring(eq + 1))}"
            }
        }
        for ((key, value) in attrs) {
            val option = headerOption(key) ?: continue
            if (value.isNotEmpty() && options.none { it.startsWith(":$option=", ignoreCase = true) }) options += ":$option=$value"
        }
        return url to options
    }

    private fun headerOption(name: String) = when (name.lowercase()) {
        "user-agent", "http-user-agent" -> "http-user-agent"
        "referer", "referrer", "http-referrer", "http-referer" -> "http-referrer"
        else -> null
    }

    private fun buildItem(sourceId: String, rawUrl: String, attrs: Map<String, String>, title: String?, extGroup: String?,
                          vlcOptions: List<String>, index: Int): MediaItem {
        val (url, options) = extractOptions(rawUrl, attrs, vlcOptions)
        val catchupDays = firstNonEmpty(attrs["catchup-days"], attrs["tvg-rec"])?.toIntOrNull() ?: 0
        return MediaItem(
            sourceId = sourceId,
            externalId = url,
            kind = classifyKind(url),
            title = firstNonEmpty(title, attrs["tvg-name"]) ?: url,
            group = firstNonEmpty(attrs["group-title"], extGroup) ?: "",
            logoUrl = firstNonEmpty(attrs["tvg-logo"]),
            streamUrl = url,
            epgChannelId = firstNonEmpty(attrs["tvg-id"]),
            catchupDays = catchupDays.coerceAtLeast(0),
            catchupMode = firstNonEmpty(attrs["catchup"], attrs["catchup-type"])?.lowercase(),
            catchupSource = firstNonEmpty(attrs["catchup-source"]),
            playerOptions = if (options.isNotEmpty()) options.joinToString("\n") else null,
            sortIndex = index,
        )
    }

    /** Xtream-backed M3U exports put VOD under /movie/ and /series/; everything else is live. */
    internal fun classifyKind(url: String) =
        if (url.contains("/movie/", ignoreCase = true) || url.contains("/series/", ignoreCase = true)) MediaKind.Movie else MediaKind.Live

    private fun parseAttributes(line: String): MutableMap<String, String> {
        // Only the part before the title separator, so commas in titles don't confuse us.
        val comma = findTitleComma(line)
        val attrPart = if (comma >= 0) line.substring(0, comma) else line
        val result = sortedMapOf<String, String>(String.CASE_INSENSITIVE_ORDER)
        for (m in attribute.findAll(attrPart)) {
            val value = m.groups[2]?.value ?: m.groups[3]?.value ?: ""
            result[m.groupValues[1]] = value.trim()
        }
        return result
    }

    private fun extractTitle(line: String): String? {
        val comma = findTitleComma(line)
        return if (comma >= 0) firstNonEmpty(line.substring(comma + 1)) else null
    }

    /** The comma between attributes and title, skipping commas inside quotes. */
    private fun findTitleComma(line: String): Int {
        var inQuotes = false
        for (i in line.indices) {
            if (line[i] == '"') inQuotes = !inQuotes
            else if (line[i] == ',' && !inQuotes) return i
        }
        return -1
    }

    private fun firstNonEmpty(vararg values: String?) = values.map { it?.trim() }.firstOrNull { !it.isNullOrEmpty() }
}
