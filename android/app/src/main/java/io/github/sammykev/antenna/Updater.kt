package io.github.sammykev.antenna

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInfo
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import okhttp3.Request
import java.io.File
import java.io.IOException

/**
 * Updates Antenna from inside the app: downloads the new APK, checks it's signed with the same key as the installed
 * app, and hands it to Android's installer, which asks the user to confirm (Android always does for apps not from the
 * Play Store). Data is kept: it's an update of the same app, not a new install.
 */
object Updater {
    class UpdateException(message: String) : IOException(message)

    /** Whether Android lets Antenna install updates; if not, [allowInstallsIntent] opens the switch for it. */
    fun canInstall(context: Context) = context.packageManager.canRequestPackageInstalls()

    fun allowInstallsIntent(context: Context) =
        Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))

    /** Downloads the update into the cache, reporting progress (0..1). Blocking: call off the main thread. */
    fun download(context: Context, update: Update, progress: (Float) -> Unit): File {
        val dir = File(context.cacheDir, "updates").apply { mkdirs() }
        dir.listFiles()?.forEach { it.delete() }
        val file = File(dir, "Antenna-${update.version}.apk")
        Services.http.newCall(Request.Builder().url(update.url).build()).execute().use { response ->
            if (!response.isSuccessful) throw UpdateException("The download failed (${response.code}).")
            val body = response.body ?: throw UpdateException("The download was empty.")
            val total = body.contentLength()
            body.byteStream().use { input ->
                file.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var done = 0L
                    while (true) {
                        val n = input.read(buffer)
                        if (n < 0) break
                        output.write(buffer, 0, n)
                        done += n
                        if (total > 0) progress(done.toFloat() / total)
                    }
                }
            }
        }
        verify(context, file)
        return file
    }

    /** The download must be Antenna, signed by the same key as this copy; anything else is refused. */
    private fun verify(context: Context, apk: File) {
        val pm = context.packageManager
        val flags = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) PackageManager.GET_SIGNING_CERTIFICATES else @Suppress("DEPRECATION") PackageManager.GET_SIGNATURES
        val downloaded = pm.getPackageArchiveInfo(apk.path, flags) ?: throw UpdateException("The download isn't a valid app.")
        if (downloaded.packageName != context.packageName)
            throw UpdateException("This copy of Antenna (${context.packageName}) can't be updated from the release build.")
        val installed = pm.getPackageInfo(context.packageName, flags)
        if (signatures(downloaded) != signatures(installed))
            throw UpdateException("The download isn't signed by Antenna's key, so it wasn't installed.")
    }

    private fun signatures(info: PackageInfo): Set<String> =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            val signing = info.signingInfo ?: return emptySet()
            val certs = if (signing.hasMultipleSigners()) signing.apkContentsSigners else signing.signingCertificateHistory
            certs.orEmpty().map { it.toCharsString() }.toSet()
        } else {
            @Suppress("DEPRECATION")
            info.signatures.orEmpty().map { it.toCharsString() }.toSet()
        }

    /** Hands the APK to Android's installer; it shows "Do you want to update this app?" and restarts Antenna after. */
    fun install(context: Context, apk: File) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val id = installer.createSession(params)
        installer.openSession(id).use { session ->
            session.openWrite("antenna.apk", 0, apk.length()).use { out ->
                apk.inputStream().use { it.copyTo(out) }
                session.fsync(out)
            }
            val intent = Intent(context, InstallResultReceiver::class.java)
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) PendingIntent.FLAG_MUTABLE else 0)
            session.commit(PendingIntent.getBroadcast(context, id, intent, flags).intentSender)
        }
    }
}

/** Android's answer to an install: shows its confirmation screen, or logs the outcome. */
class InstallResultReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
                    intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java) else intent.getParcelableExtra(Intent.EXTRA_INTENT))
                confirm?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)?.let(context::startActivity)
            }
            PackageInstaller.STATUS_SUCCESS -> Log.info("Update installed")
            else -> Log.info("Update not installed ($status): ${intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)}")
        }
    }
}
