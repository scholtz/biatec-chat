/// Asks the user to prove presence with a fresh strong biometric before the device key signs, or before a security setting changes. The app's implementation is `LockBiometricGate` (`lib/lock/lock_controller.dart`), which refuses while the app is locked.
abstract class BiometricGate {
  /// True only when the user passed the check. Never throws.
  Future<bool> confirm(String reason);
}
