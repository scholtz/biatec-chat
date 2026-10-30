package io.biatec.authenticator

import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.view.MotionEvent
import android.view.WindowManager
import android.widget.Toast
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
        // From Android 12 the filter is not needed: other apps' overlays (TYPE_APPLICATION_OVERLAY, the only type the system lets through
        // at opacity 0.8 or less) are hidden above, and the system blocks touches that pass through any other untrusted window from another
        // app. Accessibility-service windows are trusted by the system and such a service can dispatch gestures directly, so a touch filter
        // would not stop one; it would only break screen filters and chat heads.
        if (Build.VERSION.SDK_INT < 31) window.decorView.filterTouchesWhenObscured = true
    }

    // Android 10 and 11: the filter above only drops a touch that lands under an overlay. An overlay that paints over other content (a
    // fake amount next to a real "Approve") leaves the touch partially obscured, which the filter lets through, so it is stopped here.
    // From Android 12 such overlays are hidden (see onCreate); below Android 10 the system does not report partial occlusion.
    //
    // The whole gesture is stopped, not single events: one that started is cancelled (ACTION_CANCEL), so Flutter never sees a press
    // without its release, and the rest of it is swallowed until the next finger down. The user is told why (a chat head or a screen
    // filter does this too), at most every few seconds.
    private var gestureStopped = false
    private var lastNotice = 0L

    override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
        if (Build.VERSION.SDK_INT !in 29..30) return super.dispatchTouchEvent(ev)
        if (ev.actionMasked == MotionEvent.ACTION_DOWN) gestureStopped = false
        if (gestureStopped) return true
        if ((ev.flags and MotionEvent.FLAG_WINDOW_IS_PARTIALLY_OBSCURED) == 0) return super.dispatchTouchEvent(ev)
        gestureStopped = true
        if (ev.actionMasked != MotionEvent.ACTION_DOWN) {
            val cancel = MotionEvent.obtain(ev)
            cancel.action = MotionEvent.ACTION_CANCEL
            super.dispatchTouchEvent(cancel)
            cancel.recycle()
        }
        val now = SystemClock.elapsedRealtime()
        if (now - lastNotice > 4000) {
            lastNotice = now
            Toast.makeText(this, "Another app is drawing over Biatec Authenticator. Close its floating window or screen filter, then try again.", Toast.LENGTH_LONG).show()
        }
        return true
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        // The device key lives in the Android Keystore (KeystorePlugin); Dart reaches it over the channel io.biatec.authenticator/keystore.
        flutterEngine.plugins.add(KeystorePlugin(applicationContext))
    }
}
