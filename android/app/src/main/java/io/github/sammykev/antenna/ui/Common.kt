package io.github.sammykev.antenna.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage

/** A search box that sits at the top of a list. */
@Composable
fun SearchField(value: String, onChange: (String) -> Unit, placeholder: String, modifier: Modifier = Modifier) {
    OutlinedTextField(
        value = value, onValueChange = onChange, singleLine = true,
        placeholder = { Text(placeholder) },
        leadingIcon = { Icon(Icons.Default.Search, null) },
        trailingIcon = { if (value.isNotEmpty()) IconButton(onClick = { onChange("") }) { Icon(Icons.Default.Clear, "Clear") } },
        shape = RoundedCornerShape(24.dp),
        colors = TextFieldDefaults.colors(unfocusedContainerColor = Surface, focusedContainerColor = Surface,
            unfocusedIndicatorColor = Color.Transparent),
        modifier = modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
    )
}

/** A row of category chips; the selected one is kept in view. */
@Composable
fun <T> ChipRow(chips: List<T>, selected: T?, label: (T) -> String, onSelect: (T) -> Unit) {
    val state = rememberLazyListState()
    LaunchedEffect(selected, chips) {
        val i = chips.indexOf(selected)
        if (i >= 0) state.animateScrollToItem(i)
    }
    LazyRow(state = state, contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)) {
        items(chips) { chip ->
            FilterChip(selected = chip == selected, onClick = { onSelect(chip) }, label = { Text(label(chip)) })
        }
    }
}

/** A logo or poster with initials (or the title) behind it while it loads or when there's none. */
@Composable
fun Artwork(url: String?, fallback: String, modifier: Modifier, crop: Boolean = false) {
    Box(modifier.clip(CardShape).background(Surface), contentAlignment = Alignment.Center) {
        Text(fallback, color = Muted, textAlign = TextAlign.Center, modifier = Modifier.padding(6.dp))
        if (!url.isNullOrBlank())
            AsyncImage(model = url, contentDescription = null, contentScale = if (crop) ContentScale.Crop else ContentScale.Fit,
                modifier = Modifier.fillMaxSize())
    }
}

@Composable
fun EmptyText(text: String) {
    Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) {
        Text(text, color = Muted, textAlign = TextAlign.Center)
    }
}

@Composable
fun ThinProgress(progress: Float, modifier: Modifier = Modifier) {
    androidx.compose.material3.LinearProgressIndicator(progress = { progress }, modifier = modifier.fillMaxWidth().height(3.dp), drawStopIndicator = {})
}

/** Calls [onEnd] when the list is scrolled near its end (to load the next page). */
@Composable
fun LoadMoreEffect(state: LazyListState, onEnd: () -> Unit) {
    val nearEnd by remember { derivedStateOf { state.layoutInfo.visibleItemsInfo.lastOrNull()?.index?.let { it >= state.layoutInfo.totalItemsCount - 20 } ?: false } }
    LaunchedEffect(nearEnd) { if (nearEnd) onEnd() }
}

@Composable
fun LoadMoreEffect(state: LazyGridState, onEnd: () -> Unit) {
    val nearEnd by remember { derivedStateOf { state.layoutInfo.visibleItemsInfo.lastOrNull()?.index?.let { it >= state.layoutInfo.totalItemsCount - 24 } ?: false } }
    LaunchedEffect(nearEnd) { if (nearEnd) onEnd() }
}

/** Two letters for a channel without a logo. */
fun initials(title: String) = title.filter { it.isLetterOrDigit() }.take(2)

/** Waits for typing to pause before searching. */
@Composable
fun debounced(value: String, millis: Long = 300): String {
    val state = remember { androidx.compose.runtime.mutableStateOf(value) }
    LaunchedEffect(value) {
        if (value.isEmpty()) state.value = value
        else {
            kotlinx.coroutines.delay(millis)
            state.value = value
        }
    }
    return state.value
}
