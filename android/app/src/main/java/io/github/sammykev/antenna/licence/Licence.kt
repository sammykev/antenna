package io.github.sammykev.antenna.licence

import android.content.Context
import io.github.sammykev.antenna.BuildConfig
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.KeystoreProtector
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.security.KeyFactory
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.time.Instant
import java.util.Base64
import java.util.UUID

/**
 * A licence signed by the Antenna site (site/lib/licence.ts): "base64url(JSON).base64url(ECDSA P-256 SHA-256 DER
 * signature)" over the first part. The Windows app checks the same format (Antenna.Core/Licensing/Licence.cs).
 */
@Serializable
data class LicencePayload(val v: Int, val sub: String, val email: String = "", val plan: String, val until: Long, val refresh: Long,
                          val device: String, val iat: Long) {
    val untilTime: Instant get() = Instant.ofEpochSecond(until)
}

enum class LicenceStatus { SignedOut, Active, Expired, Stale }

object LicenceCheck {
    private val json = Json { ignoreUnknownKeys = true }

    /** The payload if the signature is good, whatever the dates say; null for anything else. */
    fun read(token: String?, publicKeyBase64: String): LicencePayload? {
        if (token.isNullOrEmpty() || publicKeyBase64.isEmpty()) return null
        val parts = token.split('.')
        if (parts.size != 2) return null
        return try {
            val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(Base64.getDecoder().decode(publicKeyBase64)))
            val verifier = Signature.getInstance("SHA256withECDSA")
            verifier.initVerify(key)
            verifier.update(parts[0].toByteArray(Charsets.US_ASCII))
            if (!verifier.verify(Base64.getUrlDecoder().decode(parts[1]))) return null
            json.decodeFromString(LicencePayload.serializer(), String(Base64.getUrlDecoder().decode(parts[0]), Charsets.UTF_8))
                .takeIf { it.v == 1 && it.sub.isNotEmpty() && it.device.isNotEmpty() }
        } catch (_: Exception) {
            null
        }
    }

    /** What the licence allows on this device now. */
    fun evaluate(licence: LicencePayload?, deviceId: String, now: Instant): LicenceStatus = when {
        licence == null || licence.device != deviceId -> LicenceStatus.SignedOut
        now.epochSecond >= licence.until -> LicenceStatus.Expired
        // A clock set far back can't stretch a licence: it's no good before it was issued.
        now.epochSecond >= licence.refresh || now.epochSecond < licence.iat - 86_400 -> LicenceStatus.Stale
        else -> LicenceStatus.Active
    }
}

class LicenceServerException(val code: String, message: String) : Exception(message)

/** Talks to the Antenna site: device sign-in with a code, and licence refreshes. */
class LicenceClient(private val http: OkHttpClient, server: String) {
    data class SignInStart(val deviceCode: String, val userCode: String, val expiresIn: Int, val interval: Int)

    private val base = server.trimEnd('/')
    val pricingUrl get() = "$base/pricing"
    val accountUrl get() = "$base/account"
    fun linkUrl(userCode: String) = "$base/link?code=$userCode"
    val linkPage get() = "$base/link"

    fun start(deviceId: String, name: String): SignInStart {
        val r = post("/api/device/start", buildJsonObject { put("deviceId", deviceId); put("name", name); put("platform", "android") })
        return SignInStart(r.s("deviceCode"), r.s("userCode"), r["expiresIn"]?.jsonPrimitive?.content?.toIntOrNull() ?: 600,
            r["interval"]?.jsonPrimitive?.content?.toIntOrNull() ?: 5)
    }

    /** The licence once the user has entered the code, null while still waiting. */
    fun poll(deviceCode: String): String? = post("/api/device/poll", buildJsonObject { put("deviceCode", deviceCode) })["token"]?.jsonPrimitive?.content

    fun refresh(token: String): String = post("/api/licence/refresh", buildJsonObject { put("token", token) }).s("token")

    private fun post(path: String, body: JsonObject): JsonObject {
        val request = Request.Builder().url(base + path).post(body.toString().toRequestBody("application/json".toMediaType())).build()
        http.newCall(request).execute().use { r ->
            val doc = runCatching { Json.parseToJsonElement(r.body?.string() ?: "").jsonObject }.getOrNull()
                ?: throw LicenceServerException("bad_response", "Unexpected answer from the Antenna server (${r.code}).")
            doc["error"]?.jsonPrimitive?.content?.let { code ->
                throw LicenceServerException(code, when (code) {
                    "expired" -> "The sign-in code expired. Get a new one."
                    "signed_out" -> "This device was signed out of your account."
                    else -> "The Antenna server said: $code"
                })
            }
            return doc
        }
    }

    private fun JsonObject.s(name: String) = this[name]?.jsonPrimitive?.content
        ?: throw LicenceServerException("bad_response", "Unexpected answer from the Antenna server.")
}

/**
 * The account and paywall: a 7-day trial, then a monthly or yearly plan bought on the Antenna site. The app signs in
 * with a short code and keeps a licence the site signed, checked offline and refreshed when the app opens (and at least
 * every 14 days). Builds without the site's key (Android Studio, CI) run without accounts.
 */
object Licensing {
    private const val PREFS = "antenna.licence"
    val enabled get() = BuildConfig.LICENCE_SERVER.isNotEmpty() && BuildConfig.LICENCE_PUBLIC_KEY.isNotEmpty()
    val client by lazy { LicenceClient(Services.http, BuildConfig.LICENCE_SERVER.ifEmpty { "https://localhost" }) }

    /** Bumps whenever the licence changes, so screens re-check. */
    val version = MutableStateFlow(0)

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, 0)

    fun deviceId(context: Context): String = prefs(context).getString("device", null)
        ?: ("android-" + UUID.randomUUID().toString().replace("-", "")).also { prefs(context).edit().putString("device", it).apply() }

    private fun token(context: Context): String? =
        prefs(context).getString("token", null)?.let { runCatching { KeystoreProtector().unprotect(it) }.getOrNull() }

    private fun setToken(context: Context, token: String?) {
        prefs(context).edit().apply { if (token == null) remove("token") else putString("token", KeystoreProtector().protect(token)) }.apply()
        version.value++
    }

    fun licence(context: Context) = LicenceCheck.read(token(context), BuildConfig.LICENCE_PUBLIC_KEY)

    fun status(context: Context): LicenceStatus =
        if (!enabled) LicenceStatus.Active else LicenceCheck.evaluate(licence(context), deviceId(context), Instant.now())

    fun summary(context: Context): String {
        val l = licence(context) ?: return "Not signed in"
        val days = ((l.until - Instant.now().epochSecond + 86_399) / 86_400).coerceAtLeast(0)
        val date = java.time.format.DateTimeFormatter.ofPattern("d MMM yyyy").withZone(java.time.ZoneId.systemDefault()).format(l.untilTime)
        val plan = when {
            status(context) == LicenceStatus.Expired -> if (l.plan == "trial" || l.plan == "none") "Your free trial has ended" else "Your plan has ended"
            l.plan == "trial" -> "Free trial: $days day${if (days == 1L) "" else "s"} left"
            l.plan == "monthly" -> "Monthly plan until $date"
            l.plan == "yearly" -> "Yearly plan until $date"
            l.plan == "granted" -> "Complimentary access until $date"
            else -> "No active plan"
        }
        return if (l.email.isNotEmpty()) "$plan · ${l.email}" else plan
    }

    /** Swaps the licence for a fresh one: payments and changes made on the site arrive this way. */
    suspend fun refresh(context: Context) = withContext(Dispatchers.IO) {
        if (!enabled) return@withContext
        val current = token(context) ?: return@withContext
        try {
            setToken(context, client.refresh(current))
            Log.info("Licence: ${summary(context)}")
        } catch (e: LicenceServerException) {
            if (e.code == "signed_out" || e.code == "bad_licence") {
                Log.info("Licence: ${e.code}, signing out")
                setToken(context, null)
            } else Log.info("Licence: refresh failed: ${e.message}")
        } catch (e: Exception) {
            // Offline or the site is busy: the current licence carries on until its refresh date.
            Log.info("Licence: refresh failed: ${e.message}")
        }
    }

    suspend fun startSignIn(context: Context): LicenceClient.SignInStart = withContext(Dispatchers.IO) {
        client.start(deviceId(context), "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}")
    }

    /** Waits for the user to enter the code on the site; returns null when signed in, or what went wrong. */
    suspend fun waitForSignIn(context: Context, start: LicenceClient.SignInStart): String? = withContext(Dispatchers.IO) {
        val deadline = System.currentTimeMillis() + start.expiresIn * 1000L
        try {
            while (System.currentTimeMillis() < deadline) {
                delay(start.interval * 1000L)
                client.poll(start.deviceCode)?.let {
                    setToken(context, it)
                    Log.info("Licence: signed in, ${summary(context)}")
                    return@withContext null
                }
            }
            "The code expired. Get a new one."
        } catch (e: LicenceServerException) {
            e.message
        } catch (e: java.io.IOException) {
            "Couldn't reach the Antenna website: ${e.message}"
        }
    }

    fun signOut(context: Context) = setToken(context, null)
}
