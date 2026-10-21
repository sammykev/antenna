package io.github.sammykev.antenna.alerts

import android.app.AlarmManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import io.github.sammykev.antenna.Log
import io.github.sammykev.antenna.MainActivity
import io.github.sammykev.antenna.R
import io.github.sammykev.antenna.Services
import io.github.sammykev.antenna.data.LibraryEntry
import io.github.sammykev.antenna.data.MediaItem
import io.github.sammykev.antenna.sports.EspnFixtures
import io.github.sammykev.antenna.sports.Fixture
import io.github.sammykev.antenna.sports.FixtureMatcher
import io.github.sammykev.antenna.sports.FixtureState
import io.github.sammykev.antenna.sports.ScoreAlerts
import io.github.sammykev.antenna.sports.ScoreEvent
import io.github.sammykev.antenna.sports.ScoreEventKind
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant
import java.time.LocalDate

/**
 * While a followed team plays, checks the score every minute and posts kick-off, goal and full-time notifications
 * ("Watch" opens the channel showing it). A foreground service, so Android keeps it running with Antenna closed; an
 * exact alarm starts it shortly before the next followed kick-off.
 */
class ScoreAlertService : Service() {
    companion object {
        const val CHANNEL = "scores"
        private const val ONGOING_CHANNEL = "following"
        private const val ONGOING_ID = 1

        fun createChannels(context: Context) {
            if (Build.VERSION.SDK_INT < 26) return
            val manager = context.getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "Goals and results", NotificationManager.IMPORTANCE_HIGH)
                .apply { description = "Kick-off, goals and full time for the teams you follow" })
            manager.createNotificationChannel(NotificationChannel(ONGOING_CHANNEL, "Following a match", NotificationManager.IMPORTANCE_LOW)
                .apply { description = "Shown while Antenna checks the score of a followed match" })
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var running = false
    private var nextId = 100

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        createChannels(this)
        val ongoing = ongoing("Following your teams…")
        if (Build.VERSION.SDK_INT >= 29) startForeground(ONGOING_ID, ongoing, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        else startForeground(ONGOING_ID, ongoing)
        if (!running) {
            running = true
            scope.launch { follow() }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private suspend fun follow() {
        val feed = EspnFixtures(Services.http)
        val before = mutableMapOf<String, Fixture>()
        val channels = mutableMapOf<String, LibraryEntry?>()
        Log.info("Score alerts: started")
        try {
            while (scope.isActive) {
                val teams = ScoreAlerts.followed(Services.library)
                val feeds = EspnFixtures.feeds.filter { f -> teams.any { it.feedPath == f.path } }
                if (feeds.isEmpty()) break
                val today = LocalDate.now()
                val fixtures = (feed.getDay(today.minusDays(1), feeds) + feed.getDay(today, feeds)).filter { ScoreAlerts.involves(it, teams) }
                for (e in ScoreAlerts.changes(before, fixtures, teams)) {
                    val channel = channels.getOrPut(e.fixture.id) { FixtureMatcher.find(Services.library, e.fixture).channels.firstOrNull() }
                    notify(e, channel)
                }
                fixtures.forEach { before[it.id] = it }

                val live = fixtures.filter { it.state == FixtureState.Live }
                val soon = fixtures.filter { it.state == FixtureState.Scheduled && Duration.between(Instant.now(), it.start) < Duration.ofMinutes(20) }
                if (live.isEmpty() && soon.isEmpty()) break  // nothing on now: sleep until the next kick-off
                NotificationManagerCompat.from(this).notifyIfAllowed(ONGOING_ID, ongoing(
                    if (live.isNotEmpty()) live.joinToString(" · ") { "${it.home} ${it.homeScore}–${it.awayScore} ${it.away} (${it.status})" }
                    else "${soon[0].title} at ${io.github.sammykev.antenna.ui.hhmm.format(soon[0].start)}"))
                delay(60_000)
            }
        } catch (e: Exception) {
            if (e !is kotlinx.coroutines.CancellationException) Log.error("Score alerts failed", e)
        }
        Log.info("Score alerts: stopped")
        running = false
        ScoreAlertScheduler.schedule(applicationContext)
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun notify(e: ScoreEvent, channel: LibraryEntry?) {
        Log.info("Score alert: ${e.title} — ${e.text}")
        val builder = NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(e.title)
            .setContentText(e.text)
            .setAutoCancel(true)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setContentIntent(open(e.fixture.id, null))
        if (channel != null && e.kind != ScoreEventKind.FullTime)
            builder.addAction(0, "Watch on ${channel.item.title}", open(e.fixture.id, channel.item))
        NotificationManagerCompat.from(this).notifyIfAllowed(nextId++, builder.build())
    }

    private fun open(fixtureId: String, watch: MediaItem?): PendingIntent {
        val intent = Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(MainActivity.FIXTURE_EXTRA, fixtureId)
        if (watch != null) intent.putExtra(MainActivity.WATCH_EXTRA, "${watch.sourceId}|${watch.externalId}")
        return PendingIntent.getActivity(this, nextId++ + if (watch == null) 0 else 50_000, intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }

    private fun ongoing(text: String): Notification = NotificationCompat.Builder(this, ONGOING_CHANNEL)
        .setSmallIcon(R.drawable.ic_notification)
        .setContentTitle("Following your teams")
        .setContentText(text)
        .setOngoing(true)
        .setSilent(true)
        .setContentIntent(open("", null))
        .build()

    private fun NotificationManagerCompat.notifyIfAllowed(id: Int, n: Notification) {
        if (areNotificationsEnabled()) try { notify(id, n) } catch (_: SecurityException) { }
    }
}

/** Starts the score service now if a followed team is playing, else sets an alarm for shortly before the next kick-off. */
object ScoreAlertScheduler {
    private const val ALARM_REQUEST = 7001

    fun schedule(context: Context) {
        try {
            val teams = ScoreAlerts.followed(Services.library)
            val alarms = context.getSystemService(AlarmManager::class.java)
            val alarm = PendingIntent.getBroadcast(context, ALARM_REQUEST, Intent(context, ScoreAlarmReceiver::class.java),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            alarms.cancel(alarm)
            val feeds = EspnFixtures.feeds.filter { f -> teams.any { it.feedPath == f.path } }
            if (feeds.isEmpty()) return

            val feed = EspnFixtures(Services.http)
            val today = LocalDate.now()
            val fixtures = (-1..3).flatMap { feed.getDay(today.plusDays(it.toLong()), feeds) }.filter { ScoreAlerts.involves(it, teams) }
            val now = Instant.now()
            if (fixtures.any { it.state == FixtureState.Live || it.state == FixtureState.Scheduled && Duration.between(now, it.start) < Duration.ofMinutes(20) }) {
                start(context)
                return
            }
            val next = fixtures.filter { it.state == FixtureState.Scheduled }.minByOrNull { it.start } ?: return
            val at = next.start.minus(Duration.ofMinutes(10)).toEpochMilli()
            if (Build.VERSION.SDK_INT >= 31 && !alarms.canScheduleExactAlarms()) alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, alarm)
            else alarms.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, alarm)
            Log.info("Score alerts: next is ${next.title} at ${next.start}")
        } catch (e: Exception) {
            Log.info("Score alerts: couldn't schedule: ${e.message}")
        }
    }

    fun start(context: Context) {
        try {
            val intent = Intent(context, ScoreAlertService::class.java)
            if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent) else context.startService(intent)
        } catch (e: Exception) {
            // Android can refuse a foreground service from the background; the next app start tries again.
            Log.info("Score alerts: couldn't start: ${e.message}")
        }
    }
}

class ScoreAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        Services.init(context.applicationContext)
        ScoreAlertScheduler.start(context)
    }
}
