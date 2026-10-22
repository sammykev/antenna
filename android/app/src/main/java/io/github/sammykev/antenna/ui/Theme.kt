package io.github.sammykev.antenna.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

// The same look as the Windows app (Themes/Antenna.xaml): a near-black stage, raised cards, artwork first.
val Accent = Color(0xFF4F7CFF)
val Background = Color(0xFF0B0C10)
val Surface = Color(0xFF16181E)
val SurfaceHigh = Color(0xFF1E2129)
val Stroke = Color(0x14FFFFFF)
val Muted = Color(0xA6FFFFFF)
val Live = Color(0xFFFF453A)
val Gold = Color(0xFFFFC531)

private val base = Typography()

/** Dark, whatever the phone's theme: it's a video player. */
@Composable
fun AntennaTheme(content: @Composable () -> Unit) = MaterialTheme(
    colorScheme = darkColorScheme(
        primary = Accent, onPrimary = Color.White, background = Background, surface = Background, surfaceVariant = Surface,
        surfaceContainer = Surface, surfaceContainerHigh = SurfaceHigh, surfaceContainerLow = Background,
        secondaryContainer = Accent.copy(alpha = 0.3f), onSurfaceVariant = Muted, outlineVariant = Stroke,
    ),
    typography = base.copy(
        // Page titles, like a large title on Apple platforms.
        headlineLarge = TextStyle(fontSize = 32.sp, lineHeight = 38.sp, fontWeight = FontWeight.Bold),
        // Shelf headings ("Continue watching").
        titleLarge = TextStyle(fontSize = 20.sp, lineHeight = 26.sp, fontWeight = FontWeight.SemiBold),
    ),
    content = content,
)
