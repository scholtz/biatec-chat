import 'dart:convert';

import 'package:crypto/crypto.dart';

final _thumbprint = RegExp(r'^[A-Za-z0-9_-]{43}$');
final _nonce = RegExp(r'^[A-Za-z0-9_-]{16,128}$');

/// The six digit pairing code both phones show when a new device is enrolled (protocol 1.1, spec section 8.3, `pairingCode` in `scripts/lib.mjs`): the first
/// four bytes of `SHA-256(newApprovalThumbprint + "." + newPresenceThumbprint + "." + nonce)` as a big-endian unsigned integer, modulo 10^6, zero padded to six digits.
///
/// A human check, not a secret: the user compares the code on the new device with the one on the phone that activates it. The new device computes it from its OWN keys and
/// the nonce it enrolled with; the activating phone computes it from what Biatec OIDC lists for the pending device, so a server that swapped a key shows a different code.
///
/// The inputs have fixed shapes (two 43 character thumbprints and a nonce without a dot), so they cannot be split two ways; anything else throws [ArgumentError].
String pairingCode(
    String newApprovalThumbprint, String newPresenceThumbprint, String nonce) {
  if (!_thumbprint.hasMatch(newApprovalThumbprint) ||
      !_thumbprint.hasMatch(newPresenceThumbprint) ||
      !_nonce.hasMatch(nonce)) {
    throw ArgumentError('invalid pairing code input');
  }
  final digest = sha256.convert(
      utf8.encode('$newApprovalThumbprint.$newPresenceThumbprint.$nonce'));
  final b = digest.bytes;
  final n = (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
  return (n % 1000000).toString().padLeft(6, '0');
}

/// [code] as `123 456`, for reading aloud and comparing.
String spacedPairingCode(String code) =>
    code.length == 6 ? '${code.substring(0, 3)} ${code.substring(3)}' : code;

/// Whether [nonce] has the shape the pairing code needs (an enrollment challenge from a server that does not follow the protocol may not).
bool isPairingNonce(String nonce) => _nonce.hasMatch(nonce);
