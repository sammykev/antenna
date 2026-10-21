package io.github.sammykev.antenna.data

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

// Tolerant access to loosely typed JSON (Xtream panels send numbers as strings and vice versa; ESPN changes shape).

fun JsonElement?.obj(name: String): JsonElement? = (this as? JsonObject)?.get(name)?.takeUnless { it is JsonNull }

fun JsonElement?.str(name: String): String? {
    val v = (this as? JsonObject)?.get(name) as? JsonPrimitive ?: return null
    if (v is JsonNull) return null
    return when (v.content) {
        "true" -> if (v.isString) v.content else "1"
        "false" -> if (v.isString) v.content else "0"
        else -> v.content
    }
}

fun JsonElement?.long(name: String): Long {
    val s = str(name) ?: return 0
    return s.toLongOrNull() ?: s.toDoubleOrNull()?.toLong() ?: 0
}

fun JsonElement?.int(name: String): Int = long(name).coerceIn(Int.MIN_VALUE.toLong(), Int.MAX_VALUE.toLong()).toInt()

fun JsonElement?.array(name: String): List<JsonElement> = ((this as? JsonObject)?.get(name) as? JsonArray) ?: emptyList()

fun String?.nullIfBlank() = if (this.isNullOrBlank()) null else this
