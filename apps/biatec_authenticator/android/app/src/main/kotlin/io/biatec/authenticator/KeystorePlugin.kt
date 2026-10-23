package io.biatec.authenticator

import android.content.pm.PackageManager
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec

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
    }

    private val ks: KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        val alias = call.argument<String>("alias")
        if (alias.isNullOrEmpty()) return result.error("bad_args", "alias is required", null)
        try {
            when (call.method) {
                "generate" -> result.success(publicOf(alias) ?: generate(alias))
                "load" -> result.success(publicOf(alias))
                "sign" -> {
                    val data = call.argument<ByteArray>("data") ?: return result.error("bad_args", "data is required", null)
                    val key = ks.getKey(alias, null) as? java.security.PrivateKey
                        ?: return result.error("no_key", "No key for alias", null)
                    val der = Signature.getInstance("SHA256withECDSA").run { initSign(key); update(data); sign() }
                    result.success(derToRaw(der))
                }
                "delete" -> { if (ks.containsAlias(alias)) ks.deleteEntry(alias); result.success(null) }
                else -> result.notImplemented()
            }
        } catch (e: Exception) {
            result.error("keystore_error", e.message, null)
        }
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
            // Devices that advertise StrongBox can still refuse the key (StrongBoxUnavailableException, ProviderException,
            // InvalidAlgorithmParameterException): fall back to the TEE. Without StrongBox there is nothing to fall back from.
            if (!hasStrongBox) throw e
            gen.initialize(spec(alias, false))
            gen.generateKeyPair()
        }
        return publicOf(alias)!!
    }

    private fun publicOf(alias: String): Map<String, String>? {
        val cert = ks.getCertificate(alias) ?: return null
        val pub = cert.publicKey as ECPublicKey
        return mapOf("x" to b64(fixed(pub.w.affineX)), "y" to b64(fixed(pub.w.affineY)))
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
