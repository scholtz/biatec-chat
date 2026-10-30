package io.biatec.authenticator

import android.os.Build
import android.os.Bundle
import android.view.WindowManager
import io.flutter.embedding.android.FlutterFragmentActivity
import io.flutter.embedding.engine.FlutterEngine

// FlutterFragmentActivity: the biometric prompt of local_auth needs a FragmentActivity.
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
        // From Android 12 the filter is not needed: other apps' overlays are hidden above, and the system itself blocks touches that pass
        // through an untrusted window from another app. Accessibility-service windows are trusted by the system and such a service can
        // dispatch gestures directly, so a touch filter would not stop one; it would only break screen filters and chat heads.
        if (Build.VERSION.SDK_INT < 31) window.decorView.filterTouchesWhenObscured = true
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        // The device key lives in the Android Keystore (KeystorePlugin); Dart reaches it over the channel io.biatec.authenticator/keystore.
        flutterEngine.plugins.add(KeystorePlugin(applicationContext))
    }
}
