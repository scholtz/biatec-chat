/// Where the device key lives, as the native keystore reports it (BCHAT-032).
enum KeySecurityLevel {
  /// iOS: the key is a Secure Enclave key.
  secureEnclave('secure_enclave', 'Secure Enclave'),

  /// Android: a dedicated security chip (StrongBox).
  strongBox('strongbox', 'StrongBox security chip'),

  /// Android: the trusted execution environment.
  trustedEnvironment('tee', 'trusted execution environment'),

  /// Android below API 31: inside secure hardware, but the platform cannot say whether that is StrongBox or the TEE.
  hardware('hardware', 'secure hardware'),

  /// The key is held in software (no hardware protection).
  software('software', 'software only'),

  /// The platform did not say, or said something this app does not know.
  unknown('unknown', 'unknown');

  const KeySecurityLevel(this.wire, this.label);

  /// The value the platform channel carries.
  final String wire;

  /// For the user.
  final String label;

  /// True when the key is protected by hardware. `software` and `unknown` are not: the app fails closed on both.
  bool get isHardware => this != software && this != unknown;

  /// A missing or unrecognised value is [unknown], never a hardware level.
  static KeySecurityLevel fromWire(Object? value) {
    for (final level in values) {
      if (level.wire == value) return level;
    }
    return unknown;
  }
}
