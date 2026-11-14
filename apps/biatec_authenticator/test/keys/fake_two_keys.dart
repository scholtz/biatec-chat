// Test support: a fake two-key keystore that signs nothing real (64 bytes) and records what was asked of it. Tests that need real signatures use
// SoftwareKeyStore (test/approvals/support.dart).
import 'dart:convert';
import 'dart:typed_data';

import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:flutter/services.dart' show PlatformException;

class FakeTwoKeyStore implements DeviceKeyStore {
  FakeTwoKeyStore([this.level = KeySecurityLevel.trustedEnvironment, int seed = 0])
      : _n = seed;

  KeySecurityLevel level;

  /// The approval key reports user authentication bound.
  bool authBound = true;
  bool failGenerate = false;

  /// The plugin refuses with `insecure_key` / `no_biometrics`.
  bool pluginRefuses = false;
  bool noBiometrics = false;
  bool generationFailed = false;
  bool throwTypeError = false;
  bool cancelPrompt = false;
  KeysState state = KeysState.ok;

  DeviceKeys? keys;
  int _n;

  /// The approval kids that were generated, in order.
  final List<String> generated = [];
  int deleteCalls = 0;
  final List<String> legacyDeleted = [];
  int prompts = 0;
  int presenceSigns = 0;

  String _coord(int seed) =>
      base64Url.encode(List<int>.filled(32, seed)).replaceAll('=', '');

  @override
  Future<DeviceKeys> generateKeys() async {
    if (failGenerate) throw Exception('no secure hardware');
    if (pluginRefuses) {
      throw PlatformException(code: 'insecure_key', message: 'software');
    }
    if (noBiometrics) {
      throw PlatformException(code: 'no_biometrics', message: 'none');
    }
    if (generationFailed) {
      throw PlatformException(code: 'key_generation_failed', message: 'x');
    }
    if (throwTypeError) {
      final Object? bad = null;
      return DeviceKeys(
          approval: DeviceKey.fromParts(KeyUse.approve,
              x: bad as String, y: 'yy', securityLevel: level),
          presence: DeviceKey.fromParts(KeyUse.presence,
              x: 'xx', y: 'yy', securityLevel: level));
    }
    _n++;
    final made = DeviceKeys(
      approval: DeviceKey.fromParts(KeyUse.approve,
          x: _coord(_n), y: _coord(100 + _n), securityLevel: level, authBound: authBound),
      presence: DeviceKey.fromParts(KeyUse.presence,
          x: _coord(50 + _n), y: _coord(150 + _n), securityLevel: level),
    );
    generated.add(made.approval.kid);
    return keys = made;
  }

  @override
  Future<DeviceKey?> loadApproval() async => keys?.approval;

  @override
  Future<DeviceKey?> loadPresence() async => keys?.presence;

  @override
  Future<KeysState> keysState() async => keys == null && state == KeysState.ok
      ? KeysState.missing
      : state;

  @override
  Future<Uint8List> signPresence(Uint8List data) async {
    presenceSigns++;
    return Uint8List(64)..fillRange(0, 64, 7);
  }

  @override
  Future<Uint8List> signApproval(Uint8List data,
      {required String promptTitle, required String promptSubtitle}) async {
    prompts++;
    if (cancelPrompt) throw const KeyPromptCancelled();
    return Uint8List(64)..fillRange(0, 64, 9);
  }

  @override
  Future<void> deleteKeys() async {
    deleteCalls++;
    keys = null;
  }

  @override
  Future<void> deleteLegacyKey(String alias) async => legacyDeleted.add(alias);
}
