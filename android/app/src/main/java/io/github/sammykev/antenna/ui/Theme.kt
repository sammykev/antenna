package io.github.sammykev.antenna.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val Accent = Color(0xFF4F7CFF)
val Surface = Color(0xFF1B1F28)
val Background = Color(0xFF12151C)
val Muted = Color(0xFF9AA3B2)
val Live = Color(0xFFE5484D)
val Gold = Color(0xFFE8A33D)

/** Dark, whatever the phone's theme: it's a video player. */
@Composable
fun AntennaTheme(content: @Composable () -> Unit) = MaterialTheme(
    colorScheme = darkColorScheme(
        primary = Accent, onPrimary = Color.White, background = Background, surface = Background, surfaceVariant = Surface,
        surfaceContainer = Surface, surfaceContainerHigh = Surface, secondaryContainer = Accent.copy(alpha = 0.35f),
        onSurfaceVariant = Muted,
    ),
    content = content,
)
