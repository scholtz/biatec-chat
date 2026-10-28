import 'dart:typed_data';

/// The HMAC hash of an account. SHA-1 is what nearly every service uses.
enum OtpAlgorithm {
  sha1('SHA1'),
  sha256('SHA256'),
  sha512('SHA512');

  const OtpAlgorithm(this.uriName);

  /// The name in an `otpauth://` link.
  final String uriName;

  static OtpAlgorithm? fromUriName(String name) {
    final n = name.toUpperCase();
    for (final a in values) {
      if (a.uriName == n) return a;
    }
    return null;
  }
}

/// Time-based (RFC 6238) or counter-based (RFC 4226).
enum OtpType {
  totp,
  hotp,
}

/// Limits of an account, shared by the parser and the model so a value cannot be valid in one and refused in the other.
abstract final class OtpLimits {
  static const minDigits = 6;
  static const maxDigits = 8;
  static const minPeriod = 15;
  static const maxPeriod = 120;
  static const defaultPeriod = 30;
  static const maxTextLength = 256;
}

/// One two-factor account: who it is for, and the secret and parameters that make its codes.
///
/// The secret is a byte array that [wipe] overwrites. Nothing here prints it: [toString] names the account and its parameters only.
class OtpAccount {
  OtpAccount({
    required this.issuer,
    required this.label,
    required this.secret,
    this.algorithm = OtpAlgorithm.sha1,
    this.digits = 6,
    this.period = OtpLimits.defaultPeriod,
    this.type = OtpType.totp,
    this.counter = 0,
    this.id,
  }) {
    if (secret.isEmpty) {
      throw ArgumentError('An account needs a secret.');
    }
    if (digits < OtpLimits.minDigits || digits > OtpLimits.maxDigits) {
      throw ArgumentError('Digits must be 6 to 8.');
    }
    if (period < OtpLimits.minPeriod || period > OtpLimits.maxPeriod) {
      throw ArgumentError('The period must be 15 to 120 seconds.');
    }
    if (counter < 0) throw ArgumentError('The counter cannot be negative.');
    if (issuer.length > OtpLimits.maxTextLength ||
        label.length > OtpLimits.maxTextLength) {
      throw ArgumentError('The issuer or name is too long.');
    }
  }

  /// The service, for example `GitHub`. May be empty.
  final String issuer;

  /// The account at the service, for example an email address.
  final String label;

  final Uint8List secret;
  final OtpAlgorithm algorithm;
  final int digits;

  /// Seconds each code lasts (time-based accounts).
  final int period;
  final OtpType type;

  /// The next counter value (counter-based accounts).
  final int counter;

  /// Assigned by the vault; the engine does not use it.
  final String? id;

  /// Strength of the secret in bits. RFC 4226 asks for at least 128, and 160 is recommended; some services give 80.
  int get secretBits => secret.length * 8;

  OtpAccount copyWith({
    String? issuer,
    String? label,
    int? counter,
    String? id,
  }) =>
      OtpAccount(
        issuer: issuer ?? this.issuer,
        label: label ?? this.label,
        secret: secret,
        algorithm: algorithm,
        digits: digits,
        period: period,
        type: type,
        counter: counter ?? this.counter,
        id: id ?? this.id,
      );

  /// Overwrites the secret in memory. The account cannot make codes afterwards.
  void wipe() => secret.fillRange(0, secret.length, 0);

  /// Same service, name, secret and parameters (the id and counter are not compared). For duplicate detection.
  bool sameAccountAs(OtpAccount other) {
    if (issuer != other.issuer ||
        label != other.label ||
        algorithm != other.algorithm ||
        digits != other.digits ||
        period != other.period ||
        type != other.type ||
        secret.length != other.secret.length) {
      return false;
    }
    // Compared without stopping at the first difference.
    var diff = 0;
    for (var i = 0; i < secret.length; i++) {
      diff |= secret[i] ^ other.secret[i];
    }
    return diff == 0;
  }

  /// Never includes the secret.
  @override
  String toString() => 'OtpAccount($issuer / $label, ${type.name}, '
      '${algorithm.uriName}, $digits digits)';
}
