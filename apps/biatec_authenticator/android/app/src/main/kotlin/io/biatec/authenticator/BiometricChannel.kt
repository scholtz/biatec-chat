package io.biatec.authenticator

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Handler
import android.os.Looper
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricManager.Authenticators.BIOMETRIC_STRONG
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.embedding.engine.plugins.activity.ActivityAware
import io.flutter.embedding.engine.plugins.activity.ActivityPluginBinding
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.util.concurrent.atomic.AtomicBoolean

/**
 * The app's biometric lock (channel io.biatec.authenticator/biometric): a strong biometric and nothing else.
 *
 * BiometricPrompt runs with BIOMETRIC_STRONG (Class 3) only. DEVICE_CREDENTIAL is never allowed, so the phone's PIN, pattern or password
 * cannot open the app, and the prompt has a Cancel button instead of a "use PIN" fallback. local_auth is not used because its biometric-only
 * mode also accepts weak (Class 2) biometrics. Nothing is logged: not a result, not an error text.
 *
 * status -> available | none_enrolled | no_hardware | unavailable
 * authenticate(reason) -> success | cancelled | failed | lockout | none_enrolled | unavailable
 * Calls screenLocked on the channel when the screen turns off.
 */
class BiometricChannel(private val context: Context) : FlutterPlugin, ActivityAware, MethodChannel.MethodCallHandler {
    private var channel: MethodChannel? = null
    private var activity: FragmentActivity? = null
    private var receiver: BroadcastReceiver? = null

    // One prompt at a time: a second request while one is open is answered at once, so two prompts are never stacked.
    private val prompting = AtomicBoolean(false)

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        val ch = MethodChannel(binding.binaryMessenger, "io.biatec.authenticator/biometric")
        ch.setMethodCallHandler(this)
        channel = ch
        // The screen turning off locks the app, whatever the background delay is.
        receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context, intent: Intent) {
                if (intent.action == Intent.ACTION_SCREEN_OFF) ch.invokeMethod("screenLocked", null)
            }
        }
        // Not exported: only the system's own screen-off broadcast is wanted.
        ContextCompat.registerReceiver(context, receiver, IntentFilter(Intent.ACTION_SCREEN_OFF), ContextCompat.RECEIVER_NOT_EXPORTED)
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        receiver?.let { try { context.unregisterReceiver(it) } catch (e: IllegalArgumentException) { /* already gone */ } }
        receiver = null
        channel?.setMethodCallHandler(null)
        channel = null
    }

    override fun onAttachedToActivity(binding: ActivityPluginBinding) { activity = binding.activity as? FragmentActivity }
    override fun onDetachedFromActivityForConfigChanges() { activity = null }
    override fun onReattachedToActivityForConfigChanges(binding: ActivityPluginBinding) { activity = binding.activity as? FragmentActivity }
    override fun onDetachedFromActivity() { activity = null }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "status" -> result.success(status())
            "authenticate" -> authenticate(call.argument<String>("reason") ?: "Unlock", result)
            else -> result.notImplemented()
        }
    }

    private fun status(): String = when (BiometricManager.from(context).canAuthenticate(BIOMETRIC_STRONG)) {
        BiometricManager.BIOMETRIC_SUCCESS -> "available"
        BiometricManager.BIOMETRIC_ERROR_NONE_ENROLLED -> "none_enrolled"
        BiometricManager.BIOMETRIC_ERROR_NO_HARDWARE -> "no_hardware"
        else -> "unavailable"
    }

    private fun authenticate(reason: String, result: MethodChannel.Result) {
        val host = activity ?: return result.success("unavailable")
        when (status()) {
            "none_enrolled" -> return result.success("none_enrolled")
            "no_hardware", "unavailable" -> return result.success("unavailable")
        }
        if (!prompting.compareAndSet(false, true)) return result.success("failed")
        // The answer is sent once, whatever the prompt does afterwards.
        val done = AtomicBoolean(false)
        fun answer(value: String) { if (done.compareAndSet(false, true)) { prompting.set(false); result.success(value) } }
        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle(reason)
            .setNegativeButtonText("Cancel")
            .setAllowedAuthenticators(BIOMETRIC_STRONG)
            .setConfirmationRequired(false)
            .build()
        val callback = object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(r: BiometricPrompt.AuthenticationResult) = answer("success")

            // A single mismatch keeps the prompt open; the platform counts the tries and locks out.
            override fun onAuthenticationError(code: Int, message: CharSequence) = answer(
                when (code) {
                    BiometricPrompt.ERROR_USER_CANCELED, BiometricPrompt.ERROR_NEGATIVE_BUTTON, BiometricPrompt.ERROR_CANCELED -> "cancelled"
                    BiometricPrompt.ERROR_LOCKOUT, BiometricPrompt.ERROR_LOCKOUT_PERMANENT -> "lockout"
                    BiometricPrompt.ERROR_NO_BIOMETRICS -> "none_enrolled"
                    BiometricPrompt.ERROR_HW_NOT_PRESENT, BiometricPrompt.ERROR_HW_UNAVAILABLE -> "unavailable"
                    else -> "failed"
                }
            )
        }
        host.runOnUiThread {
            try {
                BiometricPrompt(host, ContextCompat.getMainExecutor(context), callback).authenticate(info)
            } catch (e: Throwable) {
                answer("failed")
            }
        }
    }
}
