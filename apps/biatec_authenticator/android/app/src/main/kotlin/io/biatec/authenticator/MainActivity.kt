package io.biatec.authenticator

import android.os.Build
import android.os.Bundle
import android.view.MotionEvent
import android.view.WindowManager
import io.flutter.embedding.android.FlutterFragmentActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

// FlutterFragmentActivity: BiometricPrompt (the lock and the vault) needs a FragmentActivity.
class MainActivity : FlutterFragmentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        // No screenshots, screen recordings or app-switcher thumbnails of any screen: the authenticator shows the recovery code,
        // the request being approved and the history, none of which is meant to be captured (BCHAT-031).
        window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        // Android 13+: no screenshot of the app in the recents list, as a second line next to FLAG_SECURE.
        if (Build.VERSION.SDK_INT >= 33) setRecentsScreenshotEnabled(false)
        // Android 12+: other apps' overlay windows (a fake button over "Approve") are hidden while this window is in front.
        if (Build.VERSION.SDK_INT >= 31) window.setHideOverlayWindows(true)
        super.onCreate(savedInstanceState)
        // Android 11 and older cannot hide overlays, so a touch that reaches the window through another app's overlay is dropped (tapjacking).
        // From Android 12 the filter is not needed: other apps' overlays (TYPE_APPLICATION_OVERLAY, the only type the system lets through
        // at opacity 0.8 or less) are hidden above, and the system blocks touches that pass through any other untrusted window from another
        // app. Accessibility-service windows are trusted by the system and such a service can dispatch gestures directly, so a touch filter
        // would not stop one; it would only break screen filters and chat heads.
        if (Build.VERSION.SDK_INT < 31) window.decorView.filterTouchesWhenObscured = true
    }

    // Android 10 and 11: the filter above drops a touch that lands under an overlay, but not one that is only partially obscured (an
    // overlay painting a fake amount next to a real "Approve"). Dropping every partially obscured touch in this full-screen window would
    // lock the whole app whenever a chat head or screen filter is on screen, and on Android 11 even the app's own toast counts. So the
    // flag of every gesture is only noted here, and Dart asks for it on the approve action alone (channel io.biatec.authenticator/touch).
    // From Android 12 the overlay is hidden anyway.
    private var obscuredGesture = false

    override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
        if (Build.VERSION.SDK_INT < 31) {
            if (ev.actionMasked == MotionEvent.ACTION_DOWN) obscuredGesture = false
            if ((ev.flags and MotionEvent.FLAG_WINDOW_IS_PARTIALLY_OBSCURED) != 0) obscuredGesture = true
        }
        return super.dispatchTouchEvent(ev)
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        // Whether the touch behind a tap was partly covered by another app's window (read once, then cleared).
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "io.biatec.authenticator/touch")
            .setMethodCallHandler { call, result ->
                if (call.method == "partiallyObscured") {
                    val flagged = obscuredGesture
                    obscuredGesture = false
                    result.success(flagged)
                } else {
                    result.notImplemented()
                }
            }
        // The device key lives in the Android Keystore (KeystorePlugin); Dart reaches it over the channel io.biatec.authenticator/keystore.
        flutterEngine.plugins.add(KeystorePlugin(applicationContext))
        // The two-factor vault keys (AES-256-GCM, biometric-bound) live in the same keystore, behind io.biatec.authenticator/vault.
        flutterEngine.plugins.add(VaultPlugin(applicationContext))
        // The biometric lock: a strong biometric only, no device PIN (BiometricChannel), reached over io.biatec.authenticator/biometric.
        flutterEngine.plugins.add(BiometricChannel(applicationContext))
        // Copied codes are marked sensitive (ClipboardChannel), over io.biatec.authenticator/clipboard.
        flutterEngine.plugins.add(ClipboardChannel(applicationContext))
        // Facts for the integrity checks (properties, debugger, signing certificate), over io.biatec.authenticator/integrity.
        flutterEngine.plugins.add(IntegrityChannel(applicationContext))
        // The platform attestation at enrollment: Play Integrity here, App Attest on iOS, over io.biatec.authenticator/attestation.
        flutterEngine.plugins.add(AttestationChannel(applicationContext))
        // The "Stay connected for approvals" setting and its foreground service (StayConnectedService), over io.biatec.authenticator/stay_connected.
        flutterEngine.plugins.add(StayConnectedPlugin(applicationContext))
    }

    // Whether the window is in front, for the foreground service: a request that arrives then is shown by the app, not by a notification.
    override fun onStart() {
        super.onStart()
        UiState.visible = true
    }

    override fun onStop() {
        UiState.visible = false
        super.onStop()
    }
}
