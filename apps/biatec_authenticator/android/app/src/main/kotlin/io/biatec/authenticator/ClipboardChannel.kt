package io.biatec.authenticator

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.PersistableBundle
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel

/**
 * Copies a one-time code to the clipboard marked sensitive (channel io.biatec.authenticator/clipboard).
 *
 * From Android 13 the clip carries EXTRA_IS_SENSITIVE, so the system's clipboard preview and history show it hidden; before that the extra
 * is harmless. Clearing after a delay is done by the Dart side, which clears only if the clipboard still holds the code. Nothing is logged.
 */
class ClipboardChannel(private val context: Context) : FlutterPlugin, MethodChannel.MethodCallHandler {
    private var channel: MethodChannel? = null

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel = MethodChannel(binding.binaryMessenger, "io.biatec.authenticator/clipboard").also { it.setMethodCallHandler(this) }
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel?.setMethodCallHandler(null)
        channel = null
    }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        if (call.method != "copy") return result.notImplemented()
        val text = call.argument<String>("text") ?: return result.error("bad_args", "text is required", null)
        try {
            val clip = ClipData.newPlainText("", text)
            val extras = PersistableBundle()
            // The literal is the value of ClipDescription.EXTRA_IS_SENSITIVE (API 33), so older compile targets build too.
            extras.putBoolean(if (Build.VERSION.SDK_INT >= 33) ClipDescription.EXTRA_IS_SENSITIVE else "android.content.extra.IS_SENSITIVE", true)
            clip.description.extras = extras
            (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(clip)
            result.success(null)
        } catch (e: Throwable) {
            result.error("clipboard_error", "The clipboard could not be written", null)
        }
    }
}
