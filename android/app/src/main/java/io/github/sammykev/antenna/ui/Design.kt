package io.github.sammykev.antenna.ui

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.MediaKind
import io.github.sammykev.antenna.data.Programme
import io.github.sammykev.antenna.data.WatchRecord

val CardShape = RoundedCornerShape(12.dp)

/** A page's large title, with any actions on the right. */
@Composable
fun LargeHeader(title: String, subtitle: String? = null, actions: @Composable RowScope.() -> Unit = {}) {
    Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp, top = 12.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.headlineLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (subtitle != null) Text(subtitle, color = Muted, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        actions()
    }
}

/**
 * Clickable, and shrinks a little while pressed: the touch version of the Windows cards lifting under the pointer.
 * Stays still when the phone's animations are turned off (the animation then completes at once).
 */
fun Modifier.pressable(onClick: () -> Unit): Modifier = composed {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed) 0.96f else 1f, tween(120), label = "press")
    graphicsLayer { scaleX = scale; scaleY = scale }
        .clickable(interactionSource = interaction, indication = null, onClick = onClick)
}

/** A shelf: a heading (with an optional "See all") and a row that scrolls sideways. */
@Composable
fun Shelf(title: String, seeAll: (() -> Unit)? = null, content: LazyListScope.() -> Unit) {
    Column(Modifier.fillMaxWidth().padding(top = 18.dp)) {
        Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(title, style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            if (seeAll != null) TextButton(onClick = seeAll) { Text("See all") }
        }
        LazyRow(contentPadding = PaddingValues(horizontal = 20.dp, vertical = 10.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), content = content)
    }
}

/** Movie / series poster (2:3) with the title underneath. */
@Composable
fun PosterCard(entry: LibraryEntry, width: Dp?, progress: Float = 0f, onClick: () -> Unit) {
    val size = if (width != null) Modifier.width(width) else Modifier.fillMaxWidth()
    Column(size.pressable(onClick)) {
        Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(CardShape).background(Surface)) {
            Text(entry.item.title, color = Muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.align(Alignment.Center).padding(8.dp))
            if (!entry.item.logoUrl.isNullOrBlank())
                AsyncImage(model = entry.item.logoUrl, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            if (progress > 0) ThinProgress(progress, Modifier.align(Alignment.BottomCenter))
        }
        Text(entry.item.title, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.SemiBold,
            style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 6.dp, start = 2.dp))
        Text(entry.item.group.ifEmpty { entry.sourceName }, color = Muted, maxLines = 1, overflow = TextOverflow.Ellipsis,
            style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(start = 2.dp))
    }
}

/** Live channel card: the logo on a 16:9 panel, then the channel and what's on now. */
@Composable
fun ChannelCard(entry: LibraryEntry, now: Programme?, favourite: Boolean = false, onClick: () -> Unit) {
    Column(Modifier.width(200.dp).pressable(onClick)) {
        Box(Modifier.fillMaxWidth().aspectRatio(16f / 9f).clip(CardShape).background(Surface).border(1.dp, Stroke, CardShape)) {
            Text(initials(entry.item.title), color = Muted, style = MaterialTheme.typography.titleLarge, modifier = Modifier.align(Alignment.Center))
            if (!entry.item.logoUrl.isNullOrBlank())
                AsyncImage(model = entry.item.logoUrl, contentDescription = null, contentScale = ContentScale.Fit,
                    modifier = Modifier.fillMaxSize().padding(horizontal = 36.dp, vertical = 20.dp))
            if (favourite) Icon(Icons.Default.Star, "Favourite", tint = Gold, modifier = Modifier.align(Alignment.TopEnd).padding(8.dp).size(14.dp))
            if (now != null) ThinProgress(now.progress(), Modifier.align(Alignment.BottomCenter).padding(horizontal = 12.dp, vertical = 10.dp))
        }
        Text(entry.item.title, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.SemiBold,
            style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 6.dp, start = 2.dp))
        Text(now?.title ?: entry.sourceName, color = Muted, maxLines = 1, overflow = TextOverflow.Ellipsis,
            style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(start = 2.dp))
    }
}

/** Continue watching: a wide card with the artwork, a fade, the title on it and progress along the bottom. */
@Composable
fun WatchCard(record: WatchRecord, onClick: () -> Unit) {
    val live = record.kind == MediaKind.Live
    Box(Modifier.width(260.dp).aspectRatio(16f / 9f).pressable(onClick).clip(CardShape).background(Surface).border(1.dp, Stroke, CardShape)) {
        if (!record.logoUrl.isNullOrBlank())
            AsyncImage(model = record.logoUrl, contentDescription = null, contentScale = if (live) ContentScale.Fit else ContentScale.Crop,
                modifier = if (live) Modifier.fillMaxSize().padding(start = 40.dp, end = 40.dp, top = 18.dp, bottom = 48.dp) else Modifier.fillMaxSize())
        Box(Modifier.fillMaxSize().background(Brush.verticalGradient(0.35f to Color.Transparent, 1f to Color(0xE6000000))))
        Column(Modifier.align(Alignment.BottomStart).padding(12.dp)) {
            Text(record.title, color = Color.White, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(watchDetails(record), color = Color(0xB3FFFFFF), style = MaterialTheme.typography.bodySmall, maxLines = 1)
            if (!live && record.lengthMs > 0) ThinProgress(record.progress, Modifier.padding(top = 6.dp))
        }
    }
}

/** "Live" or "12:40 left". */
fun watchDetails(record: WatchRecord): String = when {
    record.kind == MediaKind.Live -> "Live channel"
    record.lengthMs > 0 -> "${clock(record.lengthMs - record.positionMs)} left"
    else -> ""
}

/** 1:02:03 or 4:05. */
fun clock(ms: Long): String {
    val s = (ms / 1000).coerceAtLeast(0)
    return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
}

/** A small "● LIVE" badge for artwork and video. */
@Composable
fun LiveBadge(modifier: Modifier = Modifier) {
    Text("● LIVE", color = Live, style = MaterialTheme.typography.labelSmall, fontWeight = FontWeight.Bold,
        modifier = modifier.clip(RoundedCornerShape(10.dp)).background(Color(0x99000000)).padding(horizontal = 8.dp, vertical = 3.dp))
}

/** Space so the last shelf clears the bottom bar comfortably. */
@Composable
fun BottomSpace() = Box(Modifier.height(24.dp))
