import 'dart:async';

import 'package:flutter/services.dart';

/// Copies a one-time code so that the system does not keep it where others can read it, and takes it back after [clearAfter] if nobody has copied anything else since.
///
/// The native side marks the clip sensitive (Android 13+ `EXTRA_IS_SENSITIVE`, so clipboard previews and history hide it; iOS: local only and expiring), over the channel `io.biatec.authenticator/clipboard`.
abstract class SensitiveClipboard {
  Future<void> copy(String code);
}

class PlatformSensitiveClipboard implements SensitiveClipboard {
  PlatformSensitiveClipboard({
    MethodChannel channel =
        const MethodChannel('io.biatec.authenticator/clipboard'),
    this.clearAfter = const Duration(seconds: 30),
  }) : _channel = channel;

  final MethodChannel _channel;
  final Duration clearAfter;
  Timer? _timer;

  @override
  Future<void> copy(String code) async {
    try {
      await _channel.invokeMethod<void>('copy', {
        'text': code,
        'seconds': clearAfter.inSeconds,
      });
    } on MissingPluginException {
      // A platform without the native side (a desktop build): the plain clipboard, still cleared below.
      await Clipboard.setData(ClipboardData(text: code));
    }
    _timer?.cancel();
    _timer = Timer(clearAfter, () => _clearIfStillHolds(code));
  }

  /// Clears the clipboard only when it still holds [code]: something the user copied since is theirs.
  Future<void> _clearIfStillHolds(String code) async {
    try {
      final now = await Clipboard.getData(Clipboard.kTextPlain);
      if (now?.text == code) {
        await Clipboard.setData(const ClipboardData(text: ''));
      }
    } on Object {
      // The clipboard cannot be read (the app is in the background on a newer system): the native side clears it on its own timer too.
    }
  }
}
