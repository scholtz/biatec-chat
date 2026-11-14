package io.biatec.authenticator

import android.security.keystore.UserNotAuthenticatedException
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * T-241 / BCHAT-007, to be run on a real phone or an emulator with a strong biometric enrolled (an emulator: Settings > Security > Fingerprint, then
 * `adb -e emu finger touch 1` is not needed here: no prompt is shown by this test):
 *
 *   cd apps/biatec_authenticator/android && ./gradlew :app:connectedDebugAndroidTest
 *
 * It checks that the approval key reports user authentication required, hardware backing and per-use authentication, and that signing with it
 * outside a BiometricPrompt fails with UserNotAuthenticatedException (the keystore refuses, whatever the app code does), while the presence key
 * signs without any prompt.
 */
@RunWith(AndroidJUnit4::class)
class ApprovalKeyInstrumentedTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val plugin = KeystorePlugin(context)

    private class Capture : MethodChannel.Result {
        val latch = CountDownLatch(1)
        var value: Any? = null
        var errorCode: String? = null
        override fun success(result: Any?) { value = result; latch.countDown() }
        override fun error(code: String, message: String?, details: Any?) { errorCode = code; latch.countDown() }
        override fun notImplemented() { errorCode = "not_implemented"; latch.countDown() }
    }

    private fun call(method: String, args: Map<String, Any?>? = null): Capture {
        val c = Capture()
        plugin.onMethodCall(MethodCall(method, args), c)
        assertTrue("the plugin did not answer $method", c.latch.await(30, TimeUnit.SECONDS))
        return c
    }

    @Before fun fresh() { call("deleteKeys") }
    @After fun clean() { call("deleteKeys") }

    @Test
    fun approvalKeyIsHardwareBackedAndAuthenticationBound() {
        val made = call("generateKeys")
        assertEquals("generateKeys failed: ${made.errorCode} (is a strong biometric enrolled?)", null, made.errorCode)
        @Suppress("UNCHECKED_CAST")
        val keys = made.value as Map<String, Map<String, Any>>
        val approval = keys["approval"]!!
        assertEquals(true, approval["authBound"])
        assertTrue(approval["securityLevel"] in listOf("strongbox", "tee", "hardware"))
        assertEquals(false, keys["presence"]!!["authBound"])
        assertEquals("ok", call("keysState").value)
    }

    @Test
    fun approvalKeyCannotSignOutsideABiometricPrompt() {
        assertEquals(null, call("generateKeys").errorCode)
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val key = ks.getKey("biatec.device.approval.v2", null) as PrivateKey
        assertNotNull(key)
        try {
            Signature.getInstance("SHA256withECDSA").run { initSign(key); update(ByteArray(8)); sign() }
            fail("the approval key signed without a BiometricPrompt")
        } catch (e: UserNotAuthenticatedException) {
            // expected: the keystore wants the CryptoObject of an authenticated BiometricPrompt for this operation
        }
    }

    @Test
    fun presenceKeySignsWithoutAPrompt() {
        assertEquals(null, call("generateKeys").errorCode)
        val sig = call("signPresence", mapOf("data" to ByteArray(8))).value as ByteArray
        assertEquals(64, sig.size)
    }
}
