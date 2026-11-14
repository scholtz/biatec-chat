package io.biatec.authenticator

import android.content.Context
import android.content.Intent
import android.os.PowerManager
import android.provider.Settings
import androidx.core.content.ContextCompat
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel

/**
 * The channel io.biatec.authenticator/stay_connected between the app's Dart code and the "Stay connected for approvals" service (see
 * lib/channel/stay_connected.dart). The service is only ever started from here, while the app is in front.
 */
class StayConnectedPlugin(private val context: Context) : FlutterPlugin, MethodChannel.MethodCallHandler {
    private var channel: MethodChannel? = null

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel = MethodChannel(binding.binaryMessenger, "io.biatec.authenticator/stay_connected").also { it.setMethodCallHandler(this) }
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel?.setMethodCallHandler(null)
        channel = null
    }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        val prefs = StayConnectedPrefs(context)
        when (call.method) {
            "isEnabled" -> result.success(prefs.enabled)
            "saveEnabled" -> {
                prefs.enabled = call.argument<Boolean>("enabled") == true
                result.success(null)
            }
            "startService" -> {
                // The choice must be on: a start that disagrees with the saved choice is refused, so the service never runs while the setting is off.
                if (!prefs.enabled) return result.error("disabled", "Stay connected is off", null)
                call.argument<String>("title")?.let { prefs.title = it }
                call.argument<String>("text")?.let { prefs.text = it }
                call.argument<String>("channelName")?.let { prefs.channelName = it }
                prefs.active = true
                ContextCompat.startForegroundService(context, Intent(context, StayConnectedService::class.java))
                result.success(null)
            }
            "stopService" -> {
                prefs.active = false
                context.stopService(Intent(context, StayConnectedService::class.java))
                result.success(null)
            }
            "takeBatteryOffer" -> {
                val power = context.getSystemService(Context.POWER_SERVICE) as PowerManager
                val exempt = power.isIgnoringBatteryOptimizations(context.packageName)
                val offer = !prefs.batteryOffered && !exempt
                if (offer) prefs.batteryOffered = true
                result.success(offer)
            }
            "openBatterySettings" -> {
                // The list of apps (no special permission, which Google Play restricts), not the direct "allow" prompt.
                val intent = Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                try {
                    context.startActivity(intent)
                    result.success(null)
                } catch (e: Exception) {
                    result.error("unavailable", "No settings screen for battery optimisation", null)
                }
            }
            else -> result.notImplemented()
        }
    }
}
