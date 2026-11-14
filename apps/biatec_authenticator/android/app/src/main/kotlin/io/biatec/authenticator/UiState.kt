package io.biatec.authenticator

/**
 * Whether the app's window is in front. MainActivity sets it; the foreground service (another Flutter engine in the same process) asks for it, so a
 * request that arrives while the app is open is shown by the app and not by a notification.
 */
object UiState {
    @Volatile
    var visible: Boolean = false
}
