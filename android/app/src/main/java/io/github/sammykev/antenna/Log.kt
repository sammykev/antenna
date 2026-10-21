package io.github.sammykev.antenna

import java.io.File
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

/** A small rolling text log in the app's files (Playlists → Share log sends it), as in the earlier app. */
object Log {
    lateinit var file: File
        private set
    private val stamp = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss")

    fun init(filesDir: File) {
        file = File(filesDir, "logs/antenna.log")
    }

    fun info(message: String) = write("INFO", message)

    fun error(message: String, e: Throwable? = null) = write("ERROR", if (e == null) message else "$message: ${e.stackTraceToString()}")

    @Synchronized
    private fun write(level: String, message: String) {
        if (!::file.isInitialized) return
        try {
            file.parentFile?.mkdirs()
            if (file.length() > 1_000_000) file.renameTo(File(file.path + ".old"))
            file.appendText("${LocalDateTime.now().format(stamp)} $level $message\n")
        } catch (_: Exception) {
            // Logging must never take the app down.
        }
        android.util.Log.println(if (level == "ERROR") android.util.Log.ERROR else android.util.Log.INFO, "Antenna", message)
    }
}
