import 'package:flutter/services.dart';

/// Proof from the platform that this is the genuine app on a genuine device: a Play Integrity token (Android) or an App Attest attestation (iOS). Biatec OIDC verifies it; this is the call the app makes when it enrolls.
class AttestationToken {
  const AttestationToken({required this.kind, required this.token, this.keyId});

  /// `play_integrity` or `app_attest`.
  final String kind;
  final String token;

  /// The App Attest key id the attestation belongs to, when there is one.
  final String? keyId;

  /// Names nothing: the token is a credential.
  @override
  String toString() => 'AttestationToken($kind)';
}

/// Asks the platform for an attestation bound to [nonce] (the server's challenge), so it cannot be replayed.
abstract class AttestationProvider {
  /// Null when the platform cannot attest (no Play services, an unsupported device, the simulator): the enrollment then goes without one and Biatec OIDC decides.
  Future<AttestationToken?> token(String nonce);
}

/// Over the channel `io.biatec.authenticator/attestation`: `token` takes `nonce` and answers `{kind, token, keyId?}`. iOS implements App Attest (`IntegrityChannel.swift`). The Android side needs the Play Integrity dependency and the cloud project number of the release, which the build supplies; until it does, it answers that it is not available.
class PlatformAttestationProvider implements AttestationProvider {
  const PlatformAttestationProvider(
      [this._channel =
          const MethodChannel('io.biatec.authenticator/attestation')]);

  final MethodChannel _channel;

  @override
  Future<AttestationToken?> token(String nonce) async {
    try {
      final raw = await _channel
          .invokeMethod<Map<Object?, Object?>>('token', {'nonce': nonce});
      final kind = raw?['kind'];
      final token = raw?['token'];
      if (kind is! String || token is! String || token.isEmpty) return null;
      final keyId = raw?['keyId'];
      return AttestationToken(
          kind: kind, token: token, keyId: keyId is String ? keyId : null);
    } on Object {
      return null;
    }
  }
}
