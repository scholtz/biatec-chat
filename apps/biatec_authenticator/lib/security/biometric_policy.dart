import 'dart:async';

import 'package:flutter/services.dart';

import '../lock/biometric_authenticator.dart';

/// The strong-biometric policy of the app, through the native channel `io.biatec.authenticator/biometric`.
///
/// Android uses `BiometricPrompt` with `BIOMETRIC_STRONG` (Class 3) only; iOS uses the `LAContext` policy `deviceOwnerAuthenticationWithBiometrics`, which excludes the passcode, with the fallback button hidden. The device PIN, pattern and password never open anything, and there is no other way in. (`local_auth` is not used: on Android its biometric-only mode also accepts weak biometrics.)
///
/// The native side answers `status` with `available`, `none_enrolled`, `no_hardware` or `unavailable`, and `authenticate` (argument `reason`) with `success`, `cancelled`, `failed`, `lockout`, `none_enrolled` or `unavailable`. It calls `screenLocked` on the channel when the screen locks.
class PlatformBiometricAuthenticator implements BiometricAuthenticator {
  PlatformBiometricAuthenticator(
      [this._channel =
          const MethodChannel('io.biatec.authenticator/biometric')]) {
    _channel.setMethodCallHandler((call) async {
      if (call.method == 'screenLocked') _screenLocked.add(null);
      return null;
    });
  }

  final MethodChannel _channel;
  final StreamController<void> _screenLocked = StreamController.broadcast();

  @override
  Stream<void> get screenLocked => _screenLocked.stream;

  @override
  Future<BiometricStatus> status() async {
    try {
      return switch (await _channel.invokeMethod<String>('status')) {
        'available' => BiometricStatus.available,
        'none_enrolled' => BiometricStatus.noneEnrolled,
        'no_hardware' => BiometricStatus.noHardware,
        // Anything else, including an answer this version does not know, is not "available".
        _ => BiometricStatus.unavailable,
      };
    } on Object {
      return BiometricStatus.unavailable;
    }
  }

  @override
  Future<BiometricOutcome> authenticate(String reason) async {
    try {
      return switch (await _channel
          .invokeMethod<String>('authenticate', {'reason': reason})) {
        'success' => BiometricOutcome.success,
        'cancelled' => BiometricOutcome.cancelled,
        'lockout' => BiometricOutcome.lockout,
        'none_enrolled' => BiometricOutcome.noneEnrolled,
        'unavailable' => BiometricOutcome.unavailable,
        _ => BiometricOutcome.failed,
      };
    } on Object {
      // Fails closed: an error is never a success.
      return BiometricOutcome.failed;
    }
  }
}
