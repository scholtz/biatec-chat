import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:flutter/services.dart';

import 'key_security_level.dart';

/// What a device key is used for (protocol 1.1, `biatec_key_use`).
enum KeyUse {
  /// The approval key: bound to the operating system's biometric check for every signature. Signs `approve` receipts, settings changes and approvals of new devices.
  approve('approve'),

  /// The presence key: usable while the phone is unlocked, no prompt. Signs device proofs, `deny` and `code_failed` receipts.
  presence('presence');

  const KeyUse(this.wire);
  final String wire;
}

/// Public half of one device key, as the JWK the enrollment call sends.
class DeviceKey {
  const DeviceKey({
    required this.kid,
    required this.x,
    required this.y,
    required this.securityLevel,
    this.authBound = false,
  });

  /// The key's public half with a [kid] derived from it: a prefix for the use and the first 32 characters of the RFC 7638 thumbprint, so the
  /// kid is unique per key, stable across loads and needs no storage of its own.
  factory DeviceKey.fromParts(KeyUse use,
      {required String x,
      required String y,
      required KeySecurityLevel securityLevel,
      bool authBound = false}) {
    return DeviceKey(
      kid: 'biatec-${use.wire}-${jwkThumbprint(x, y).substring(0, 32)}',
      x: x,
      y: y,
      securityLevel: securityLevel,
      authBound: authBound,
    );
  }

  /// Identifier of the key on the server (the enrollment names it).
  final String kid;

  /// Base64url P-256 coordinates.
  final String x;
  final String y;

  /// Where the keystore says the private key lives (BCHAT-032).
  final KeySecurityLevel securityLevel;

  /// The keystore says the key needs the operating system's biometric check for every signature, and is invalidated when the biometrics change (read back from the key after it was created, never assumed). Only the approval key has it.
  final bool authBound;

  Map<String, String> toJwk() => {'kty': 'EC', 'crv': 'P-256', 'x': x, 'y': y};

  /// RFC 7638 thumbprint of the public key.
  String get thumbprint => jwkThumbprint(x, y);
}

/// RFC 7638 thumbprint of a P-256 public key: base64url of the SHA-256 of the JWK with its members in alphabetical order and no spaces.
String jwkThumbprint(String x, String y) {
  final json = '{"crv":"P-256","kty":"EC","x":${jsonEncode(x)},"y":${jsonEncode(y)}}';
  return base64Url.encode(sha256.convert(utf8.encode(json)).bytes).replaceAll('=', '');
}

/// Both device keys, created together (protocol 1.1, two keys per device).
class DeviceKeys {
  const DeviceKeys({required this.approval, required this.presence});

  final DeviceKey approval;
  final DeviceKey presence;
}

/// What the keystore says about this device's keys, without asking the user for anything.
enum KeysState {
  /// Both keys are there and usable.
  ok('ok'),

  /// The approval key was invalidated (a fingerprint or face was added, or the biometrics were removed), or is gone after it was made here. Approvals stop; Deny and the presence key still work. The device has to enroll again.
  approvalKeyInvalidated('approval_key_invalidated'),

  /// No keys (a new install, or one from before the two keys).
  missing('missing');

  const KeysState(this.wire);
  final String wire;

  /// An answer this app does not know is [missing]: it fails closed.
  static KeysState fromWire(Object? value) {
    for (final s in values) {
      if (s.wire == value) return s;
    }
    return missing;
  }
}

/// The user dismissed the biometric prompt (or pressed Cancel). Nothing was signed.
class KeyPromptCancelled implements Exception {
  const KeyPromptCancelled();
  @override
  String toString() => 'The biometric check was cancelled.';
}

/// The biometric prompt could not give a signature: [code] is `no_biometrics`, `locked_out`, `no_activity` or `auth_failed`.
class KeyPromptFailed implements Exception {
  const KeyPromptFailed(this.code, this.message);
  final String code;
  final String message;
  @override
  String toString() => message;
}

/// The approval key was invalidated by a change of the phone's biometrics (or is gone). The key can never sign again.
class ApprovalKeyInvalidated implements Exception {
  const ApprovalKeyInvalidated();
  static const message = 'The fingerprint or face settings on this phone '
      'changed, so approvals are switched off on this device. You can still '
      'deny requests. Enroll this device again to approve.';
  @override
  String toString() => message;
}

/// The two hardware-backed device keys (Secure Enclave on iOS, StrongBox or the TEE on Android).
///
/// The private keys are generated inside the keystore and are never readable: the interface has no way to export them, only to sign.
///
///  * The **approval key** is created so that the operating system itself requires a strong biometric for every signature and invalidates the key when the biometrics change. [signApproval] is the only call that prompts, and the prompt and the signature are one operation: nothing in this app can sign with this key without the prompt, whatever the Dart side believes. There is no fallback to a software key or to the device PIN.
///  * The **presence key** is usable while the phone is unlocked and never prompts ([signPresence]).
///
/// The level each key got is reported in [DeviceKey.securityLevel] and [DeviceKey.authBound]; enrollment refuses keys that are not in hardware and an approval key that is not bound.
abstract class DeviceKeyStore {
  /// Creates both key pairs, or returns the existing ones. Throws [PlatformException] with code `insecure_key` when the keystore could not make a hardware key (for the approval key: bound to the biometrics) and `no_biometrics` when no strong biometric is enrolled on the phone.
  Future<DeviceKeys> generateKeys();

  /// The existing approval key, or null when there is none (never made, or gone).
  Future<DeviceKey?> loadApproval();

  /// The existing presence key, or null when there is none.
  Future<DeviceKey?> loadPresence();

  /// Whether the keys are usable, without a prompt.
  Future<KeysState> keysState();

  /// Signs [data] (ES256, raw r||s) with the presence key. Never prompts.
  Future<Uint8List> signPresence(Uint8List data);

  /// Signs [data] (ES256, raw r||s) with the approval key. The operating system asks the user for a strong biometric for this one signature, with [promptTitle] and [promptSubtitle] on the prompt. Throws [KeyPromptCancelled] when the user dismisses it, [ApprovalKeyInvalidated] when the key is invalidated and [KeyPromptFailed] for anything else.
  Future<Uint8List> signApproval(Uint8List data,
      {required String promptTitle, required String promptSubtitle});

  /// Removes both keys (after a failed enrollment, a sign-out or an erase).
  Future<void> deleteKeys();

  /// Removes the single key of an install from before the two keys, by its alias.
  Future<void> deleteLegacyKey(String alias);
}

/// Talks to the native keystore code over a platform channel.
///
/// The native side (`KeystorePlugin` for Android and iOS) implements `generateKeys`, `load` (argument `use`), `keysState`, `signPresence`, `signApproval` (arguments `data`, `title`, `subtitle`), `deleteKeys` and `delete` (argument `alias`, for the single key of old installs). A key is `{x, y, securityLevel, authBound}` (coordinates base64url, level as in [KeySecurityLevel.wire]).
class PlatformDeviceKeyStore implements DeviceKeyStore {
  const PlatformDeviceKeyStore(
      [this._channel =
          const MethodChannel('io.biatec.authenticator/keystore')]);

  final MethodChannel _channel;

  DeviceKey _parse(Object? raw, KeyUse use) {
    try {
      final map = (raw as Map).cast<String, Object?>();
      return DeviceKey.fromParts(
        use,
        x: map['x'] as String,
        y: map['y'] as String,
        securityLevel: KeySecurityLevel.fromWire(map['securityLevel']),
        // Only an explicit true counts, and only the approval key can be bound.
        authBound: use == KeyUse.approve && map['authBound'] == true,
      );
    } on Object {
      throw const FormatException('The keystore returned malformed key data.');
    }
  }

  @override
  Future<DeviceKeys> generateKeys() async {
    final raw = await _channel.invokeMethod<Object?>('generateKeys');
    try {
      final map = (raw as Map).cast<String, Object?>();
      return DeviceKeys(
        approval: _parse(map['approval'], KeyUse.approve),
        presence: _parse(map['presence'], KeyUse.presence),
      );
    } on FormatException {
      rethrow;
    } on Object {
      throw const FormatException('The keystore returned malformed key data.');
    }
  }

  Future<DeviceKey?> _load(KeyUse use) async {
    final raw = await _channel.invokeMethod<Object?>('load', {'use': use.wire});
    return raw == null ? null : _parse(raw, use);
  }

  @override
  Future<DeviceKey?> loadApproval() => _load(KeyUse.approve);

  @override
  Future<DeviceKey?> loadPresence() => _load(KeyUse.presence);

  @override
  Future<KeysState> keysState() async => KeysState.fromWire(
      await _channel.invokeMethod<Object?>('keysState'));

  @override
  Future<Uint8List> signPresence(Uint8List data) async {
    final raw =
        await _channel.invokeMethod<Uint8List>('signPresence', {'data': data});
    return raw!;
  }

  @override
  Future<Uint8List> signApproval(Uint8List data,
      {required String promptTitle, required String promptSubtitle}) async {
    try {
      final raw = await _channel.invokeMethod<Uint8List>('signApproval',
          {'data': data, 'title': promptTitle, 'subtitle': promptSubtitle});
      return raw!;
    } on PlatformException catch (e) {
      switch (e.code) {
        case 'cancelled':
          throw const KeyPromptCancelled();
        case 'approval_key_invalidated':
          throw const ApprovalKeyInvalidated();
        case 'no_biometrics':
        case 'locked_out':
        case 'no_activity':
        case 'auth_failed':
          throw KeyPromptFailed(e.code, e.message ?? 'The biometric check did not succeed.');
      }
      rethrow;
    }
  }

  @override
  Future<void> deleteKeys() => _channel.invokeMethod('deleteKeys');

  @override
  Future<void> deleteLegacyKey(String alias) =>
      _channel.invokeMethod('delete', {'alias': alias});
}
