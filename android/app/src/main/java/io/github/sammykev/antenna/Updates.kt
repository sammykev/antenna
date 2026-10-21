package io.github.sammykev.antenna

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.Request

/** A newer APK on the GitHub releases page: its version and download link. */
data class Update(val version: String, val url: String)

object Updates {
    /** The public releases repository first; the source repository had the releases before it existed. */
    private val latest = listOf(
        "https://api.github.com/repos/sammykev/antenna-releases/releases/latest",
        "https://api.github.com/repos/sammykev/antenna/releases/latest",
    )

    /** The latest release if it's newer than this app and has an APK, else null. */
    fun check(): Update? = latest.firstNotNullOfOrNull(::check)

    private fun check(url: String): Update? = try {
        Services.http.newCall(Request.Builder().url(url).header("Accept", "application/vnd.github+json").build()).execute().use { r ->
            if (!r.isSuccessful) return null
            val root = Json.parseToJsonElement(r.body!!.string()).jsonObject
            val tag = root["tag_name"]?.jsonPrimitive?.content?.trimStart('v') ?: return null
            val apk = root["assets"]?.jsonArray?.map { it as JsonObject }?.firstOrNull { it["name"]?.jsonPrimitive?.content?.endsWith(".apk") == true }
                ?.get("browser_download_url")?.jsonPrimitive?.content ?: return null
            if (newer(tag, BuildConfig.VERSION_NAME)) Update(tag, apk) else null
        }
    } catch (e: Exception) {
        Log.info("Update check failed: ${e.message}")
        null
    }

    internal fun newer(candidate: String, current: String): Boolean {
        val a = candidate.split('.').map { it.toIntOrNull() ?: 0 }
        val b = current.split('.').map { it.toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(a.size, b.size)) {
            val x = a.getOrElse(i) { 0 }
            val y = b.getOrElse(i) { 0 }
            if (x != y) return x > y
        }
        return false
    }
}
