package io.biatec.authenticator

import android.content.pm.PackageManager
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
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
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Hardware-backed AES-256-GCM for the two-factor vault (channel io.biatec.authenticator/vault).
 *
 * Two non-exportable keys live in the Android Keystore, StrongBox when the device has it:
 *  - metadata: no user authentication, so the account list can be shown before the user proves anything;
 *  - secret: setUserAuthenticationRequired(true) with a strong biometric only (no screen-lock PIN), a prompt for every use through a
 *    CryptoObject, and invalidated when a fingerprint or face is added or removed.
 * seal returns nonce(12) || ciphertext || tag; the platform makes the nonce (randomized encryption is required of the key), and the
 * associated data is authenticated with updateAAD. Nothing here logs: not a key, a secret, an alias or an exception text.
 */
class VaultPlugin(private val context: android.content.Context) : FlutterPlugin, ActivityAware, MethodChannel.MethodCallHandler {
    private var channel: MethodChannel? = null
    private var activity: FragmentActivity? = null

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel = MethodChannel(binding.binaryMessenger, "io.biatec.authenticator/vault").also { it.setMethodCallHandler(this) }
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel?.setMethodCallHandler(null)
        channel = null
    }

    override fun onAttachedToActivity(binding: ActivityPluginBinding) { activity = binding.activity as? FragmentActivity }
    override fun onDetachedFromActivityForConfigChanges() { activity = null }
    override fun onReattachedToActivityForConfigChanges(binding: ActivityPluginBinding) { activity = binding.activity as? FragmentActivity }
    override fun onDetachedFromActivity() { activity = null }

    private val ks: KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    private fun alias(purpose: String) = if (purpose == "secret") "biatec.vault.secret" else "biatec.vault.metadata"

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "deleteKeys" -> {
                listOf("metadata", "secret").forEach { if (ks.containsAlias(alias(it))) ks.deleteEntry(alias(it)) }
                result.success(null)
            }
            "seal", "open" -> {
                val purpose = call.argument<String>("purpose")
                val data = call.argument<ByteArray>("data")
                val aad = call.argument<ByteArray>("aad")
                if ((purpose != "metadata" && purpose != "secret") || data == null || aad == null) {
                    return result.error("bad_args", "purpose, data and aad are required", null)
                }
                run(call.method == "seal", purpose, data, aad, result)
            }
            else -> result.notImplemented()
        }
    }

    private fun run(seal: Boolean, purpose: String, data: ByteArray, aad: ByteArray, result: MethodChannel.Result) {
        try {
            val cipher: Cipher
            if (seal) {
                cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.ENCRYPT_MODE, key(purpose))
            } else {
                if (data.size < 12 + 16) return result.error("bad_data", "too short", null)
                cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.DECRYPT_MODE, key(purpose), GCMParameterSpec(128, data.copyOfRange(0, 12)))
            }
            if (purpose == "secret") {
                prompt(cipher, result) { authed -> finish(authed, seal, data, aad, result) }
            } else {
                finish(cipher, seal, data, aad, result)
            }
        } catch (e: KeyPermanentlyInvalidatedException) {
            result.error("biometrics_changed", "The vault key was invalidated", null)
        } catch (e: Throwable) {
            result.error("keystore_error", "The keystore could not do that", null)
        }
    }

    private fun finish(cipher: Cipher, seal: Boolean, data: ByteArray, aad: ByteArray, result: MethodChannel.Result) {
        try {
            cipher.updateAAD(aad)
            if (seal) {
                result.success(cipher.iv + cipher.doFinal(data))
            } else {
                result.success(cipher.doFinal(data, 12, data.size - 12))
            }
        } catch (e: javax.crypto.AEADBadTagException) {
            result.error("bad_data", "A sealed value did not verify", null)
        } catch (e: Throwable) {
            result.error("keystore_error", "The keystore could not do that", null)
        }
    }

    /** The strong-biometric prompt that unlocks [cipher] (a CryptoObject, so the key is used only after the user is verified). */
    private fun prompt(cipher: Cipher, result: MethodChannel.Result, then: (Cipher) -> Unit) {
        val host = activity ?: return result.error("auth_failed", "No screen to ask on", null)
        if (BiometricManager.from(context).canAuthenticate(BIOMETRIC_STRONG) != BiometricManager.BIOMETRIC_SUCCESS) {
            return result.error("auth_failed", "No strong biometric is available", null)
        }
        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle("Unlock your two-factor accounts")
            .setNegativeButtonText("Cancel")
            .setAllowedAuthenticators(BIOMETRIC_STRONG)
            .setConfirmationRequired(false)
            .build()
        val callback = object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(r: BiometricPrompt.AuthenticationResult) {
                val authed = r.cryptoObject?.cipher
                if (authed == null) result.error("auth_failed", "No verified key", null) else then(authed)
            }

            override fun onAuthenticationError(code: Int, message: CharSequence) {
                result.error("auth_failed", "The biometric check did not succeed", null)
            }
        }
        host.runOnUiThread {
            BiometricPrompt(host, ContextCompat.getMainExecutor(context), callback).authenticate(info, BiometricPrompt.CryptoObject(cipher))
        }
    }

    private fun key(purpose: String): SecretKey {
        val existing = ks.getKey(alias(purpose), null) as? SecretKey
        if (existing != null) return existing
        val hasStrongBox = context.packageManager.hasSystemFeature(PackageManager.FEATURE_STRONGBOX_KEYSTORE)
        return try {
            generate(purpose, hasStrongBox)
        } catch (e: StrongBoxUnavailableException) {
            generate(purpose, false)
        }
    }

    private fun generate(purpose: String, strongBox: Boolean): SecretKey {
        val builder = KeyGenParameterSpec.Builder(alias(purpose), KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .setRandomizedEncryptionRequired(true)
        if (strongBox) builder.setIsStrongBoxBacked(true)
        if (purpose == "secret") {
            builder.setUserAuthenticationRequired(true)
            builder.setInvalidatedByBiometricEnrollment(true)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                // Timeout 0: a prompt for every operation, strong biometrics only.
                builder.setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)
            } else {
                @Suppress("DEPRECATION")
                builder.setUserAuthenticationValidityDurationSeconds(-1)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) builder.setUnlockedDeviceRequired(true)
        }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(builder.build())
        return gen.generateKey()
    }
}
