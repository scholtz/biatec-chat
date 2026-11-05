import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';
import '../lock/lock_controller.dart';
import '../security/secure_storage.dart';
import '../totp/vault/vault_key_store.dart';
import '../totp/vault/vault_storage.dart';

/// Removes everything the app keeps in its secure storage (the keychain on iOS, encrypted preferences on Android): the enrollment, the history, the preferences, the vault's version counter.
abstract class SecureWipe {
  Future<void> deleteAll();
}

class DeviceBoundSecureWipe implements SecureWipe {
  const DeviceBoundSecureWipe([this._storage = deviceBoundStorage]);

  final FlutterSecureStorage _storage;

  @override
  Future<void> deleteAll() => _storage.deleteAll();
}

/// The result of an erase: what could not be removed, in words. Empty when everything went.
class EraseResult {
  const EraseResult(this.failed);

  final List<String> failed;

  bool get complete => failed.isEmpty;
}

/// "Erase everything on this phone": after a fresh biometric, deletes the two-factor accounts and their hardware keys, the device key and the enrollment, the history and every preference, then signs the device out.
///
/// Every step runs even when an earlier one fails, so a broken keystore does not leave the rest behind; the result says what failed. The enrollment is cleared last, so the app only moves to the enrollment screen once the rest is gone. This removes data on the phone only: Biatec OIDC keeps its record of the device until it is cancelled from the account.
class PhoneEraser {
  PhoneEraser({
    required this.lock,
    required this.vaultKeys,
    required this.vaultStorage,
    required this.deviceKeys,
    required this.enrollment,
    required this.secure,
    this.stepTimeout = const Duration(seconds: 15),
  });

  final LockController lock;
  final VaultKeyStore vaultKeys;
  final VaultStorage vaultStorage;
  final DeviceKeyStore deviceKeys;
  final EnrollmentStore enrollment;
  final SecureWipe secure;

  /// How long one step may take. A keystore that never answers must not hold the whole erase (and the screen) for good: the step counts as failed.
  final Duration stepTimeout;

  /// Null when the fresh biometric was refused (nothing was removed); otherwise what happened.
  Future<EraseResult?> erase() async {
    if (!await lock.reauthenticate('Erase everything on this phone')) {
      return null;
    }
    final failed = <String>[];
    Future<void> step(String what, Future<void> Function() run) async {
      try {
        await run().timeout(stepTimeout);
      } on Object {
        failed.add(what);
      }
    }

    // What is shown on screen goes first.
    lock.clearSecretsFromMemory();
    await step('The two-factor accounts', vaultStorage.delete);
    await step('The keys that protect the two-factor accounts', () async {
      await vaultKeys.deleteKeys();
      await vaultKeys.endSession();
    });
    await step('The device key', () async {
      final alias = await enrollment.readAlias();
      if (alias != null) await deviceKeys.delete(alias);
    });
    await step('Settings, history and saved data', secure.deleteAll);
    // Last: this is what moves the app to the enrollment screen.
    await step('The enrollment', () async {
      await enrollment.clearAlias();
      await enrollment.clear();
    });
    return EraseResult(failed);
  }
}
