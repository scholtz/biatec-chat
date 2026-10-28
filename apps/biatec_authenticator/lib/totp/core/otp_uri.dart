import 'dart:typed_data';

import 'base32.dart';
import 'otp_account.dart';

/// Why an `otpauth://` link was refused. Each is a different thing for the user to fix.
enum OtpUriError {
  /// Not an `otpauth://` link at all (a web address in a QR code, say).
  notOtpAuth,

  /// `otpauth://something-else/...`: only `totp` and `hotp` exist.
  unsupportedType,

  /// The link is far longer than any real one.
  tooLong,
  missingSecret,
  invalidSecret,

  /// A secret under 80 bits.
  weakSecret,
  invalidAlgorithm,
  invalidDigits,
  invalidPeriod,

  /// A counter-based account without its starting counter.
  missingCounter,
  invalidCounter,

  /// A time-based account with a counter: the link is for something else.
  unexpectedCounter,

  /// The same setting twice with a chance to disagree, for example two secrets.
  duplicateParameter,

  /// The name has control or direction-changing characters, which can make one account look like another.
  invalidName,
}

class OtpUriException implements Exception {
  const OtpUriException(this.error, this.message);
  final OtpUriError error;

  /// For the user. Never contains the link or the secret.
  final String message;

  @override
  String toString() => 'OtpUriException(${error.name}): $message';
}

/// Reads and writes the `otpauth://` Key URI format that services show as a QR code.
///
/// A `counter` on a time-based link is refused (it is most likely a counter-based account mislabelled); a `period` on a counter-based link is ignored, since it changes nothing.
///
/// When the label has an `Issuer:` prefix and the `issuer` setting says something else, the setting is used and the prefix dropped. The format asks for both to agree, but real services differ in case and spacing, so a difference is not an error.
abstract final class OtpUri {
  /// A real link is a few hundred characters; this stops a hostile QR code from making the parser work on megabytes.
  static const maxLength = 2048;

  static const _single = {
    'secret',
    'issuer',
    'algorithm',
    'digits',
    'period',
    'counter',
  };

  static OtpAccount parse(String link) {
    final text = link.trim();
    if (text.length > maxLength) {
      throw const OtpUriException(
          OtpUriError.tooLong, 'This link is too long to be a setup link.');
    }
    final Uri uri;
    try {
      uri = Uri.parse(text);
    } on FormatException {
      throw const OtpUriException(
          OtpUriError.notOtpAuth, 'This is not a setup link for a code.');
    }
    if (uri.scheme.toLowerCase() != 'otpauth') {
      throw const OtpUriException(
          OtpUriError.notOtpAuth, 'This is not a setup link for a code.');
    }
    final OtpType type;
    switch (uri.host.toLowerCase()) {
      case 'totp':
        type = OtpType.totp;
      case 'hotp':
        type = OtpType.hotp;
      default:
        throw const OtpUriException(OtpUriError.unsupportedType,
            'Only time-based and counter-based codes are supported.');
    }

    final Map<String, List<String>> all;
    try {
      all = uri.queryParametersAll;
    } on Object {
      // Bad percent-encoding in a value (invalid UTF-8, say).
      throw const OtpUriException(
          OtpUriError.invalidName, 'This link has text that is not valid.');
    }
    for (final name in _single) {
      if ((all[name]?.length ?? 0) > 1) {
        throw const OtpUriException(OtpUriError.duplicateParameter,
            'This link sets the same value twice.');
      }
    }
    String? param(String name) => all[name]?.first;

    final secretText = param('secret');
    if (secretText == null || secretText.trim().isEmpty) {
      throw const OtpUriException(
          OtpUriError.missingSecret, 'This link has no secret key.');
    }
    final Uint8List secret;
    try {
      secret = Base32.decode(secretText);
    } on Base32Exception {
      throw const OtpUriException(OtpUriError.invalidSecret,
          'The secret key in this link is not valid.');
    }

    if (secret.length < OtpLimits.minSecretBytes) {
      secret.fillRange(0, secret.length, 0);
      throw const OtpUriException(OtpUriError.weakSecret,
          'The secret key in this link is too short to be safe.');
    }

    var algorithm = OtpAlgorithm.sha1;
    final algorithmText = param('algorithm');
    if (algorithmText != null) {
      final a = OtpAlgorithm.fromUriName(algorithmText);
      if (a == null) {
        throw const OtpUriException(OtpUriError.invalidAlgorithm,
            'This link uses a hash that is not supported (SHA1, SHA256 and SHA512 are).');
      }
      algorithm = a;
    }

    var digits = 6;
    final digitsText = param('digits');
    if (digitsText != null) {
      final d = _wholeNumber(digitsText);
      if (d == null || d < OtpLimits.minDigits || d > OtpLimits.maxDigits) {
        throw const OtpUriException(
            OtpUriError.invalidDigits, 'Codes must have 6 to 8 digits.');
      }
      digits = d;
    }

    var period = OtpLimits.defaultPeriod;
    final periodText = param('period');
    if (periodText != null && type == OtpType.totp) {
      final p = _wholeNumber(periodText);
      if (p == null || p < OtpLimits.minPeriod || p > OtpLimits.maxPeriod) {
        throw const OtpUriException(OtpUriError.invalidPeriod,
            'A code must last between 15 and 120 seconds.');
      }
      period = p;
    }

    var counter = 0;
    final counterText = param('counter');
    if (type == OtpType.hotp) {
      if (counterText == null) {
        throw const OtpUriException(OtpUriError.missingCounter,
            'A counter-based account needs its starting counter.');
      }
      final c = _wholeNumber(counterText);
      if (c == null) {
        throw const OtpUriException(OtpUriError.invalidCounter,
            'The counter in this link is not valid.');
      }
      counter = c;
    } else if (counterText != null) {
      throw const OtpUriException(OtpUriError.unexpectedCounter,
          'A time-based account does not have a counter.');
    }

    // The label is "Issuer:account" or just "account", percent-encoded.
    var path = uri.path;
    if (path.startsWith('/')) path = path.substring(1);
    String decoded;
    try {
      decoded = Uri.decodeComponent(path);
    } on Object {
      throw const OtpUriException(
          OtpUriError.invalidName, 'The name in this link is not valid.');
    }
    var issuer = '';
    var label = decoded;
    final issuerParam = param('issuer')?.trim() ?? '';
    if (issuerParam.isNotEmpty && decoded.startsWith('$issuerParam:')) {
      // The usual case: the label is "Issuer:account" and the issuer setting says the same.
      issuer = issuerParam;
      label = decoded.substring(issuerParam.length + 1);
    } else {
      final colon = decoded.indexOf(':');
      if (colon >= 0) {
        issuer = decoded.substring(0, colon).trim();
        label = decoded.substring(colon + 1).trim();
      }
      // The issuer setting wins over a different prefix in the label.
      if (issuerParam.isNotEmpty) issuer = issuerParam;
    }
    label = label.trim();
    if (label.isEmpty) {
      throw const OtpUriException(
          OtpUriError.invalidName, 'This account has no name.');
    }
    for (final t in [issuer, label]) {
      if (_hasUnsafeCharacters(t) || t.length > OtpLimits.maxTextLength) {
        throw const OtpUriException(
            OtpUriError.invalidName, 'The name in this link is not valid.');
      }
    }

    try {
      return OtpAccount(
        issuer: issuer,
        label: label,
        secret: secret,
        algorithm: algorithm,
        digits: digits,
        period: period,
        type: type,
        counter: counter,
      );
    } finally {
      // The account holds its own copy.
      secret.fillRange(0, secret.length, 0);
    }
  }

  /// The link for [account], with every setting explicit. Contains the secret: handle the result as one.
  static String write(OtpAccount account) {
    // With no issuer, a colon inside the name would be read as the separator, so a leading colon says "no issuer".
    final label = account.issuer.isEmpty
        ? (account.label.contains(':')
            ? ':${Uri.encodeComponent(account.label)}'
            : Uri.encodeComponent(account.label))
        : '${Uri.encodeComponent(account.issuer)}:'
            '${Uri.encodeComponent(account.label)}';
    final params = <String>[
      'secret=${Base32.encode(account.secret)}',
      if (account.issuer.isNotEmpty)
        'issuer=${Uri.encodeQueryComponent(account.issuer)}',
      'algorithm=${account.algorithm.uriName}',
      'digits=${account.digits}',
      if (account.type == OtpType.totp) 'period=${account.period}',
      if (account.type == OtpType.hotp) 'counter=${account.counter}',
    ];
    return 'otpauth://${account.type.name}/$label?${params.join('&')}';
  }

  static int? _wholeNumber(String text) {
    // Digits only: no sign, no decimal point, no spaces.
    if (!RegExp(r'^[0-9]{1,15}$').hasMatch(text)) return null;
    return int.parse(text);
  }

  /// Control characters, invisible characters, and the characters that reorder text: any of them can make two different names look the same or make "evil.com" display as something else.
  static bool _hasUnsafeCharacters(String text) {
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
        u == 0x180E ||
        (u >= 0x200B && u <= 0x200F) || // zero width, direction marks
        (u >= 0x2028 && u <= 0x202E) || // line separators, embeddings
        (u >= 0x2060 && u <= 0x2064) || // word joiner, invisible operators
        (u >= 0x2066 && u <= 0x2069) || // isolates
        u == 0xFEFF ||
        (u >= 0xFFF9 && u <= 0xFFFB) ||
        u == 0x034F || // combining grapheme joiner
        u == 0x115F ||
        u == 0x1160 || // Hangul fillers
        u == 0x3164 ||
        u == 0xFFA0 ||
        u == 0x2800 || // braille blank
        (u >= 0xFE00 && u <= 0xFE0F) || // variation selectors
        (u >= 0xE0000 && u <= 0xE007F); // tag characters
  }
}
