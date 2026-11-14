package io.biatec.authenticator

import android.content.pm.PackageManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyInfo
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import android.util.Base64
import androidx.biometric.BiometricManager.Authenticators.BIOMETRIC_STRONG
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.embedding.engine.plugins.activity.ActivityAware
import io.flutter.embedding.engine.plugins.activity.ActivityPluginBinding
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.security.InvalidAlgorithmParameterException
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.ProviderException
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Android Keystore implementation of the device key channel (io.biatec.authenticator/keystore): TWO keys (protocol 1.1).
 *
 *  - Approval key: setUserAuthenticationRequired(true) with setUserAuthenticationParameters(0, AUTH_BIOMETRIC_STRONG), so the keystore itself
 *    wants a strong biometric for EVERY signature (no time window, no device PIN), invalidated when a fingerprint or face is enrolled, usable
 *    only while the device is unlocked, StrongBox when the device has it. Signing is one operation with the prompt: a BiometricPrompt whose
 *    CryptoObject wraps the Signature, so nothing in this app (or code running as it) can sign with this key without the prompt.
 *  - Presence key: usable while the device is unlocked, no user authentication. Signs device proofs, deny and code_failed receipts.
 *
 * Both are non-exportable: only sign is offered. After the approval key is made, KeyInfo is read back and the key is refused (and both keys
 * deleted) unless it is in hardware, authentication bound and invalidated by biometric enrollment. Nothing here logs a key, an alias or an
 * exception text. There is no fallback to a software key or to the device credential.
 */
class KeystorePlugin(private val context: android.content.Context) : FlutterPlugin, ActivityAware, MethodChannel.MethodCallHandler {
    private var channel: MethodChannel? = null
    private var activity: FragmentActivity? = null

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel = MethodChannel(binding.binaryMessenger, "io.biatec.authenticator/keystore").also { it.setMethodCallHandler(this) }
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel?.setMethodCallHandler(null)
        channel = null
        worker.shutdown()
    }

    // The activity is only there in the app (not in the foreground service's engine): the approval prompt needs it, the presence key does not.
    override fun onAttachedToActivity(binding: ActivityPluginBinding) { activity = binding.activity as? FragmentActivity }
    override fun onDetachedFromActivityForConfigChanges() { activity = null }
    override fun onReattachedToActivityForConfigChanges(binding: ActivityPluginBinding) { activity = binding.activity as? FragmentActivity }
    override fun onDetachedFromActivity() { activity = null }

    private val ks: KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    // Keystore work (key generation, signing) can take hundreds of milliseconds, StrongBox more: it runs off the platform thread and the
    // result is posted back to it, as MethodChannel requires.
    private var worker: ExecutorService = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())

    // Kept next to the app (never in a backup: allowBackup is off): says the approval key was made here, so a key that is gone (the system
    // removes it when the last biometric is removed) is reported as invalidated instead of as a fresh install.
    private val markers by lazy { context.getSharedPreferences("biatec_device_key_markers", android.content.Context.MODE_PRIVATE) }

    private companion object {
        const val APPROVAL_ALIAS = "biatec.device.approval.v2"
        const val PRESENCE_ALIAS = "biatec.device.presence.v2"
        const val MARKER = "approval_created"
    }

    /** A failure with a code Dart can tell apart from an unexpected keystore error. */
    private class PluginError(val code: String, message: String, val details: Any? = null) : Exception(message)

    /** Answers the channel call once, whatever the prompt callbacks do afterwards. */
    private class Once(private val result: MethodChannel.Result, private val main: Handler) {
        private val done = AtomicBoolean(false)
        fun success(value: Any?) { if (done.compareAndSet(false, true)) main.post { result.success(value) } }
        fun error(code: String, message: String, details: Any? = null) { if (done.compareAndSet(false, true)) main.post { result.error(code, message, details) } }
    }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        val method = call.method
        val data = call.argument<ByteArray>("data")
        val once = Once(result, main)
        // Created again after a detach (the plugin instance can be attached to another engine).
        if (worker.isShutdown) worker = Executors.newSingleThreadExecutor()
        worker.execute {
            try {
                when (method) {
                    "generateKeys" -> once.success(generateKeys())
                    "load" -> once.success(when (call.argument<String>("use")) {
                        "approval" -> publicOf(APPROVAL_ALIAS, true)
                        "presence" -> publicOf(PRESENCE_ALIAS, false)
                        else -> throw PluginError("bad_args", "use is required")
                    })
                    "keysState" -> once.success(keysState())
                    "signPresence" -> once.success(signPresence(data))
                    "signApproval" -> signApproval(data, call.argument<String>("title"), call.argument<String>("subtitle"), once)
                    "deleteKeys" -> { deleteKeys(); once.success(null) }
                    // The single key of an install from before the two keys, by its alias.
                    "delete" -> {
                        val alias = call.argument<String>("alias")
                        if (alias.isNullOrEmpty()) throw PluginError("bad_args", "alias is required")
                        if (alias != APPROVAL_ALIAS && alias != PRESENCE_ALIAS && ks.containsAlias(alias)) ks.deleteEntry(alias)
                        once.success(null)
                    }
                    else -> main.post { result.notImplemented() }
                }
            } catch (e: PluginError) {
                once.error(e.code, e.message ?: "", e.details)
            } catch (e: Throwable) {
                // Throwable, not Exception: a class that is missing on an old API level must still answer Dart instead of leaving the call open.
                once.error("keystore_error", "The keystore could not do that")
            }
        }
    }

    // --- Presence key -------------------------------------------------------------------------------------------------------------------

    private fun signPresence(data: ByteArray?): ByteArray {
        if (data == null) throw PluginError("bad_args", "data is required")
        val key = ks.getKey(PRESENCE_ALIAS, null) as? PrivateKey ?: throw PluginError("no_key", "No presence key")
        val der = Signature.getInstance("SHA256withECDSA").run { initSign(key); update(data); sign() }
        return derToRaw(der)
    }

    // --- Approval key -------------------------------------------------------------------------------------------------------------------

    /**
     * The prompt and the signature are one operation: the CryptoObject wraps a Signature that was initialised with the approval key, and the
     * keystore releases the key for this one operation only after BiometricPrompt reports a strong biometric. Strong biometrics only: no
     * setNegativeButtonText-less device-credential mode, so the PIN can never stand in for the fingerprint or face.
     */
    private fun signApproval(data: ByteArray?, title: String?, subtitle: String?, result: Once) {
        if (data == null) return result.error("bad_args", "data is required")
        val host = activity ?: return result.error("no_activity", "No screen to ask on")
        val key = try {
            ks.getKey(APPROVAL_ALIAS, null) as? PrivateKey
        } catch (e: Exception) {
            null
        }
        if (key == null) {
            return if (markers.getBoolean(MARKER, false)) result.error("approval_key_invalidated", "The approval key is gone")
            else result.error("no_key", "No approval key")
        }
        val signature = Signature.getInstance("SHA256withECDSA")
        try {
            signature.initSign(key)
        } catch (e: KeyPermanentlyInvalidatedException) {
            return result.error("approval_key_invalidated", "The approval key was invalidated")
        }
        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle(title?.take(80) ?: "Approve this action")
            .setSubtitle(subtitle?.take(160))
            .setNegativeButtonText("Cancel")
            .setAllowedAuthenticators(BIOMETRIC_STRONG)
            // A face is not accepted by merely looking at the phone: the user taps Confirm.
            .setConfirmationRequired(true)
            .build()
        val callback = object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(r: BiometricPrompt.AuthenticationResult) {
                // Signing is the keystore's work again: off the platform thread.
                worker.execute {
                    try {
                        val s = r.cryptoObject?.signature ?: return@execute result.error("auth_failed", "The biometric check gave no signature")
                        s.update(data)
                        result.success(derToRaw(s.sign()))
                    } catch (e: KeyPermanentlyInvalidatedException) {
                        result.error("approval_key_invalidated", "The approval key was invalidated")
                    } catch (e: Throwable) {
                        result.error("auth_failed", "The signature could not be made")
                    }
                }
            }

            override fun onAuthenticationError(code: Int, message: CharSequence) {
                when (code) {
                    BiometricPrompt.ERROR_USER_CANCELED, BiometricPrompt.ERROR_NEGATIVE_BUTTON, BiometricPrompt.ERROR_CANCELED ->
                        result.error("cancelled", "The biometric check was cancelled")
                    BiometricPrompt.ERROR_LOCKOUT, BiometricPrompt.ERROR_LOCKOUT_PERMANENT ->
                        result.error("locked_out", "Too many tries. Wait a moment or unlock the phone with its PIN, then try again.")
                    BiometricPrompt.ERROR_NO_BIOMETRICS, BiometricPrompt.ERROR_HW_NOT_PRESENT, BiometricPrompt.ERROR_HW_UNAVAILABLE ->
                        result.error("no_biometrics", "No strong biometric is available on this phone")
                    else -> result.error("auth_failed", "The biometric check did not succeed")
                }
            }
            // onAuthenticationFailed (a finger that did not match): the prompt stays open and the user tries again.
        }
        host.runOnUiThread {
            try {
                BiometricPrompt(host, ContextCompat.getMainExecutor(context), callback).authenticate(info, BiometricPrompt.CryptoObject(signature))
            } catch (e: Throwable) {
                result.error("auth_failed", "The biometric check could not start")
            }
        }
    }

    // --- Creating and reading the keys --------------------------------------------------------------------------------------------------

    private fun hasStrongBox() = context.packageManager.hasSystemFeature(PackageManager.FEATURE_STRONGBOX_KEYSTORE)

    private fun spec(alias: String, approval: Boolean, strongBox: Boolean): KeyGenParameterSpec {
        val b = KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
        if (strongBox) b.setIsStrongBoxBacked(true)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) b.setUnlockedDeviceRequired(true)
        if (approval) {
            b.setUserAuthenticationRequired(true)
            // Per use (timeout 0), strong biometric only: no device credential.
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                b.setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)
            } else {
                @Suppress("DEPRECATION")
                b.setUserAuthenticationValidityDurationSeconds(-1) // -1: every use needs a fingerprint (API 23 to 29)
            }
            b.setInvalidatedByBiometricEnrollment(true)
        }
        return b.build()
    }

    private fun make(alias: String, approval: Boolean) {
        val gen = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        val strongBox = hasStrongBox()
        try {
            gen.initialize(spec(alias, approval, strongBox))
            gen.generateKeyPair()
        } catch (e: Exception) {
            // Devices that advertise StrongBox can still refuse the key: fall back to the TEE, but only for the failures StrongBox is known to
            // cause. Anything else, or no StrongBox at all, is a real error.
            if (strongBox && (e is StrongBoxUnavailableException || e is ProviderException || e is InvalidAlgorithmParameterException)) {
                try {
                    gen.initialize(spec(alias, approval, false))
                    gen.generateKeyPair()
                    return
                } catch (e2: Exception) {
                    throw mapCreateError(e2, approval)
                }
            }
            throw mapCreateError(e, approval)
        }
    }

    /** A key with user authentication needs an enrolled biometric: the keystore refuses without one. */
    private fun mapCreateError(e: Exception, approval: Boolean): Exception =
        if (approval && (e is IllegalStateException || e is InvalidAlgorithmParameterException || e is ProviderException)) {
            PluginError("no_biometrics", "A strong biometric (fingerprint or face) must be set up on this phone first")
        } else e

    private fun generateKeys(): Map<String, Any> {
        val existingApproval = publicOf(APPROVAL_ALIAS, true)
        val existingPresence = publicOf(PRESENCE_ALIAS, false)
        if (existingApproval != null && existingPresence != null && existingApproval["authBound"] == true) {
            return mapOf("approval" to existingApproval, "presence" to existingPresence)
        }
        // Both or neither: a half set (an interrupted enrollment, an invalidated key) is removed and made again.
        deleteKeys()
        make(APPROVAL_ALIAS, true)
        markers.edit().putBoolean(MARKER, true).apply()
        val approval = publicOf(APPROVAL_ALIAS, true)!!
        // Read back, never assumed: refuse unless the keystore says the key is in hardware, needs authentication for every use and is
        // invalidated by a biometric enrollment. Nothing is enrolled with such a key.
        if (approval["securityLevel"] == "software" || approval["securityLevel"] == "unknown" || approval["authBound"] != true) {
            deleteKeys()
            throw PluginError("insecure_key", "This phone cannot protect approvals", mapOf("securityLevel" to approval["securityLevel"]))
        }
        make(PRESENCE_ALIAS, false)
        val presence = publicOf(PRESENCE_ALIAS, false)!!
        if (presence["securityLevel"] == "software") {
            deleteKeys()
            throw PluginError("insecure_key", "The keystore holds the presence key in software", mapOf("securityLevel" to "software"))
        }
        return mapOf("approval" to approval, "presence" to presence)
    }

    private fun deleteKeys() {
        if (ks.containsAlias(APPROVAL_ALIAS)) ks.deleteEntry(APPROVAL_ALIAS)
        if (ks.containsAlias(PRESENCE_ALIAS)) ks.deleteEntry(PRESENCE_ALIAS)
        markers.edit().clear().apply()
    }

    /** Without a prompt: both keys there and the approval key still usable (initSign throws when the biometrics changed), invalidated, or missing. */
    private fun keysState(): String {
        val presence = ks.containsAlias(PRESENCE_ALIAS)
        val approvalHere = ks.containsAlias(APPROVAL_ALIAS)
        if (!approvalHere) return if (markers.getBoolean(MARKER, false)) "approval_key_invalidated" else "missing"
        if (!presence) return "missing"
        return try {
            val key = ks.getKey(APPROVAL_ALIAS, null) as PrivateKey
            // For a per-use key this only starts the operation: it throws when the key was invalidated and asks for nothing.
            Signature.getInstance("SHA256withECDSA").initSign(key)
            "ok"
        } catch (e: KeyPermanentlyInvalidatedException) {
            "approval_key_invalidated"
        } catch (e: Exception) {
            "ok"
        }
    }

    private fun publicOf(alias: String, approval: Boolean): Map<String, Any>? {
        val cert = ks.getCertificate(alias) ?: return null
        val pub = cert.publicKey as ECPublicKey
        val info = keyInfo(alias)
        return mapOf(
            "x" to b64(fixed(pub.w.affineX)),
            "y" to b64(fixed(pub.w.affineY)),
            "securityLevel" to securityLevel(info),
            "authBound" to (approval && isAuthBound(info)),
        )
    }

    /** KeyInfo of the key, or null when it cannot be read (an invalidated key can refuse). */
    private fun keyInfo(alias: String): KeyInfo? = try {
        val key = ks.getKey(alias, null) as PrivateKey
        KeyFactory.getInstance(key.algorithm, "AndroidKeyStore").getKeySpec(key, KeyInfo::class.java)
    } catch (e: Exception) {
        // Nothing is logged: not even an exception, whose text can name keys.
        null
    }

    /** KeyInfo says: authentication for every use, strong biometric only (API 30+), invalidated by biometric enrollment, in hardware. */
    private fun isAuthBound(info: KeyInfo?): Boolean {
        if (info == null || !info.isUserAuthenticationRequired || !info.isInvalidatedByBiometricEnrollment) return false
        if (info.userAuthenticationValidityDurationSeconds > 0) return false
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            if (info.userAuthenticationType and KeyProperties.AUTH_BIOMETRIC_STRONG == 0) return false
            if (info.userAuthenticationType and KeyProperties.AUTH_DEVICE_CREDENTIAL != 0) return false
        }
        return securityLevel(info).let { it != "software" && it != "unknown" }
    }

    /**
     * Where the key lives, from KeyInfo: strongbox, tee, hardware (below API 31 the platform only says "inside secure hardware"),
     * software, or unknown when it cannot be read. The Dart side treats software and unknown as not good enough.
     */
    private fun securityLevel(info: KeyInfo?): String {
        if (info == null) return "unknown"
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            when (info.securityLevel) {
                KeyProperties.SECURITY_LEVEL_STRONGBOX -> "strongbox"
                KeyProperties.SECURITY_LEVEL_TRUSTED_ENVIRONMENT -> "tee"
                KeyProperties.SECURITY_LEVEL_UNKNOWN_SECURE -> "hardware"
                KeyProperties.SECURITY_LEVEL_SOFTWARE -> "software"
                else -> "unknown"
            }
        } else {
            @Suppress("DEPRECATION")
            if (info.isInsideSecureHardware) "hardware" else "software"
        }
    }

    private fun fixed(n: java.math.BigInteger): ByteArray {
        val raw = n.toByteArray().dropWhile { it.toInt() == 0 }.toByteArray()
        return ByteArray(32 - raw.size) + raw
    }

    private fun b64(b: ByteArray) = Base64.encodeToString(b, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)

    /** Converts an ASN.1 DER ECDSA signature to the raw r||s form JWS ES256 uses. */
    private fun derToRaw(der: ByteArray): ByteArray {
        var i = 2
        if (der[1].toInt() and 0x80 != 0) i += (der[1].toInt() and 0x7f)
        fun int(): ByteArray {
            val len = der[i + 1].toInt() and 0xff
            val v = der.copyOfRange(i + 2, i + 2 + len)
            i += 2 + len
            val trimmed = v.dropWhile { it.toInt() == 0 }.toByteArray()
            return ByteArray(32 - trimmed.size) + trimmed
        }
        val r = int()
        val s = int()
        return r + s
    }
}
