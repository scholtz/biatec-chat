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

/// Which account rule a value broke.
enum OtpAccountProblem { weakSecret, digits, period, counter, name }

/// A value [OtpAccount] refuses. An [ArgumentError], with the rule it broke so callers (the link parser) can tell the user which. The message never contains the secret or the name.
class OtpAccountException extends ArgumentError {
  OtpAccountException(this.problem, String super.message);
  final OtpAccountProblem problem;
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

  /// The largest counter: 15 digits, what a link may carry, and below 2^53 so the counter is exact when compiled to JavaScript.
  static const maxCounter = 999999999999999;

  static bool validDigits(int d) => d >= minDigits && d <= maxDigits;
  static bool validPeriod(int p) => p >= minPeriod && p <= maxPeriod;
  static bool validCounter(int c) => c >= 0 && c <= maxCounter;

  /// The rules every code needs, for the account and the generator alike: a key of at least [minSecretBytes], [digits] in range, and a counter in range.
  static void checkCodeInputs(int secretLength, int digits, int counter) {
    if (secretLength < minSecretBytes) {
      throw OtpAccountException(
          OtpAccountProblem.weakSecret, weakSecretMessage);
    }
    if (!validDigits(digits)) {
      throw OtpAccountException(OtpAccountProblem.digits, digitsMessage);
    }
    if (!validCounter(counter)) {
      throw OtpAccountException(OtpAccountProblem.counter, counterMessage);
    }
  }

  // The messages are built from the limits, so they cannot go out of step with them.
  static const digitsMessage =
      'Codes must have $minDigits to $maxDigits digits.';
  static const periodMessage =
      'A code must last between $minPeriod and $maxPeriod seconds.';
  static const weakSecretMessage = 'The secret key is too short to be safe '
      '(at least ${minSecretBytes * 8} bits).';
  static const counterMessage = 'The counter must be a whole number from 0 '
      'to $maxCounter.';

  /// Unicode White_Space, the one idea of a space shared by Base32 (between key groups), names (turned into a plain space) and the issuer comparison.
  static bool isSpace(int u) =>
      (u >= 0x09 && u <= 0x0D) ||
      u == 0x20 ||
      u == 0x85 ||
      u == 0xA0 ||
      u == 0x1680 ||
      (u >= 0x2000 && u <= 0x200A) ||
      u == 0x2028 ||
      u == 0x2029 ||
      u == 0x202F ||
      u == 0x205F ||
      u == 0x3000;

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
        (u >= 0xD800 && u <= 0xDFFF) || // an unpaired surrogate
        // Every format character (general category Cf) as of Unicode 15.1:
        u == 0x00AD ||
        (u >= 0x0600 && u <= 0x0605) ||
        u == 0x061C ||
        u == 0x06DD ||
        u == 0x070F ||
        u == 0x0890 ||
        u == 0x0891 ||
        u == 0x08E2 ||
        u == 0x110BD ||
        u == 0x110CD ||
        (u >= 0x13430 && u <= 0x1345F) || // Egyptian hieroglyph format controls
        (u >= 0x200B && u <= 0x200F) || // zero width, direction marks
        (u >= 0x2028 && u <= 0x202E) || // line separators, embeddings
        (u >= 0x2060 && u <= 0x2064) || // word joiner, invisible operators
        (u >= 0x2066 && u <= 0x206F) || // isolates, deprecated format controls
        u == 0xFEFF ||
        (u >= 0xFFF9 && u <= 0xFFFC) || // annotation, object replacement
        (u >= 0x1BCA0 && u <= 0x1BCA3) || // shorthand format controls
        (u >= 0x1D173 && u <= 0x1D17A) || // musical formatting
        // Other characters that draw nothing:
        (u >= 0x180B && u <= 0x180F) || // Mongolian selectors, vowel separator
        u == 0x17B4 ||
        u == 0x17B5 || // Khmer invisible vowels
        u == 0x034F || // combining grapheme joiner
        u == 0x115F ||
        u == 0x1160 || // Hangul fillers
        u == 0x3164 ||
        u == 0xFFA0 ||
        u == 0x2800 || // braille blank
        (u >= 0x0334 && u <= 0x0338) || // combining overlays (strike-through)
        (u >= 0xE000 && u <= 0xF8FF) || // private use
        u >= 0xF0000 || // supplementary private use (and the tags above)
        (u >= 0xFDD0 && u <= 0xFDEF) || // noncharacters
        (u & 0xFFFE) == 0xFFFE || // U+FFFE and U+FFFF in every plane
        (u >= 0xFE00 && u <= 0xFE0F) || // variation selectors
        (u >= 0xE0000 && u <= 0xE0FFF); // tags, variation selectors supplement
  }
}

/// One two-factor account: who it is for, and the secret and parameters that make its codes.
///
/// The secret is a byte array that [wipe] overwrites. Nothing here prints it: [toString] names the account and its parameters only.
class OtpAccount {
  /// The account keeps its own copy of [secret]; wipe the buffer you passed in yourself when you are done with it.
  factory OtpAccount({
    required String issuer,
    required String label,
    required Uint8List secret,
    OtpAlgorithm algorithm = OtpAlgorithm.sha1,
    int digits = 6,
    int period = OtpLimits.defaultPeriod,
    OtpType type = OtpType.totp,
    int counter = 0,
    String? id,
  }) {
    // Normalised once, so the value checked is the value stored.
    final i = _plainSpaces(issuer).trim();
    final l = _plainSpaces(label).trim();
    return OtpAccount._(
      issuer: i,
      label: l,
      // Checked before the copy is made, so a refused account leaves no copy of the secret behind.
      secret: _checkedCopy(secret, i, l, digits, period, type, counter),
      algorithm: algorithm,
      digits: digits,
      period: period,
      type: type,
      counter: counter,
      id: id,
    );
  }

  OtpAccount._({
    required this.issuer,
    required this.label,
    required Uint8List secret,
    required this.algorithm,
    required this.digits,
    required this.period,
    required this.type,
    required this.counter,
    required this.id,
  }) : _secret = secret;

  /// Unicode spaces (no-break, thin, ideographic and the like) look like a plain space, so they become one: "Git", U+00A0, "Hub" and "Git Hub" are then the same name, not a look-alike.
  /// Spaces that are control or separator characters (a line break, say) are left alone, so the name check still refuses them.
  static String _plainSpaces(String s) => String.fromCharCodes(s.runes
      .map((u) => OtpLimits.isSpace(u) && !OtpLimits._isUnsafe(u) ? 0x20 : u));

  /// The one place the account rules are checked; the parser maps the [OtpAccountException] to its own error.
  static Uint8List _checkedCopy(Uint8List secret, String issuer, String label,
      int digits, int period, OtpType type, int counter) {
    OtpLimits.checkCodeInputs(secret.length, digits, counter);
    if (!OtpLimits.validPeriod(period)) {
      throw OtpAccountException(
          OtpAccountProblem.period, OtpLimits.periodMessage);
    }
    // Each type refuses the other's setting, which a link would not carry, so writing the account as a link and reading it back gives the same account.
    if (type == OtpType.totp && counter != 0) {
      throw OtpAccountException(OtpAccountProblem.counter,
          'A time-based account does not have a counter.');
    }
    if (type == OtpType.hotp && period != OtpLimits.defaultPeriod) {
      throw OtpAccountException(OtpAccountProblem.period,
          'A counter-based account does not have a period.');
    }
    if (label.isEmpty) {
      throw OtpAccountException(
          OtpAccountProblem.name, 'This account has no name.');
    }
    if (issuer.length > OtpLimits.maxTextLength ||
        label.length > OtpLimits.maxTextLength ||
        OtpLimits.hasUnsafeCharacters(issuer) ||
        OtpLimits.hasUnsafeCharacters(label)) {
      throw OtpAccountException(OtpAccountProblem.name,
          'The name is too long or has invisible or direction-changing characters.');
    }
    return Uint8List.fromList(secret);
  }

  /// The service, for example `GitHub`. May be empty.
  final String issuer;

  /// The account at the service, for example an email address.
  final String label;

  final Uint8List _secret;

  /// A read-only view of the secret: only [wipe] changes it. Throws once wiped, so nothing can save or use the zeros in place of the key. Showing or exporting it is for the vault and backup code to refuse (PRODUCT-DESCRIPTION.md, "Seeds are never shown again").
  Uint8List get secret {
    checkUsable();
    return _secret.asUnmodifiableView();
  }

  /// Whether every byte of the secret is zero, which [wipe] makes so. Tells nothing about the key itself; for tests and audits of the wipe.
  bool get isZeroed => _secret.every((b) => b == 0);
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
  ///
  /// [clearId] drops the vault id, for a copy the vault stores as a new account.
  OtpAccount copyWith({
    String? issuer,
    String? label,
    int? counter,
    String? id,
    bool clearId = false,
  }) {
    checkUsable();
    if (clearId && id != null) {
      throw ArgumentError('Give a new id or clear it, not both.');
    }
    return OtpAccount(
      issuer: issuer ?? this.issuer,
      label: label ?? this.label,
      secret: _secret,
      algorithm: algorithm,
      digits: digits,
      period: period,
      type: type,
      counter: counter ?? this.counter,
      id: clearId ? null : id ?? this.id,
    );
  }

  bool _wiped = false;

  /// Whether [wipe] was called. A wiped account cannot make codes: the generator refuses it, so a redraw after a lock never shows a wrong code made from zeros.
  bool get isWiped => _wiped;

  /// Throws once the account was wiped: everything that uses the secret (codes, links, copies) calls this first, so nothing makes a code or link from zeros.
  void checkUsable() {
    if (_wiped) {
      throw StateError('This account was wiped from memory (the app locked).');
    }
  }

  /// Overwrites the secret in memory.
  void wipe() {
    _secret.fillRange(0, _secret.length, 0);
    _wiped = true;
  }

  /// The same credential: same secret and parameters. The names are not compared, because scanning one service's code twice under two spellings is still one account (the id and counter are not compared either, nor the period of a counter-based account, which it does not use).
  bool isDuplicateOf(OtpAccount other) {
    // Zeros say nothing about the account they were, and answering "no" would let the same key be stored twice: a wiped account throws, so the caller reloads first.
    checkUsable();
    other.checkUsable();
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
