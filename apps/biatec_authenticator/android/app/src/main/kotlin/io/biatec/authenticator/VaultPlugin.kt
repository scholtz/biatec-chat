package io.biatec.authenticator

import android.content.pm.PackageManager
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.KeyProperties
import android.security.keystore.UserNotAuthenticatedException
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
import android.os.Handler
import android.os.Looper
import java.security.KeyStore
import java.security.InvalidAlgorithmParameterException
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Hardware-backed AES-256-GCM for the two-factor vault (channel io.biatec.authenticator/vault).
 *
 * Two non-exportable keys live in the Android Keystore, StrongBox when the device has it:
 *  - metadata: no user authentication, so the account list can be shown before the user proves anything;
 *  - secret: setUserAuthenticationRequired(true) with a strong biometric only (no screen-lock PIN), a 30 second window after each strong biometric (the app's own unlock counts), then a prompt, and invalidated when a fingerprint or face is added or removed.
 * seal returns nonce(12) || ciphertext || tag; the platform makes the nonce (randomized encryption is required of the key), and the
 * associated data is authenticated with updateAAD. Nothing here logs: not a key, a secret, an alias or an exception text.
 */
private const val VAULT_AUTH_SECONDS = 30

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

    // Key generation (StrongBox especially) and the AEAD work take a while: they run off the platform thread, and the answer is posted back to it.
    private var worker: ExecutorService = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())

    // A marker kept next to the app (never in a backup: allowBackup is off) that says the secret key was made here, so a key that is gone
    // is reported as invalidated instead of being quietly replaced, which would make the vault unreadable.
    private val markers by lazy { context.getSharedPreferences("biatec_vault_markers", android.content.Context.MODE_PRIVATE) }

    /** Answers the channel call once, whatever the prompt callbacks do afterwards. */
    private class Once(private val result: MethodChannel.Result, private val main: Handler) {
        private val done = AtomicBoolean(false)
        fun success(value: Any?) { if (done.compareAndSet(false, true)) main.post { result.success(value) } }
        fun error(code: String, message: String) { if (done.compareAndSet(false, true)) main.post { result.error(code, message, null) } }
    }

    private fun alias(purpose: String) = if (purpose == "secret") "biatec.vault.secret" else "biatec.vault.metadata"

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "deleteKeys" -> {
                listOf("metadata", "secret").forEach { if (ks.containsAlias(alias(it))) ks.deleteEntry(alias(it)) }
                markers.edit().clear().apply()
                result.success(null)
            }
            "seal", "open" -> {
                val purpose = call.argument<String>("purpose")
                val data = call.argument<ByteArray>("data")
                val aad = call.argument<ByteArray>("aad")
                if ((purpose != "metadata" && purpose != "secret") || data == null || aad == null) {
                    return result.error("bad_args", "purpose, data and aad are required", null)
                }
                if (worker.isShutdown) worker = Executors.newSingleThreadExecutor()
                val once = Once(result, main)
                worker.execute { run(call.method == "seal", purpose, data, aad, once) }
            }
            else -> result.notImplemented()
        }
    }

    private fun newCipher(seal: Boolean, purpose: String, data: ByteArray): Cipher {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        if (seal) {
            cipher.init(Cipher.ENCRYPT_MODE, key(purpose))
        } else {
            cipher.init(Cipher.DECRYPT_MODE, key(purpose), GCMParameterSpec(128, data.copyOfRange(0, 12)))
        }
        return cipher
    }

    /** Runs [block], turning the failures the keystore can cause into the codes Dart expects. */
    private fun guarded(result: Once, block: () -> Unit) {
        try {
            block()
        } catch (e: KeyPermanentlyInvalidatedException) {
            result.error("biometrics_changed", "The vault key was invalidated")
        } catch (e: NoBiometricsException) {
            result.error("auth_failed", "No strong biometric is enrolled")
        } catch (e: Throwable) {
            result.error("keystore_error", "The keystore could not do that")
        }
    }

    private fun run(seal: Boolean, purpose: String, data: ByteArray, aad: ByteArray, result: Once) {
        guarded(result) {
            if (!seal && data.size < 12 + 16) return@guarded result.error("bad_data", "too short")
            if (purpose != "secret") return@guarded finish(newCipher(seal, purpose, data), seal, data, aad, result)
            // The secret key accepts a strong biometric for a short window (see generate): the unlock that opened the app counts, so the codes
            // of every account open without a prompt each. When the window has run out the user is asked once more, then the key is used.
            val ready = try {
                newCipher(seal, purpose, data)
            } catch (e: UserNotAuthenticatedException) {
                null
            }
            if (ready != null) return@guarded finish(ready, seal, data, aad, result)
            prompt(result) {
                worker.execute { guarded(result) { finish(newCipher(seal, purpose, data), seal, data, aad, result) } }
            }
        }
    }

    private class NoBiometricsException : Exception()

    private fun finish(cipher: Cipher, seal: Boolean, data: ByteArray, aad: ByteArray, result: Once) {
        try {
            cipher.updateAAD(aad)
            if (seal) {
                result.success(cipher.iv + cipher.doFinal(data))
            } else {
                result.success(cipher.doFinal(data, 12, data.size - 12))
            }
        } catch (e: javax.crypto.AEADBadTagException) {
            result.error("bad_data", "A sealed value did not verify")
        } catch (e: Throwable) {
            result.error("keystore_error", "The keystore could not do that")
        }
    }

    /** The strong-biometric prompt that opens the secret key's time window. */
    private fun prompt(result: Once, then: () -> Unit) {
        val host = activity ?: return result.error("auth_failed", "No screen to ask on")
        if (BiometricManager.from(context).canAuthenticate(BIOMETRIC_STRONG) != BiometricManager.BIOMETRIC_SUCCESS) {
            return result.error("auth_failed", "No strong biometric is available")
        }
        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle("Unlock your two-factor accounts")
            .setNegativeButtonText("Cancel")
            .setAllowedAuthenticators(BIOMETRIC_STRONG)
            .setConfirmationRequired(false)
            .build()
        val callback = object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(r: BiometricPrompt.AuthenticationResult) = then()

            override fun onAuthenticationError(code: Int, message: CharSequence) {
                result.error("auth_failed", "The biometric check did not succeed")
            }
        }
        host.runOnUiThread {
            BiometricPrompt(host, ContextCompat.getMainExecutor(context), callback).authenticate(info)
        }
    }

    private fun key(purpose: String): SecretKey {
        val existing = ks.getKey(alias(purpose), null) as? SecretKey
        if (existing != null) return existing
        if (markers.getBoolean(purpose, false)) throw KeyPermanentlyInvalidatedException("The key is gone")
        val hasStrongBox = context.packageManager.hasSystemFeature(PackageManager.FEATURE_STRONGBOX_KEYSTORE)
        val created = try {
            generate(purpose, hasStrongBox)
        } catch (e: StrongBoxUnavailableException) {
            generate(purpose, false)
        } catch (e: IllegalStateException) {
            // The secret key needs an enrolled biometric.
            if (purpose == "secret") throw NoBiometricsException() else throw e
        } catch (e: InvalidAlgorithmParameterException) {
            if (purpose == "secret") throw NoBiometricsException() else throw e
        }
        markers.edit().putBoolean(purpose, true).apply()
        return created
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
                // A strong biometric opens the key for VAULT_AUTH_SECONDS, so the list of codes opens with the unlock instead of one prompt per account.
                builder.setUserAuthenticationParameters(VAULT_AUTH_SECONDS, KeyProperties.AUTH_BIOMETRIC_STRONG)
            } else {
                @Suppress("DEPRECATION")
                builder.setUserAuthenticationValidityDurationSeconds(VAULT_AUTH_SECONDS)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) builder.setUnlockedDeviceRequired(true)
        }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(builder.build())
        return gen.generateKey()
    }
}
