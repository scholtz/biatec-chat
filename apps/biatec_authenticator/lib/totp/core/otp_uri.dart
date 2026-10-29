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
/// The `issuer` setting names the issuer. A label prefix that names the same issuer, apart from case and spacing (real services differ in both), is dropped from the name; a prefix that names something else is kept as part of the name, so nothing the link says is lost. Setting names are read without case.
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

    // Names are compared without case, so `Secret` and `secret` count as the same setting given twice.
    final all = <String, List<String>>{};
    try {
      uri.queryParametersAll.forEach(
          (name, values) => (all[name.toLowerCase()] ??= []).addAll(values));
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
    // Everything else is checked before the secret is decoded, so no refusal leaves a decoded secret behind.
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
    if (periodText != null) {
      // Checked for both types, so a bad value is never let through; only a time-based account uses it.
      final p = _wholeNumber(periodText);
      if (p == null || p < OtpLimits.minPeriod || p > OtpLimits.maxPeriod) {
        throw const OtpUriException(OtpUriError.invalidPeriod,
            'A code must last between 15 and 120 seconds.');
      }
      if (type == OtpType.totp) period = p;
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
    final colon = decoded.indexOf(':');
    if (issuerParam.isEmpty) {
      if (colon >= 0) {
        issuer = decoded.substring(0, colon).trim();
        label = decoded.substring(colon + 1);
      }
    } else {
      issuer = issuerParam;
      // The prefix is dropped only when it names the same issuer (the usual "Issuer:account"); a different one is part of the name and kept, so "corp:alice" and "home:alice" stay apart.
      if (decoded.startsWith('$issuerParam:')) {
        // Checked whole first: the issuer itself may contain a colon.
        label = decoded.substring(issuerParam.length + 1);
      } else if (colon >= 0 &&
          _sameName(decoded.substring(0, colon), issuerParam)) {
        label = decoded.substring(colon + 1);
      }
    }
    label = label.trim();
    if (label.isEmpty) {
      throw const OtpUriException(
          OtpUriError.invalidName, 'This account has no name.');
    }
    for (final t in [issuer, label]) {
      if (OtpLimits.hasUnsafeCharacters(t) ||
          t.length > OtpLimits.maxTextLength) {
        throw const OtpUriException(
            OtpUriError.invalidName, 'The name in this link is not valid.');
      }
    }

    final Uint8List secret;
    try {
      secret = Base32.decode(secretText);
    } on Base32Exception {
      throw const OtpUriException(OtpUriError.invalidSecret,
          'The secret key in this link is not valid.');
    }
    try {
      if (secret.length < OtpLimits.minSecretBytes) {
        throw const OtpUriException(OtpUriError.weakSecret,
            'The secret key in this link is too short to be safe.');
      }
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
      // The account holds its own copy, and a refusal needs none.
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

  /// Digits only: no sign, no decimal point, no spaces.
  static final _digitsOnly = RegExp(r'^[0-9]{1,15}$');

  static int? _wholeNumber(String text) {
    if (!_digitsOnly.hasMatch(text)) return null;
    return int.parse(text);
  }

  /// Spacing, and `+`, which some services use for a space in the label (the path keeps it, the query turns it into a space).
  static final _spaces = RegExp(r'[\s+]+');

  /// Equal apart from case and spacing, as services write their own name.
  static bool _sameName(String a, String b) =>
      a.replaceAll(_spaces, '').toLowerCase() ==
      b.replaceAll(_spaces, '').toLowerCase();
}
