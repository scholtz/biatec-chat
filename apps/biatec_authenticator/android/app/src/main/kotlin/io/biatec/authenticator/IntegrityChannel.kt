package io.biatec.authenticator

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.Debug
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.security.MessageDigest

/**
 * The facts the Dart integrity detectors cannot read themselves (channel io.biatec.authenticator/integrity): system properties, whether a
 * debugger is attached, whether a hooking-framework class is loaded, and the SHA-256 of the app's signing certificate. The files and /proc
 * are read in Dart. The policy is in Dart too; nothing here decides, and nothing is logged.
 *
 * property(key) -> String? | debuggerAttached -> Boolean | classPresent(name) -> Boolean | signingDigest -> String? (lower-case hex)
 */
class IntegrityChannel(private val context: Context) : FlutterPlugin, MethodChannel.MethodCallHandler {
    private var channel: MethodChannel? = null

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel = MethodChannel(binding.binaryMessenger, "io.biatec.authenticator/integrity").also { it.setMethodCallHandler(this) }
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel?.setMethodCallHandler(null)
        channel = null
    }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        try {
            when (call.method) {
                "property" -> result.success(property(call.argument<String>("key") ?: ""))
                "debuggerAttached" -> result.success(Debug.isDebuggerConnected() || Debug.waitingForDebugger())
                "classPresent" -> result.success(classPresent(call.argument<String>("name") ?: ""))
                "signingDigest" -> result.success(signingDigest())
                else -> result.notImplemented()
            }
        } catch (e: Throwable) {
            result.error("integrity_error", "The fact could not be read", null)
        }
    }

    /** Reads a system property through the hidden SystemProperties class, the way build tools do; null when it cannot be read. */
    private fun property(key: String): String? = try {
        val cls = Class.forName("android.os.SystemProperties")
        (cls.getMethod("get", String::class.java).invoke(null, key) as? String)?.takeIf { it.isNotEmpty() }
    } catch (e: Throwable) {
        null
    }

    /** True when a class of that name is already loaded or loadable (Xposed and Substrate put theirs in the app's class loader). */
    private fun classPresent(name: String): Boolean = try {
        Class.forName(name, false, context.classLoader)
        true
    } catch (e: Throwable) {
        false
    }

    private fun signingDigest(): String? {
        val pm = context.packageManager
        val cert = if (Build.VERSION.SDK_INT >= 28) {
            val info = pm.getPackageInfo(context.packageName, PackageManager.GET_SIGNING_CERTIFICATES).signingInfo ?: return null
            // More than one current signer is not the release. (A key rotation keeps the same current signer, whose certificate is the one compared.)
            if (info.hasMultipleSigners()) return null
            info.apkContentsSigners.singleOrNull()?.toByteArray()
        } else {
            @Suppress("DEPRECATION")
            pm.getPackageInfo(context.packageName, PackageManager.GET_SIGNATURES).signatures?.singleOrNull()?.toByteArray()
        } ?: return null
        return MessageDigest.getInstance("SHA-256").digest(cert).joinToString("") { "%02x".format(it) }
    }
}
