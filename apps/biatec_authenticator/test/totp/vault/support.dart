import 'dart:math';
import 'dart:typed_data';

import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:biatec_authenticator/totp/vault/vault_key_store.dart';
import 'package:biatec_authenticator/totp/vault/vault_storage.dart';
import 'package:pointycastle/export.dart';

/// An in-memory stand-in for the hardware key store, with real AES-256-GCM so a changed byte, a wrong associated data or a swapped record fails the way it does on a phone.
class FakeVaultKeyStore implements VaultKeyStore {
  FakeVaultKeyStore([Random? random]) : _random = random ?? Random.secure() {
    for (final p in VaultKeyPurpose.values) {
      _keys[p] = _bytes(32);
    }
  }

  final Random _random;
  final Map<VaultKeyPurpose, Uint8List> _keys = {};

  /// The phone's biometrics changed: every secret operation reports it.
  bool invalidated = false;

  /// The user cancels the prompt.
  bool denyAuth = false;

  /// How many biometric prompts were asked.
  int prompts = 0;

  /// The nonce of every sealed value.
  final List<String> nonces = [];

  /// The associated data of every open call, in order.
  final List<String> openedAad = [];

  /// The plaintext buffers the vault handed in, and the ones handed back, to check they are wiped.
  final List<Uint8List> sealedPlain = [];
  final List<Uint8List> openedPlain = [];

  Uint8List _bytes(int n) =>
      Uint8List.fromList(List<int>.generate(n, (_) => _random.nextInt(256)));

  void _auth(VaultKeyPurpose purpose) {
    if (purpose != VaultKeyPurpose.secret) return;
    prompts++;
    if (invalidated) throw const VaultBiometricsChanged();
    if (denyAuth) throw const VaultAuthFailed();
  }

  @override
  Future<Uint8List> seal(
      VaultKeyPurpose purpose, Uint8List plaintext, Uint8List aad) async {
    _auth(purpose);
    if (purpose == VaultKeyPurpose.secret) await sealGate;
    if (purpose == VaultKeyPurpose.secret) sealedPlain.add(plaintext);
    final nonce = _bytes(12);
    nonces.add(nonce.join(','));
    final cipher = GCMBlockCipher(AESEngine())
      ..init(
          true, AEADParameters(KeyParameter(_keys[purpose]!), 128, nonce, aad));
    return Uint8List.fromList([...nonce, ...cipher.process(plaintext)]);
  }

  @override
  Future<Uint8List> open(
      VaultKeyPurpose purpose, Uint8List sealed, Uint8List aad) async {
    _auth(purpose);
    openedAad.add(String.fromCharCodes(aad));
    if (sealed.length < 12 + 16) throw const VaultCorrupt('short');
    final cipher = GCMBlockCipher(AESEngine())
      ..init(
          false,
          AEADParameters(KeyParameter(_keys[purpose]!), 128,
              Uint8List.sublistView(sealed, 0, 12), aad));
    try {
      final plain = cipher.process(Uint8List.sublistView(sealed, 12));
      if (purpose == VaultKeyPurpose.secret) openedPlain.add(plain);
      return plain;
    } on InvalidCipherTextException {
      throw const VaultCorrupt('tag');
    }
  }

  /// When set, sealing a secret waits for it (a fingerprint prompt that is still open).
  Future<void>? sealGate;

  /// How many times the app told the key store it locked.
  int sessionsEnded = 0;

  @override
  Future<void> endSession() async => sessionsEnded++;

  @override
  Future<void> deleteKeys() async {
    _keys.clear();
    // The real store makes new keys on first use, and they are not tied to the old biometric set.
    invalidated = false;
    for (final p in VaultKeyPurpose.values) {
      _keys[p] = _bytes(32);
    }
  }
}

class MemoryVaultStorage implements VaultStorage {
  Uint8List? bytes;

  /// Makes every write fail.
  bool failWrites = false;

  @override
  Future<Uint8List?> read() async =>
      bytes == null ? null : Uint8List.fromList(bytes!);

  @override
  Future<void> write(Uint8List value) async {
    if (failWrites) throw StateError('disk full');
    bytes = Uint8List.fromList(value);
  }

  @override
  Future<void> delete() async => bytes = null;
}

class MemoryVersionStore implements VaultVersionStore {
  int version = 0;

  @override
  Future<int> read() async => version;

  @override
  Future<void> write(int v) async => version = v;
}

/// A valid account whose secret bytes are 20 distinct values starting at [seed].
OtpAccount account(
    {String issuer = 'GitHub',
    String label = 'alice@example.com',
    int seed = 1,
    OtpType type = OtpType.totp,
    int counter = 0}) {
  return OtpAccount(
    issuer: issuer,
    label: label,
    secret: Uint8List.fromList(
        List<int>.generate(20, (i) => seed * 7 + i * 11 + 3)),
    type: type,
    counter: counter,
  );
}
