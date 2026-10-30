package io.biatec.authenticator

import android.content.pm.PackageManager
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyInfo
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import android.util.Base64
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.InvalidAlgorithmParameterException
import java.security.KeyStore
import java.security.ProviderException
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/**
 * Android Keystore implementation of the device key store channel.
 * The key is created with StrongBox when present, otherwise in the TEE. The private key is
 * non-exportable by construction: only sign is offered.
 */
class KeystorePlugin(private val context: android.content.Context) : FlutterPlugin, MethodChannel.MethodCallHandler {
    private var channel: MethodChannel? = null

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel = MethodChannel(binding.binaryMessenger, "io.biatec.authenticator/keystore").also { it.setMethodCallHandler(this) }
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel?.setMethodCallHandler(null)
        channel = null
        worker.shutdown()
    }

    private val ks: KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    // Keystore work (key generation, signing) can take hundreds of milliseconds, StrongBox more: it runs off the platform thread and the
    // result is posted back to it, as MethodChannel requires.
    private var worker: ExecutorService = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())

    /** A failure with a code Dart can tell apart from an unexpected keystore error. */
    private class PluginError(val code: String, message: String, val details: Any? = null) : Exception(message)

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        val alias = call.argument<String>("alias")
        if (alias.isNullOrEmpty()) return result.error("bad_args", "alias is required", null)
        val data = call.argument<ByteArray>("data")
        val method = call.method
        // Created again after a detach (the plugin instance can be attached to another engine).
        if (worker.isShutdown) worker = Executors.newSingleThreadExecutor()
        worker.execute {
            try {
                val value: Any? = when (method) {
                    "generate" -> publicOf(alias) ?: generate(alias)
                    "load" -> publicOf(alias)
                    "sign" -> sign(alias, data)
                    "delete" -> { if (ks.containsAlias(alias)) ks.deleteEntry(alias); null }
                    else -> { main.post { result.notImplemented() }; return@execute }
                }
                main.post { result.success(value) }
            } catch (e: PluginError) {
                main.post { result.error(e.code, e.message, e.details) }
            } catch (e: Throwable) {
                // Throwable, not Exception: a class that is missing on an old API level must still answer Dart instead of leaving the call open.
                main.post { result.error("keystore_error", e.message, null) }
            }
        }
    }

    private fun sign(alias: String, data: ByteArray?): ByteArray {
        if (data == null) throw PluginError("bad_args", "data is required")
        val key = ks.getKey(alias, null) as? java.security.PrivateKey ?: throw PluginError("no_key", "No key for alias")
        val der = Signature.getInstance("SHA256withECDSA").run { initSign(key); update(data); sign() }
        return derToRaw(der)
    }

    private fun spec(alias: String, strongBox: Boolean): KeyGenParameterSpec =
        KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .apply { if (strongBox) setIsStrongBoxBacked(true) }
            .build()

    private fun generate(alias: String): Map<String, String> {
        val gen = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        val hasStrongBox = context.packageManager.hasSystemFeature(PackageManager.FEATURE_STRONGBOX_KEYSTORE)
        try {
            gen.initialize(spec(alias, hasStrongBox))
            gen.generateKeyPair()
        } catch (e: Exception) {
            // Devices that advertise StrongBox can still refuse the key: fall back to the TEE, but only for the failures StrongBox is known
            // to cause (securityLevel() reports the level the key actually got). Anything else, or no StrongBox at all, is a real error.
            // hasStrongBox first: StrongBoxUnavailableException does not exist below API 28.
            if (!hasStrongBox) throw e
            val strongBoxRefused = e is StrongBoxUnavailableException || e is ProviderException || e is InvalidAlgorithmParameterException
            if (!strongBoxRefused) throw e
            gen.initialize(spec(alias, false))
            gen.generateKeyPair()
        }
        // A key that ended up in software is deleted and refused, not enrolled (BCHAT-032). A level that could not be read is returned as
        // "unknown": enrollment refuses it and removes the key, but a read hiccup must not destroy a key that may be in StrongBox.
        val created = publicOf(alias)!!
        if (created["securityLevel"] == "software") {
            ks.deleteEntry(alias)
            throw PluginError("insecure_key", "The keystore holds the key in software, not in secure hardware", mapOf("securityLevel" to "software"))
        }
        return created
    }

    private fun publicOf(alias: String): Map<String, String>? {
        val cert = ks.getCertificate(alias) ?: return null
        val pub = cert.publicKey as ECPublicKey
        return mapOf("x" to b64(fixed(pub.w.affineX)), "y" to b64(fixed(pub.w.affineY)), "securityLevel" to securityLevel(alias))
    }

    /**
     * Where the key lives, from KeyInfo: strongbox, tee, hardware (below API 31 the platform only says "inside secure hardware"),
     * software, or unknown when it cannot be read. The Dart side treats software and unknown as not good enough.
     */
    private fun securityLevel(alias: String): String = try {
        val key = ks.getKey(alias, null) as java.security.PrivateKey
        val info = KeyFactory.getInstance(key.algorithm, "AndroidKeyStore").getKeySpec(key, KeyInfo::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
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
    } catch (e: Exception) {
        // Nothing is logged: not even an exception, whose text can name keys.
        "unknown"
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
