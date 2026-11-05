package io.github.sammykev.antenna.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
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
    // On a TV, moving over the box mustn't pop the keyboard up: it's a button until OK is pressed on it.
    var editing by remember { mutableStateOf(false) }
    var edited by remember { mutableStateOf(false) }
    val button = remember { FocusRequester() }
    if (LocalTv.current && !editing) {
        // Back on the box after typing, so the remote carries on from here.
        LaunchedEffect(Unit) { if (edited) runCatching { button.requestFocus() } }
        Row(modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp).clip(RoundedCornerShape(24.dp)).background(Surface)
            .focusRequester(button).focusRing(RoundedCornerShape(24.dp)).clickable { editing = true; edited = true }.padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Icon(Icons.Default.Search, null, tint = Muted)
            Text(value.ifEmpty { placeholder }, color = if (value.isEmpty()) Muted else Color.White, maxLines = 1)
        }
        return
    }
    val focus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    var focused by remember { mutableStateOf(false) }
    LaunchedEffect(editing) { if (editing) runCatching { focus.requestFocus() } }
    OutlinedTextField(
        value = value, onValueChange = onChange, singleLine = true,
        placeholder = { Text(placeholder) },
        leadingIcon = { Icon(Icons.Default.Search, null) },
        trailingIcon = { if (value.isNotEmpty()) IconButton(onClick = { onChange("") }) { Icon(Icons.Default.Clear, "Clear") } },
        shape = RoundedCornerShape(24.dp),
        colors = TextFieldDefaults.colors(unfocusedContainerColor = Surface, focusedContainerColor = Surface,
            unfocusedIndicatorColor = Color.Transparent),
        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
        keyboardActions = KeyboardActions(onSearch = { keyboard?.hide(); editing = false }),
        modifier = modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp).focusRequester(focus)
            .onFocusChanged { if (focused && !it.isFocused) editing = false; focused = it.isFocused }
            .leaveWithRemote(editing) { editing = false },
    )
}

/**
 * A form's text box. On a TV it's a button showing the label and what's typed until OK is pressed on it: Android
 * otherwise opens the keyboard as soon as the remote passes over a box, and the arrows can't get out of it.
 */
@Composable
fun FormField(value: String, onChange: (String) -> Unit, label: String, modifier: Modifier = Modifier,
              keyboardType: KeyboardType = KeyboardType.Text, password: Boolean = false) {
    var editing by remember { mutableStateOf(false) }
    var edited by remember { mutableStateOf(false) }
    val button = remember { FocusRequester() }
    val field = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    val shape = RoundedCornerShape(4.dp)
    if (LocalTv.current && !editing) {
        LaunchedEffect(Unit) { if (edited) runCatching { button.requestFocus() } }
        Column(modifier.fillMaxWidth().clip(shape).border(1.dp, Muted, shape).focusRequester(button).focusRing(shape)
            .clickable { editing = true; edited = true }.padding(horizontal = 16.dp, vertical = 10.dp)) {
            Text(label, color = Muted, style = MaterialTheme.typography.bodySmall)
            Text(if (value.isEmpty()) "Press OK to type" else if (password) "•".repeat(value.length) else value,
                color = if (value.isEmpty()) Muted else Color.White, maxLines = 1)
        }
        return
    }
    var focused by remember { mutableStateOf(false) }
    LaunchedEffect(editing) { if (editing) runCatching { field.requestFocus() } }
    OutlinedTextField(value, onChange, label = { Text(label) }, singleLine = true,
        visualTransformation = if (password) PasswordVisualTransformation() else VisualTransformation.None,
        keyboardOptions = KeyboardOptions(keyboardType = keyboardType, imeAction = ImeAction.Done),
        keyboardActions = KeyboardActions(onDone = { keyboard?.hide(); editing = false }),
        modifier = modifier.fillMaxWidth().focusRequester(field)
            .onFocusChanged { if (focused && !it.isFocused) editing = false; focused = it.isFocused }
            .leaveWithRemote(editing) { editing = false })
}

/** While typing on a TV: Back (once the keyboard is down) or up/down returns to the box, so the remote can move on. */
@Composable
private fun Modifier.leaveWithRemote(editing: Boolean, leave: () -> Unit): Modifier {
    val tv = LocalTv.current
    BackHandler(enabled = tv && editing, onBack = leave)
    return if (!tv) this else onPreviewKeyEvent {
        if (it.type == KeyEventType.KeyDown && (it.key == Key.DirectionDown || it.key == Key.DirectionUp)) { leave(); true } else false
    }
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
    // One continuous bar: no gap or stop dot (Material 3's defaults), as on Windows.
    androidx.compose.material3.LinearProgressIndicator(progress = { progress }, modifier = modifier.fillMaxWidth().height(3.dp), gapSize = 0.dp,
        drawStopIndicator = {})
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
