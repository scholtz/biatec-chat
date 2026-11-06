package io.biatec.authenticator

import android.content.Context
import android.content.pm.PackageManager
import android.util.Base64
import com.google.android.play.core.integrity.IntegrityManagerFactory
import com.google.android.play.core.integrity.IntegrityTokenRequest
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.security.MessageDigest

/**
 * Play Integrity for the enrollment (channel io.biatec.authenticator/attestation): a token that Google signs to say this is the genuine app,
 * installed from Google Play, on a genuine device. Biatec OIDC verifies it; nothing is decided here and nothing is logged.
 *
 * supported -> Boolean (the release's Google Cloud project number is configured)
 * token(nonce) -> {kind: "play_integrity", token}, or an error with code "unavailable"
 *
 * The request nonce is the SHA-256 of the server's nonce, base64url without padding, so the token is bound to this challenge. The Cloud project
 * number comes from the build (`-PplayIntegrityProject=<number>` or gradle.properties), placed in the manifest as meta-data; without it
 * the call is not available and the enrollment goes without an attestation.
 */
class AttestationChannel(private val context: Context) : FlutterPlugin, MethodChannel.MethodCallHandler {
    private var channel: MethodChannel? = null

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel = MethodChannel(binding.binaryMessenger, "io.biatec.authenticator/attestation").also { it.setMethodCallHandler(this) }
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel?.setMethodCallHandler(null)
        channel = null
    }

    /** The project number the build was given, or 0. The manifest value starts with "p" so a long number stays text. */
    private fun projectNumber(): Long = try {
        val info = context.packageManager.getApplicationInfo(context.packageName, PackageManager.GET_META_DATA)
        info.metaData?.getString("io.biatec.authenticator.PLAY_INTEGRITY_PROJECT")?.removePrefix("p")?.toLongOrNull() ?: 0L
    } catch (e: Throwable) {
        0L
    }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "supported" -> result.success(projectNumber() > 0)
            "token" -> {
                val nonce = call.argument<String>("nonce")
                val project = projectNumber()
                if (nonce.isNullOrEmpty()) return result.error("bad_args", "nonce is required", null)
                if (project <= 0) return result.error("unavailable", "Play Integrity is not configured", null)
                try {
                    val digest = MessageDigest.getInstance("SHA-256").digest(nonce.toByteArray())
                    val bound = Base64.encodeToString(digest, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
                    IntegrityManagerFactory.create(context)
                        .requestIntegrityToken(IntegrityTokenRequest.builder().setNonce(bound).setCloudProjectNumber(project).build())
                        .addOnSuccessListener { r -> result.success(mapOf("kind" to "play_integrity", "token" to r.token())) }
                        .addOnFailureListener { result.error("unavailable", "Play Integrity could not attest", null) }
                } catch (e: Throwable) {
                    result.error("unavailable", "Play Integrity could not attest", null)
                }
            }
            else -> result.notImplemented()
        }
    }
}
