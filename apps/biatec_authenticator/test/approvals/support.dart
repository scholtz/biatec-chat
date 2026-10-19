// Test support: a software P-256 key in place of the hardware keystore, and
// access to the consent-protocol vectors.
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:pointycastle/export.dart';

final _curve = ECCurve_secp256r1();

BigInt _int(String b64u) {
  final bytes = base64Url.decode(base64Url.normalize(b64u));
  return bytes.fold(BigInt.zero, (a, b) => (a << 8) | BigInt.from(b));
}

Uint8List _bytes32(BigInt v) {
  final out = Uint8List(32);
  for (var i = 31; i >= 0; i--) {
    out[i] = (v & BigInt.from(0xff)).toInt();
    v >>= 8;
  }
  return out;
}

ECPublicKey publicKeyOf(Map<String, Object?> jwk) => ECPublicKey(
    _curve.curve
        .createPoint(_int(jwk['x'] as String), _int(jwk['y'] as String)),
    _curve);

/// ES256 verify over [data] with a raw r||s [signature].
bool verifyEs256(
    Map<String, Object?> jwk, List<int> data, List<int> signature) {
  if (signature.length != 64) return false;
  final r = _int(base64Url.encode(signature.sublist(0, 32)));
  final s = _int(base64Url.encode(signature.sublist(32)));
  final verifier = Signer('SHA-256/ECDSA')
    ..init(false, PublicKeyParameter<ECPublicKey>(publicKeyOf(jwk)));
  return verifier.verifySignature(Uint8List.fromList(data), ECSignature(r, s));
}

/// A software key under one alias, signing like the hardware store.
class SoftwareKeyStore implements DeviceKeyStore {
  SoftwareKeyStore(this.jwk);

  /// Private JWK (with `d`).
  final Map<String, Object?> jwk;
  final List<Uint8List> signed = [];

  @override
  Future<DeviceKey> generate(String alias) async => (await load(alias))!;

  @override
  Future<DeviceKey?> load(String alias) async =>
      DeviceKey(kid: alias, x: jwk['x'] as String, y: jwk['y'] as String);

  @override
  Future<Uint8List> sign(String alias, Uint8List data) async {
    signed.add(data);
    final signer = Signer('SHA-256/DET-ECDSA')
      ..init(
          true,
          PrivateKeyParameter<ECPrivateKey>(
              ECPrivateKey(_int(jwk['d'] as String), _curve)));
    final sig = signer.generateSignature(data) as ECSignature;
    // Low-S is not required by ES256; keep r||s as produced.
    return Uint8List.fromList([..._bytes32(sig.r), ..._bytes32(sig.s)]);
  }

  @override
  Future<void> delete(String alias) async {}
}

Map<String, Object?> _readJson(String name) =>
    (jsonDecode(File('../../packages/consent-protocol/vectors/$name')
            .readAsStringSync()) as Map)
        .cast<String, Object?>();

List<Map<String, Object?>> vectors(String file) => [
      for (final v in _readJson(file)['vectors'] as List)
        (v as Map).cast<String, Object?>()
    ];

/// The test-only device key of vectors/keys.json (the mock's seeded devices
/// use it too).
Map<String, Object?> deviceTestKey() =>
    (_readJson('keys.json')['device'] as Map).cast<String, Object?>();
