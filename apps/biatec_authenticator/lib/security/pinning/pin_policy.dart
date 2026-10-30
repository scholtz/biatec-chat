import 'dart:typed_data';

import 'spki_pin.dart';

/// What the policy says about the certificate a server presented.
enum PinDecision {
  accepted,

  /// No pin is configured for the host: nothing unpinned is ever trusted.
  noPinsForHost,

  /// The certificate's public key is not one of the host's pins.
  pinMismatch,
  notYetValid,
  expired,

  /// The certificate could not be read.
  unreadable,
}

/// The public keys Biatec OIDC may present, per host. Checked against the leaf certificate the server sends (Dart's TLS stack shows no more than that),
/// together with its validity dates. The system's list of trusted certificate authorities is not used at all.
///
/// What that means: the trust is one static key per host. Hostnames in the certificate are not compared (the key is pinned to the host in this table, so
/// a certificate for another name is no use to anyone without the key), there is no revocation check (a compromised key is replaced by an app update,
/// which is why a backup key is mandatory), and a key listed for two hosts is accepted for both.
class PinPolicy {
  PinPolicy({
    required Map<String, Iterable<String>> pins,
    DateTime Function()? clock,
  })  : _clock = clock ?? DateTime.now,
        _pins = {
          for (final e in pins.entries)
            e.key.toLowerCase(): {
              for (final p in e.value)
                SpkiPin.parse(p) ??
                    (throw ArgumentError('Not a pin for ${e.key}.')),
            },
        };

  /// A policy that trusts no host.
  PinPolicy.none()
      : _pins = const {},
        _clock = DateTime.now;

  final Map<String, Set<String>> _pins;
  final DateTime Function() _clock;

  Set<String> pinsFor(String host) => _pins[host.toLowerCase()] ?? const {};

  bool get isEmpty => _pins.isEmpty;

  Iterable<String> get hosts => _pins.keys;

  /// Decides on the certificate [der] (valid from [notBefore] to [notAfter]) presented by [host].
  PinDecision check(
      String host, Uint8List der, DateTime notBefore, DateTime notAfter) {
    final pins = pinsFor(host);
    if (pins.isEmpty) return PinDecision.noPinsForHost;
    final String pin;
    try {
      pin = SpkiPin.fromCertificate(der);
    } on CertificateFormatException {
      return PinDecision.unreadable;
    }
    // Without stopping at the first pin that is different.
    var match = false;
    for (final p in pins) {
      if (_sameText(p, pin)) match = true;
    }
    if (!match) return PinDecision.pinMismatch;
    final now = _clock();
    if (now.isBefore(notBefore)) return PinDecision.notYetValid;
    if (now.isAfter(notAfter)) return PinDecision.expired;
    return PinDecision.accepted;
  }

  static bool _sameText(String a, String b) {
    if (a.length != b.length) return false;
    var diff = 0;
    for (var i = 0; i < a.length; i++) {
      diff |= a.codeUnitAt(i) ^ b.codeUnitAt(i);
    }
    return diff == 0;
  }
}
