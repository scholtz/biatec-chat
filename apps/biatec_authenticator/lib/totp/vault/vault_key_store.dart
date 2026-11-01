import 'package:flutter/services.dart';

/// Which hardware key a value is sealed under.
enum VaultKeyPurpose {
  /// Issuer, label and parameters: listed without a biometric, so the Codes tab can show the accounts before the user proves anything.
  metadata,

  /// The seed of one account: every use needs the user's strong biometric, and the key is invalidated when the set of fingerprints or faces changes.
  secret,
}

/// Why the vault refused. The text never holds a secret, a name or a key.
class VaultException implements Exception {
  const VaultException(this.message);
  final String message;

  @override
  String toString() => '$runtimeType: $message';
}

/// The biometric key was invalidated because the phone's biometrics changed (a finger or face was added or removed). Every read of a secret reports this; the vault file is left as it is, for a later restore.
class VaultBiometricsChanged extends VaultException {
  const VaultBiometricsChanged()
      : super('The phone\'s biometrics changed, so the vault key was '
            'invalidated.');
}

/// The user cancelled the biometric prompt, or it failed or is locked out. Nothing was read or written.
class VaultAuthFailed extends VaultException {
  const VaultAuthFailed() : super('The biometric check did not succeed.');
}

/// The vault file or one of its records is not what the vault wrote: changed, cut short, swapped, or an older version. Nothing from it is used.
class VaultCorrupt extends VaultException {
  const VaultCorrupt(super.message);
}

/// The file is an older version than the phone has seen (a restored copy).
class VaultRolledBack extends VaultCorrupt {
  const VaultRolledBack()
      : super('The vault file is older than a version already seen.');
}

/// Hardware-backed AES-256-GCM, used by the vault to seal values.
///
/// The keys never leave the keystore. [seal] returns an opaque sealed value (on Android `nonce || ciphertext || tag`, on iOS an ECIES message) made with a fresh nonce inside the platform, never one the caller chose; [aad] is authenticated with it, so a sealed value only opens with the same [aad] it was sealed with. Opening a [VaultKeyPurpose.secret] value (and sealing one) needs the user's biometric.
abstract class VaultKeyStore {
  Future<Uint8List> seal(
      VaultKeyPurpose purpose, Uint8List plaintext, Uint8List aad);

  /// Throws [VaultBiometricsChanged], [VaultAuthFailed] or [VaultCorrupt] (the tag did not match).
  Future<Uint8List> open(
      VaultKeyPurpose purpose, Uint8List sealed, Uint8List aad);

  /// Removes both keys, for a reset. The vault file can no longer be opened afterwards.
  Future<void> deleteKeys();
}

/// Talks to the native vault key code over a platform channel.
///
/// The native side (`VaultPlugin.kt`, `VaultPlugin.swift`) implements `seal`, `open` and `deleteKeys`, each `seal` and `open` taking `purpose` (`metadata` or `secret`), `data` and `aad`. It answers errors with the codes `biometrics_changed`, `auth_failed` and `bad_data`.
class PlatformVaultKeyStore implements VaultKeyStore {
  const PlatformVaultKeyStore(
      [this._channel = const MethodChannel('io.biatec.authenticator/vault')]);

  final MethodChannel _channel;

  Future<Uint8List> _call(String method, VaultKeyPurpose purpose,
      Uint8List data, Uint8List aad) async {
    try {
      final out = await _channel.invokeMethod<Uint8List>(
          method, {'purpose': purpose.name, 'data': data, 'aad': aad});
      if (out == null) {
        throw const VaultCorrupt('The keystore returned nothing.');
      }
      return out;
    } on PlatformException catch (e) {
      // The platform message is dropped: it can name keys.
      switch (e.code) {
        case 'biometrics_changed':
          throw const VaultBiometricsChanged();
        case 'auth_failed':
          throw const VaultAuthFailed();
        case 'bad_data':
          throw const VaultCorrupt('A sealed value did not verify.');
        default:
          throw const VaultException('The keystore could not do that.');
      }
    }
  }

  @override
  Future<Uint8List> seal(
          VaultKeyPurpose purpose, Uint8List plaintext, Uint8List aad) =>
      _call('seal', purpose, plaintext, aad);

  @override
  Future<Uint8List> open(
          VaultKeyPurpose purpose, Uint8List sealed, Uint8List aad) =>
      _call('open', purpose, sealed, aad);

  @override
  Future<void> deleteKeys() async {
    try {
      await _channel.invokeMethod('deleteKeys');
    } on PlatformException {
      throw const VaultException('The keystore could not do that.');
    }
  }
}
