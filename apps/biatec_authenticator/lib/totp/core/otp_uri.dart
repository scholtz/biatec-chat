import 'dart:convert';
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

  /// A setting's percent-encoding is broken (not UTF-8, say), so it cannot be read at all.
  invalidEncoding,
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
/// A `counter` on a time-based link is refused (it is most likely a counter-based account mislabelled). A `period` on a counter-based link must still be a valid period, so no malformed value is let through, but it is not used, since it changes nothing.
///
/// The `issuer` setting names the issuer; without one, the label prefix does, read as written (a "+" in a path is a plus). A label prefix that names the same issuer as the setting, apart from case and spacing (real services differ in both, and some write a space in the path as a literal "+"), is dropped from the name; a prefix that names something else is kept as part of the name, so nothing the link says is lost. A link with an issuer but no account name gets the issuer as its name, as other authenticator apps do. Setting names are read without case.
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
    const tooLong = OtpUriException(
        OtpUriError.tooLong, 'This link is too long to be a setup link.');
    // Refused before anything is copied, with room for whitespace around a pasted link.
    if (link.length > 2 * maxLength) throw tooLong;
    final text = link.trim();
    if (text.length > maxLength) throw tooLong;
    const notOtpAuth = OtpUriException(
        OtpUriError.notOtpAuth, 'This is not a setup link for a code.');
    final Uri uri;
    try {
      uri = Uri.parse(text);
    } on FormatException {
      throw notOtpAuth;
    }
    if (uri.scheme.toLowerCase() != 'otpauth') throw notOtpAuth;
    // The label is read from the raw text; refuse anything (user info, a port) that would let that reading and Uri.parse disagree on where the type ends.
    final raw = _rawParts(text);
    if (uri.userInfo.isNotEmpty ||
        uri.hasPort ||
        raw.authority.toLowerCase() != uri.host.toLowerCase()) {
      throw notOtpAuth;
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
    } on FormatException {
      // Bad percent-encoding in a setting (invalid UTF-8, say); which one is not told, since it may be the secret.
      throw const OtpUriException(OtpUriError.invalidEncoding,
          'This link has a setting that cannot be read.');
    }
    for (final name in _single) {
      if ((all[name]?.length ?? 0) > 1) {
        throw const OtpUriException(OtpUriError.duplicateParameter,
            'This link sets the same value twice.');
      }
    }
    String? param(String name) => all[name]?.first;

    const noSecret = OtpUriException(
        OtpUriError.missingSecret, 'This link has no secret key.');
    final secretText = param('secret');
    if (secretText == null) throw noSecret;
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
      if (d == null || !OtpLimits.validDigits(d)) {
        throw const OtpUriException(
            OtpUriError.invalidDigits, OtpLimits.digitsMessage);
      }
      digits = d;
    }

    var period = OtpLimits.defaultPeriod;
    final periodText = param('period');
    if (periodText != null) {
      // Checked for both types, so a bad value is never let through; only a time-based account uses it.
      final p = _wholeNumber(periodText);
      if (p == null || !OtpLimits.validPeriod(p)) {
        throw const OtpUriException(
            OtpUriError.invalidPeriod, OtpLimits.periodMessage);
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

    // The label is "Issuer:account" or just "account", percent-encoded. A "+" stays a "+" (RFC 3986 paths, and plus-addressed emails such as a+b@example.com depend on it); only the query turns "+" into a space.
    final decoded = _label(raw.label);
    var issuer = '';
    var label = decoded;
    final issuerParam = param('issuer')?.trim() ?? '';
    if (issuerParam.isEmpty) {
      // No issuer setting: the path is all there is, read as written.
      final colon = decoded.indexOf(':');
      if (colon >= 0) {
        issuer = decoded.substring(0, colon).trim();
        label = decoded.substring(colon + 1);
      }
    } else {
      // The issuer setting names the issuer. The prefix is dropped only when it names the same one (the usual "Issuer:account"), apart from case and spacing; a different one is part of the name and kept, so "corp:alice" and "home:alice" stay apart. Every colon is tried, since the issuer itself may contain one.
      issuer = issuerParam;
      final want = _nameKey(issuerParam);
      // Some services write a space in the path as a literal "+" (the issuer setting was already form-decoded), so the prefix matches as written or with those read as spaces. Both readings have their colons in the same places.
      final spaced = _label(raw.label, plusAsSpace: true);
      for (var i = decoded.indexOf(':');
          i >= 0;
          i = decoded.indexOf(':', i + 1)) {
        if (_nameKey(decoded.substring(0, i)) == want ||
            _nameKey(spaced.substring(0, i)) == want) {
          label = decoded.substring(i + 1);
          break;
        }
      }
    }
    if (label.trim().isEmpty && issuer.isNotEmpty) label = issuer;
    final Uint8List secret;
    try {
      secret = Base32.decode(secretText);
    } on Base32Exception catch (e) {
      // Only spaces, dashes or padding: there is no key, which is not the same as a broken one.
      if (e.empty) throw noSecret;
      // Valid characters, too few to make a byte: as weak as any key under the floor.
      if (e.tooShort) {
        throw const OtpUriException(
            OtpUriError.weakSecret, OtpLimits.weakSecretMessage);
      }
      throw const OtpUriException(OtpUriError.invalidSecret,
          'The secret key in this link is not valid.');
    }
    try {
      // The account checks the rest of its rules (key strength, name); its refusal is told as this link's error.
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
    } on OtpAccountException catch (e) {
      throw OtpUriException(
          switch (e.problem) {
            OtpAccountProblem.weakSecret => OtpUriError.weakSecret,
            OtpAccountProblem.digits => OtpUriError.invalidDigits,
            OtpAccountProblem.period => OtpUriError.invalidPeriod,
            OtpAccountProblem.counter => OtpUriError.invalidCounter,
            OtpAccountProblem.name => OtpUriError.invalidName,
          },
          e.message as String);
    } finally {
      // The account holds its own copy, and a refusal needs none.
      secret.fillRange(0, secret.length, 0);
    }
  }

  /// The link for [account], with every setting explicit. Contains the secret: handle the result as one.
  static String write(OtpAccount account) {
    // A wiped account's zeros would make a link that looks valid but never matches the service.
    account.checkUsable();
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

  /// The authority and the label of [text] exactly as written (the label without its leading "/"). `Uri.path` would drop "." and ".." segments, turning "corp/../alice" into "alice"; the caller checks that the authority read here is the host Uri.parse found, so both read the same slice of the link.
  static ({String authority, String label}) _rawParts(String text) {
    final start = text.indexOf('://') + 3;
    var end = text.length;
    for (final c in const ['?', '#']) {
      final i = text.indexOf(c, start);
      if (i >= 0 && i < end) end = i;
    }
    final slash = text.indexOf('/', start);
    if (slash < 0 || slash >= end) {
      return (authority: text.substring(start, end), label: '');
    }
    return (
      authority: text.substring(start, slash),
      label: text.substring(slash + 1, end)
    );
  }

  /// The decoded label. The raw text is first encoded the way Uri.parse would (a stray "%" read as itself, raw non-ASCII and spaces percent-encoded), so a QR code with "Café" in it decodes like its encoded form instead of failing. With [plusAsSpace], a literal "+" (not an escaped %2B) is read as a space, as some services mean it.
  static String _label(String rawLabel, {bool plusAsSpace = false}) {
    final raw = rawLabel.replaceAll(_strayPercent, '%25');
    // Uri.encodeFull would also escape the "%" of real escapes, so only what cannot stand raw in a URI is encoded here.
    final encoded = StringBuffer();
    for (final r in raw.runes) {
      if (plusAsSpace && r == 0x2B) {
        encoded.write('%20');
      } else if (r > 0x20 && r < 0x7F) {
        encoded.writeCharCode(r);
      } else {
        for (final b in utf8.encode(String.fromCharCode(r))) {
          encoded.write('%${b.toRadixString(16).padLeft(2, '0')}');
        }
      }
    }
    try {
      return Uri.decodeComponent(encoded.toString());
    } on FormatException {
      // Escapes that are not UTF-8.
      throw const OtpUriException(
          OtpUriError.invalidName, 'The name in this link is not valid.');
    }
  }

  static final _strayPercent = RegExp(r'%(?![0-9A-Fa-f]{2})');

  /// Digits only: no sign, no decimal point, no spaces.
  static final _digitsOnly = RegExp(r'^[0-9]{1,15}$');

  static int? _wholeNumber(String text) {
    if (!_digitsOnly.hasMatch(text)) return null;
    return int.parse(text);
  }

  /// Spacing only: a "+" is a real character here ("C++").
  static final _spaces = RegExp(r'\s+');

  /// A name without case or spacing, as services write their own name differently in both.
  static String _nameKey(String name) =>
      name.replaceAll(_spaces, '').toLowerCase();
}
