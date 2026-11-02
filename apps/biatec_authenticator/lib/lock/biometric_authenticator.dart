/// Whether the phone can check a strong biometric right now.
enum BiometricStatus {
  /// A strong biometric (Android Class 3, Face ID or Touch ID) is enrolled and usable.
  available,

  /// The phone has the hardware but no fingerprint or face is enrolled.
  noneEnrolled,

  /// The phone has no strong biometric hardware.
  noHardware,

  /// The hardware cannot be used right now (locked out, busy, or an update is needed).
  unavailable,
}

/// What a biometric prompt came to. Only [success] opens anything.
enum BiometricOutcome {
  success,

  /// The user closed the prompt.
  cancelled,

  /// The check ran and did not match, or failed for a reason the platform did not name.
  failed,

  /// Too many wrong tries: the operating system refuses further checks for a while.
  lockout,

  /// No fingerprint or face is enrolled (any more).
  noneEnrolled,

  /// The hardware cannot be used right now.
  unavailable,
}

/// A strong-biometric check and nothing else: no device PIN, pattern or password, and no fallback after failed tries. The platform implementation is in `lib/security/biometric_policy.dart`; tests use a fake.
abstract class BiometricAuthenticator {
  Future<BiometricStatus> status();

  /// Asks for the fingerprint or face. Never throws: anything unexpected is [BiometricOutcome.failed].
  Future<BiometricOutcome> authenticate(String reason);

  /// Fires when the screen locks. Native code reports it (a screen-off broadcast on Android, protected data becoming unavailable on iOS).
  Stream<void> get screenLocked;
}
