import 'dart:convert';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

/// A certificate that cannot be read.
class CertificateFormatException implements Exception {
  const CertificateFormatException(this.message);
  final String message;

  @override
  String toString() => 'CertificateFormatException: $message';
}

/// A public key pin: the SHA-256 of a certificate's SubjectPublicKeyInfo, in Base64 (the form of HPKP, Android's network security config and iOS
/// `NSPinnedDomains`). Pinning the key, not the certificate, lets the certificate be renewed with the same key.
abstract final class SpkiPin {
  /// The pin of the certificate [der] (an X.509 certificate in DER form).
  static String fromCertificate(Uint8List der) =>
      base64.encode(sha256.convert(extractSpki(der)).bytes);

  /// The SubjectPublicKeyInfo of [der], as the bytes of its whole DER element.
  static Uint8List extractSpki(Uint8List der) {
    // Certificate ::= SEQUENCE { tbsCertificate SEQUENCE { [0] version OPTIONAL, serialNumber, signature,
    //                                                      issuer, validity, subject, subjectPublicKeyInfo, ... }, ... }
    final cert = _read(der, 0);
    if (cert.tag != 0x30) {
      throw const CertificateFormatException('Not a certificate.');
    }
    final tbs = _read(der, cert.contentStart);
    if (tbs.tag != 0x30) {
      throw const CertificateFormatException('No certificate body.');
    }
    var at = tbs.contentStart;
    final first = _read(der, at);
    if (first.tag == 0xA0) at = first.end; // the explicit version
    // serialNumber, signature, issuer, validity, subject
    for (var i = 0; i < 5; i++) {
      at = _read(der, at).end;
    }
    final spki = _read(der, at);
    if (spki.tag != 0x30) {
      throw const CertificateFormatException(
          'No public key in the certificate.');
    }
    return Uint8List.sublistView(der, spki.start, spki.end);
  }

  /// A pin as written in configuration: `sha256/BASE64`, or the bare Base64, of 32 bytes. Returns the Base64 in its standard form, or null when [text] is not a pin.
  static String? parse(String text) {
    var t = text.trim();
    if (t.startsWith('sha256/')) t = t.substring(7);
    try {
      final bytes = base64.decode(base64.normalize(t));
      if (bytes.length != 32) return null;
      return base64.encode(bytes);
    } on FormatException {
      return null;
    }
  }

  static _Element _read(Uint8List b, int at) {
    if (at < 0 || at + 2 > b.length) {
      throw const CertificateFormatException('The certificate is cut short.');
    }
    final tag = b[at];
    var p = at + 1;
    var len = b[p++];
    if (len & 0x80 != 0) {
      final n = len & 0x7F;
      // Lengths of 1 to 4 bytes cover any real certificate.
      if (n == 0 || n > 4 || p + n > b.length) {
        throw const CertificateFormatException(
            'A length in the certificate is not valid.');
      }
      len = 0;
      for (var i = 0; i < n; i++) {
        len = (len << 8) | b[p++];
      }
    }
    if (p + len > b.length) {
      throw const CertificateFormatException('The certificate is cut short.');
    }
    return _Element(tag, at, p, p + len);
  }
}

class _Element {
  const _Element(this.tag, this.start, this.contentStart, this.end);
  final int tag;
  final int start;
  final int contentStart;
  final int end;
}
