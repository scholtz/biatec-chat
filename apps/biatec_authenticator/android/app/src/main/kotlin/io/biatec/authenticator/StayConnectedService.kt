package io.biatec.authenticator

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.ServiceCompat
import io.flutter.FlutterInjector
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.embedding.engine.dart.DartExecutor
import io.flutter.plugin.common.MethodChannel
import io.flutter.plugins.GeneratedPluginRegistrant

/**
 * The foreground service behind "Stay connected for approvals" (Android only, off by default). It keeps the process alive with a permanent,
 * low-priority notification and runs the device channel in a headless Flutter engine (Dart entry point `stayConnectedMain`, lib/channel/background_main.dart).
 *
 * The channel signs its ticket request with the device key like everything else, so this engine gets the same KeystorePlugin as MainActivity. Without
 * it the ticket request cannot be signed and nothing connects; the device proof is never skipped.
 *
 * Foreground service type `specialUse` (see the manifest and the README for the Play Console declaration). The app does not start it from the
 * background: Dart starts it while the app is in front, the boot receiver restarts it after a boot or an update, and START_STICKY restarts it after
 * the system kills the process.
 */
class StayConnectedService : Service() {
    private var engine: FlutterEngine? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val prefs = StayConnectedPrefs(this)
        // startForegroundService() obliges the service to call startForeground() within seconds, even when it is about to stop again.
        enterForeground(prefs)
        if (!prefs.active) {
            stopSelf()
            return START_NOT_STICKY
        }
        if (engine == null) startEngine()
        return START_STICKY
    }

    private fun enterForeground(prefs: StayConnectedPrefs) {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        // Low importance: no sound, no heads-up. The user chose to stay connected; the notification only says so. (minSdk is 28: channels always exist.)
        manager.createNotificationChannel(NotificationChannel(CHANNEL_ID, prefs.channelName, NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val builder = Notification.Builder(this, CHANNEL_ID)
        builder
            .setSmallIcon(applicationInfo.icon)
            .setContentTitle(prefs.title)
            .setContentText(prefs.text)
            .setContentIntent(open)
            .setOngoing(true)
            .setShowWhen(false)
            .setCategory(Notification.CATEGORY_SERVICE)
            // The text is fixed and names nothing, but a locked phone does not need to show it.
            .setVisibility(Notification.VISIBILITY_PRIVATE)
        if (Build.VERSION.SDK_INT >= 31) builder.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
        // The type is a manifest attribute from Android 14 (API 34), where specialUse exists; before that no type is needed.
        val type = if (Build.VERSION.SDK_INT >= 34) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0
        ServiceCompat.startForeground(this, NOTIFICATION_ID, builder.build(), type)
    }

    private fun startEngine() {
        val loader = FlutterInjector.instance().flutterLoader()
        loader.startInitialization(applicationContext)
        loader.ensureInitializationComplete(applicationContext, null)
        val e = FlutterEngine(applicationContext)
        // The plugins of pubspec.yaml (secure storage, notifications, connectivity), and the one the app registers by hand: the keystore.
        GeneratedPluginRegistrant.registerWith(e)
        e.plugins.add(KeystorePlugin(applicationContext))
        MethodChannel(e.dartExecutor.binaryMessenger, "io.biatec.authenticator/stay_connected_service").setMethodCallHandler { call, result ->
            when (call.method) {
                "uiVisible" -> result.success(UiState.visible)
                "stopSelf" -> {
                    // The device is no longer enrolled: nothing to stay connected to, and no restart after a boot.
                    StayConnectedPrefs(this).active = false
                    result.success(null)
                    stopSelf()
                }
                else -> result.notImplemented()
            }
        }
        e.dartExecutor.executeDartEntrypoint(
            DartExecutor.DartEntrypoint(loader.findAppBundlePath(), "package:biatec_authenticator/channel/background_main.dart", "stayConnectedMain"),
        )
        engine = e
    }

    override fun onDestroy() {
        engine?.destroy()
        engine = null
        super.onDestroy()
    }

    companion object {
        const val CHANNEL_ID = "stay_connected"
        const val NOTIFICATION_ID = 7001
    }
}
