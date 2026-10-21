package io.github.sammykev.antenna

import io.github.sammykev.antenna.data.ChannelMatch
import io.github.sammykev.antenna.data.EpgMatcher
import io.github.sammykev.antenna.data.EventSearch
import io.github.sammykev.antenna.data.M3uParser
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.XmltvChannel
import io.github.sammykev.antenna.data.XmltvParser
import io.github.sammykev.antenna.data.escapeData
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.Instant

class ParserTests {
    @Test
    fun m3uAttributesGroupsOptionsAndKinds() {
        val playlist = M3uParser.parse("""
            #EXTM3U url-tvg="http://guide/a.xml,http://guide/b.xml" catchup="shift" catchup-days="3"
            #EXTINF:-1 tvg-id="bbc1.uk" tvg-logo="http://l/bbc.png" group-title="UK, News",BBC One, HD
            #EXTVLCOPT:http-user-agent=Foo
            http://host/live/u/p/1.ts
            #EXTINF:-1,Film
            #EXTGRP:Movies
            http://host/movie/u/p/2.mp4|User-Agent=Bar%20Baz&Referer=http://r
        """.trimIndent(), "s")
        assertEquals("http://guide/a.xml", playlist.epgUrl)
        val (bbc, film) = playlist.items
        assertEquals("BBC One, HD", bbc.title)
        assertEquals("UK, News", bbc.group)
        assertEquals("bbc1.uk", bbc.epgChannelId)
        assertEquals(3, bbc.catchupDays)
        assertEquals("shift", bbc.catchupMode)
        assertEquals(listOf(":http-user-agent=Foo"), bbc.options)
        assertEquals(MediaKind.Live, bbc.kind)
        assertEquals("http://host/movie/u/p/2.mp4", film.streamUrl)
        assertEquals("Movies", film.group)
        assertEquals(MediaKind.Movie, film.kind)
        assertEquals(listOf(":http-user-agent=Bar Baz", ":http-referrer=http://r"), film.options)
    }

    @Test
    fun escapingMatchesDotNet() {
        assertEquals("a%20b%2Bc%40d~e", escapeData("a b+c@d~e"))
        assertEquals("p%C3%A4ss%2F1", escapeData("päss/1"))
    }

    @Test
    fun channelKeysAndGuideMatching() {
        assertEquals("sky sport tennis", ChannelMatch.key("UK: SKY SPORTS TENNIS ᴴᴰ ◉"))
        assertEquals("sky sport tennis", ChannelMatch.key("SKYGO: SKY SPORT TENNIS HD"))
        assertEquals("tennis", ChannelMatch.key("Tennis"))
        val items = listOf(MediaItem("s", "1", MediaKind.Live, "UK: BBC One HD"), MediaItem("s", "2", MediaKind.Live, "X", epgChannelId = "ITV.uk"))
        val map = EpgMatcher.match(items, listOf(XmltvChannel("bbc1", listOf("BBC One")), XmltvChannel("itv.uk", listOf("ITV"))))
        assertEquals(mapOf("1" to "bbc1", "2" to "itv.uk"), map)
    }

    @Test
    fun eventSearchExpressions() {
        assertEquals("""("man"* OR "manchester"*) AND ("utd"* OR "united"*)""".let { "($it)" }, EventSearch.buildExpression("man utd vs live"))
        assertEquals(""""match"* AND "of"* AND "the"* AND "day"*""".let { "($it)" }, EventSearch.buildExpression("Match of the Day", keepAllWords = true))
        assertNull(EventSearch.buildExpression("vs live"))
    }

    @Test
    fun xmltvTimesAndProgrammes() {
        assertEquals(Instant.parse("2024-01-01T11:00:00Z"), XmltvParser.parseTime("20240101120000 +0100"))
        assertEquals(Instant.parse("2024-01-01T12:00:00Z"), XmltvParser.parseTime("202401011200"))
        val xml = """<?xml version="1.0"?><!DOCTYPE tv SYSTEM "xmltv.dtd"><tv>
            <channel id="a"><display-name>A</display-name><icon src="x"/></channel>
            <programme channel="a" start="20240101120000 +0000" stop="20240101130000 +0000"><title lang="en">News</title><title>Nachrichten</title><desc>Today</desc></programme>
            <programme channel="b" start="20240101120000 +0000" stop="20240101130000 +0000"><title>Skipped</title></programme>
            <programme channel="a" start="20240101130000 +0000"><title>Later</title></programme>
            </tv>"""
        val seen = mutableListOf<String>()
        val programmes = mutableListOf<io.github.sammykev.antenna.data.Programme>()
        XmltvParser.parse(xml.byteInputStream(), { channels -> seen += channels.map { it.id }; { it == "a" } }, { programmes += it },
            Instant.parse("2023-12-31T00:00:00Z"), Instant.parse("2024-01-02T00:00:00Z"))
        assertEquals(listOf("a"), seen)
        assertEquals(listOf("News", "Later"), programmes.map { it.title })
        assertEquals("Today", programmes[0].description)
        assertEquals(Instant.parse("2024-01-01T13:30:00Z"), programmes[1].stop)
    }
}
