import 'package:flutter/services.dart';

/// Whether the touch that just reached the app was only partly covered by another app's window. Android 10 and 11 cannot hide overlays and drop only a touch that lands under one; a window painting a fake amount next to the real Approve button covers part of the screen but not the button. Asked on the approve action alone, so a chat head or a screen filter does not lock the rest of the app.
abstract class TouchGuard {
  /// True when the gesture behind the tap that is being handled had a flagged event. Reading it clears it, so a later approval without a touch (a screen reader's activation) is not blocked by an old one.
  Future<bool> partiallyObscured();
}

/// For platforms and tests with nothing to check: nothing is obscured.
class NoTouchGuard implements TouchGuard {
  const NoTouchGuard();

  @override
  Future<bool> partiallyObscured() async => false;
}

/// Asks the Android activity (`MainActivity.dispatchTouchEvent` notes `FLAG_WINDOW_IS_PARTIALLY_OBSCURED` of every touch on Android 10 and 11).
class PlatformTouchGuard implements TouchGuard {
  const PlatformTouchGuard();

  static const channel = MethodChannel('io.biatec.authenticator/touch');

  @override
  Future<bool> partiallyObscured() async {
    try {
      return await channel.invokeMethod<bool>('partiallyObscured') ?? false;
    } on MissingPluginException {
      // iOS and desktop have no such channel: nothing to check.
      return false;
    } on PlatformException {
      // The stricter reading: when the check itself fails the approval is not sent.
      return true;
    }
  }
}
