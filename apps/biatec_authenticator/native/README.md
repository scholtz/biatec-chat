# Native pieces

`android/KeystorePlugin.kt` and `ios/KeystorePlugin.swift` are the device-key plugins. The Android and iOS projects themselves are not in the
repository yet (BCHAT-008), so these files are copied into a generated project by hand.

## Screen-capture protection (BCHAT-031, not wired yet)

The screens that show the recovery code, a request being approved, or the history must not end up in screenshots, screen recordings or the app
switcher. When the platform projects are added:

- **Android:** `window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)` in `MainActivity.onCreate`
  (all screens: the authenticator shows nothing that is meant to be captured).
- **iOS:** put a blur or a plain cover view over the window on `applicationWillResignActive` and remove it on `applicationDidBecomeActive`; and
  observe `UIScreen.capturedDidChangeNotification` to hide the content while the screen is recorded or mirrored.

The Dart side already keeps the binding code out of the stored history and shows the recovery code as non-selectable text.
