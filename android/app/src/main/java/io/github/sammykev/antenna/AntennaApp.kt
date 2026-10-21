package io.github.sammykev.antenna

import android.app.Application
import io.github.sammykev.antenna.alerts.ScoreAlertService

class AntennaApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Log.init(filesDir)
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, e ->
            Log.error("Crashed", e)
            previous?.uncaughtException(thread, e)
        }
        Services.init(this)
        ScoreAlertService.createChannels(this)
        Log.info("Started ${BuildConfig.VERSION_NAME} on Android ${android.os.Build.VERSION.RELEASE} (${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL})")
    }
}
