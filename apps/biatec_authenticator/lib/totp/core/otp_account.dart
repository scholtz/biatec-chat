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

  /// The smallest secret accepted, 80 bits: what the smallest keys in wide use (Google's 16 characters) carry. A shorter one is guessable, so it is refused instead of shown as a normal account.
  static const minSecretBytes = 10;

  /// Control characters, invisible characters, and the characters that reorder text: any of them can make two different names look the same or make "evil.com" display as something else.
  static bool hasUnsafeCharacters(String text) {
    for (final u in text.runes) {
      if (_isUnsafe(u)) return true;
    }
    return false;
  }

  static bool _isUnsafe(int u) {
    return u < 0x20 ||
        (u >= 0x7F && u <= 0x9F) ||
        u == 0x00AD ||
        u == 0x061C ||
        (u >= 0x180B && u <= 0x180F) || // Mongolian selectors, vowel separator
        u == 0x17B4 ||
        u == 0x17B5 || // Khmer invisible vowels
        (u >= 0x200B && u <= 0x200F) || // zero width, direction marks
        (u >= 0x2028 && u <= 0x202E) || // line separators, embeddings
        (u >= 0x2060 && u <= 0x2064) || // word joiner, invisible operators
        (u >= 0x2066 && u <= 0x206F) || // isolates, deprecated format controls
        u == 0xFEFF ||
        (u >= 0xFFF9 && u <= 0xFFFC) || // annotation, object replacement
        (u >= 0x1BCA0 && u <= 0x1BCA3) || // shorthand format controls
        (u >= 0x1D173 && u <= 0x1D17A) || // musical formatting
        u == 0x034F || // combining grapheme joiner
        u == 0x115F ||
        u == 0x1160 || // Hangul fillers
        u == 0x3164 ||
        u == 0xFFA0 ||
        u == 0x2800 || // braille blank
        (u >= 0xFE00 && u <= 0xFE0F) || // variation selectors
        (u >= 0xE0000 && u <= 0xE0FFF); // tags, variation selectors supplement
  }
}

/// One two-factor account: who it is for, and the secret and parameters that make its codes.
///
/// The secret is a byte array that [wipe] overwrites. Nothing here prints it: [toString] names the account and its parameters only.
class OtpAccount {
  /// The account keeps its own copy of [secret]; wipe the buffer you passed in yourself when you are done with it.
  OtpAccount({
    required String issuer,
    required String label,
    required Uint8List secret,
    this.algorithm = OtpAlgorithm.sha1,
    this.digits = 6,
    this.period = OtpLimits.defaultPeriod,
    this.type = OtpType.totp,
    this.counter = 0,
    this.id,
  })  : issuer = issuer.trim(),
        label = label.trim(),
        // Checked before the copy is made, so a refused account leaves no copy of the secret behind.
        _secret = _checkedCopy(
            secret, issuer.trim(), label.trim(), digits, period, counter);

  static Uint8List _checkedCopy(Uint8List secret, String issuer, String label,
      int digits, int period, int counter) {
    if (secret.length < OtpLimits.minSecretBytes) {
      throw ArgumentError('The secret is too short (at least 80 bits).');
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
    // The same name rules as a scanned link, so a rename cannot bring back what the parser refuses.
    if (label.isEmpty) throw ArgumentError('The account needs a name.');
    if (OtpLimits.hasUnsafeCharacters(issuer) ||
        OtpLimits.hasUnsafeCharacters(label)) {
      throw ArgumentError('The issuer or name has invisible or '
          'direction-changing characters.');
    }
    return Uint8List.fromList(secret);
  }

  /// The service, for example `GitHub`. May be empty.
  final String issuer;

  /// The account at the service, for example an email address.
  final String label;

  final Uint8List _secret;

  /// A read-only view of the secret: only [wipe] changes it. Showing or exporting it is for the vault and backup code to refuse (PRODUCT-DESCRIPTION.md, "Seeds are never shown again").
  Uint8List get secret => _secret.asUnmodifiableView();
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
  int get secretBits => _secret.length * 8;

  /// A copy with some fields changed (it has its own copy of the secret).
  OtpAccount copyWith({
    String? issuer,
    String? label,
    int? counter,
    String? id,
  }) {
    if (_wiped) throw StateError('This account was wiped from memory.');
    return OtpAccount(
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
  }

  bool _wiped = false;

  /// Whether [wipe] was called. A wiped account cannot make codes: the generator refuses it, so a redraw after a lock never shows a wrong code made from zeros.
  bool get isWiped => _wiped;

  /// Overwrites the secret in memory.
  void wipe() {
    _secret.fillRange(0, _secret.length, 0);
    _wiped = true;
  }

  /// The same credential: same secret and parameters. The names are not compared, because scanning one service's code twice under two spellings is still one account (the id and counter are not compared either, nor the period of a counter-based account, which it does not use).
  bool isDuplicateOf(OtpAccount other) {
    // Zeros say nothing about the account they were: a wiped one is never a duplicate of anything.
    if (_wiped || other._wiped) return false;
    if (algorithm != other.algorithm ||
        digits != other.digits ||
        (type == OtpType.totp && period != other.period) ||
        type != other.type ||
        _secret.length != other._secret.length) {
      return false;
    }
    // Compared without stopping at the first different byte.
    var diff = 0;
    for (var i = 0; i < _secret.length; i++) {
      diff |= _secret[i] ^ other._secret[i];
    }
    return diff == 0;
  }

  /// Names neither the secret nor the person: the issuer and label are usually an email address, and logs and crash reports must not carry one.
  @override
  String toString() => 'OtpAccount(${type.name}, ${algorithm.uriName}, '
      '$digits digits${id == null ? '' : ', id $id'})';
}
