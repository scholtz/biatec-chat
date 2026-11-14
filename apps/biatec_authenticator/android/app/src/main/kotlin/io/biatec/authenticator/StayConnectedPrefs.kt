package io.biatec.authenticator

import android.content.Context
import android.content.SharedPreferences

/**
 * What the "Stay connected for approvals" setting and its service need to know without the Dart side running (after a boot, for example). Nothing
 * secret: the choice, whether the service should be running (the choice is on and the device is enrolled), and the fixed texts of the service's
 * notification as Dart last gave them (they are in the phone's language). Kept in the app's private storage, which is never backed up (allowBackup is off).
 */
class StayConnectedPrefs(context: Context) {
    private val prefs: SharedPreferences = context.applicationContext.getSharedPreferences("stay_connected", Context.MODE_PRIVATE)

    /** The user's choice. Default off. */
    var enabled: Boolean
        get() = prefs.getBoolean("enabled", false)
        set(value) = prefs.edit().putBoolean("enabled", value).apply()

    /** The service should run: the choice is on and the device is enrolled. The boot receiver restarts the service only when this is true. */
    var active: Boolean
        get() = prefs.getBoolean("active", false)
        set(value) = prefs.edit().putBoolean("active", value).apply()

    var title: String
        get() = prefs.getString("title", null) ?: "Biatec Authenticator"
        set(value) = prefs.edit().putString("title", value).apply()

    var text: String
        get() = prefs.getString("text", null) ?: "Connected, waiting for approval requests"
        set(value) = prefs.edit().putString("text", value).apply()

    var channelName: String
        get() = prefs.getString("channelName", null) ?: "Stay connected"
        set(value) = prefs.edit().putString("channelName", value).apply()

    /** True once the battery exemption was offered, so it is offered once. */
    var batteryOffered: Boolean
        get() = prefs.getBoolean("batteryOffered", false)
        set(value) = prefs.edit().putBoolean("batteryOffered", value).apply()
}
