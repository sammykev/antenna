package io.github.sammykev.antenna.ui

import android.app.UiModeManager
import android.content.Context
import android.content.pm.PackageManager
import android.content.res.Configuration
import androidx.compose.foundation.border
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.unit.dp

/**
 * Android TV: the same app, driven by the remote. Screens read this to swap the bottom bar for a side menu, put the
 * Live TV preview beside the list, and keep the keyboard away until a search box is chosen.
 */
val LocalTv = staticCompositionLocalOf { false }

fun Context.isTv(): Boolean =
    packageManager.hasSystemFeature(PackageManager.FEATURE_LEANBACK) ||
        (getSystemService(Context.UI_MODE_SERVICE) as? UiModeManager)?.currentModeType == Configuration.UI_MODE_TYPE_TELEVISION

/** A white ring while this (or something inside it) has the remote's focus, so you can see where you are. */
fun Modifier.focusRing(shape: Shape = CardShape): Modifier = composed {
    var focused by remember { mutableStateOf(false) }
    onFocusChanged { focused = it.hasFocus }
        .then(if (focused) Modifier.border(3.dp, Color.White, shape) else Modifier)
}
