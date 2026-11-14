// Test support: a software P-256 key in place of the hardware keystore, and
// access to the consent-protocol vectors.
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
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

/// Software P-256 keys in place of the hardware keystore: an approval key and a presence key, with the counters and switches the tests of the
/// two-key rules need. Like the real store, [signApproval] is the only call that "prompts", and it is counted.
class SoftwareKeyStore implements DeviceKeyStore {
  SoftwareKeyStore(this.jwk,
      {Map<String, Object?>? presenceJwk,
      this.approvalKid = 'alice-device',
      this.presenceKid = 'alice-device-presence'})
      : presenceJwk = presenceJwk ?? presenceTestKey();

  /// Private JWK (with `d`) of the approval key, and of the presence key.
  final Map<String, Object?> jwk;
  final Map<String, Object?> presenceJwk;
  final String approvalKid;
  final String presenceKid;

  /// Payloads signed by each key.
  final List<Uint8List> signed = [];
  final List<Uint8List> presenceSigned = [];

  /// Every approval prompt, as (title, subtitle).
  final List<(String, String)> prompts = [];

  /// Where this stand-in claims its keys live.
  KeySecurityLevel level = KeySecurityLevel.trustedEnvironment;

  /// The approval key reports that it is bound to the biometrics (false: a software key).
  bool authBound = true;

  /// What [keysState] answers.
  KeysState state = KeysState.ok;

  /// The next [signApproval] ends like a dismissed prompt (consumed by that call).
  bool cancelNextPrompt = false;

  DeviceKey _key(Map<String, Object?> k, String kid, {required bool bound}) =>
      DeviceKey(
          kid: kid,
          x: k['x'] as String,
          y: k['y'] as String,
          securityLevel: level,
          authBound: bound);

  @override
  Future<DeviceKeys> generateKeys() async => DeviceKeys(
      approval: _key(jwk, approvalKid, bound: authBound),
      presence: _key(presenceJwk, presenceKid, bound: false));

  @override
  Future<DeviceKey?> loadApproval() async =>
      _key(jwk, approvalKid, bound: authBound);

  @override
  Future<DeviceKey?> loadPresence() async =>
      _key(presenceJwk, presenceKid, bound: false);

  @override
  Future<KeysState> keysState() async => state;

  Uint8List _sign(Map<String, Object?> k, Uint8List data) {
    final signer = Signer('SHA-256/DET-ECDSA')
      ..init(
          true,
          PrivateKeyParameter<ECPrivateKey>(
              ECPrivateKey(_int(k['d'] as String), _curve)));
    final sig = signer.generateSignature(data) as ECSignature;
    // Low-S is not required by ES256; keep r||s as produced.
    return Uint8List.fromList([..._bytes32(sig.r), ..._bytes32(sig.s)]);
  }

  @override
  Future<Uint8List> signPresence(Uint8List data) async {
    presenceSigned.add(data);
    return _sign(presenceJwk, data);
  }

  @override
  Future<Uint8List> signApproval(Uint8List data,
      {required String promptTitle, required String promptSubtitle}) async {
    prompts.add((promptTitle, promptSubtitle));
    if (state == KeysState.approvalKeyInvalidated) {
      throw const ApprovalKeyInvalidated();
    }
    if (cancelNextPrompt) {
      cancelNextPrompt = false;
      throw const KeyPromptCancelled();
    }
    signed.add(data);
    return _sign(jwk, data);
  }

  @override
  Future<void> deleteKeys() async {}

  @override
  Future<void> deleteLegacyKey(String alias) async {}
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

/// The test-only presence key of vectors/keys.json (the mock's seeded devices use it too).
Map<String, Object?> presenceTestKey() =>
    (_readJson('keys.json')['presence'] as Map).cast<String, Object?>();

/// A new random private P-256 JWK (a stand-in for a key the keystore just made).
Map<String, Object?> randomPrivateJwk() {
  final random = Random.secure();
  var d = BigInt.zero;
  for (var i = 0; i < 32; i++) {
    d = (d << 8) | BigInt.from(random.nextInt(256));
  }
  d = d % (_curve.n - BigInt.one) + BigInt.one;
  final q = (_curve.G * d)!;
  String b64(BigInt v) => base64Url.encode(_bytes32(v)).replaceAll('=', '');
  return {
    'kty': 'EC',
    'crv': 'P-256',
    'x': b64(q.x!.toBigInteger()!),
    'y': b64(q.y!.toBigInteger()!),
    'd': b64(d),
  };
}

/// A software two-key store with fresh random keys and the kids the real store derives from them, for enrolling against the mock.
SoftwareKeyStore randomSoftwareKeyStore() {
  final approval = randomPrivateJwk();
  final presence = randomPrivateJwk();
  DeviceKey key(KeyUse use, Map<String, Object?> jwk) => DeviceKey.fromParts(
      use,
      x: jwk['x'] as String,
      y: jwk['y'] as String,
      securityLevel: KeySecurityLevel.trustedEnvironment);
  return SoftwareKeyStore(approval,
      presenceJwk: presence,
      approvalKid: key(KeyUse.approve, approval).kid,
      presenceKid: key(KeyUse.presence, presence).kid);
}
