package io.biatec.authenticator

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import androidx.core.content.ContextCompat

/**
 * Restarts the "Stay connected for approvals" service after a boot or an app update, and only when it should be running: the user turned the
 * setting on and the device is enrolled ([StayConnectedPrefs.active]). Not exported: the system delivers these broadcasts to it all the same.
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        val prefs = StayConnectedPrefs(context)
        if (!prefs.enabled || !prefs.active) return
        try {
            ContextCompat.startForegroundService(context, Intent(context, StayConnectedService::class.java))
        } catch (e: Exception) {
            // Not allowed to start it now (background start restrictions, a revoked permission): the app must not crash from a broadcast. The service is
            // not running, so it is not marked active; the app starts it again when it is opened.
            prefs.active = false
        }
    }
}
