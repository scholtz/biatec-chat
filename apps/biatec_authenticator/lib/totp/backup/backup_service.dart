import 'dart:typed_data';

import '../../lock/lock_controller.dart';
import '../core/otp_account.dart';
import '../vault/totp_vault.dart';
import 'backup_files.dart';
import 'backup_format.dart';
import 'passphrase.dart';

/// Why a backup was not made or read, in words for the user. Never the passphrase, a name or a secret.
class BackupRefused implements Exception {
  const BackupRefused(this.message);
  final String message;

  @override
  String toString() => message;
}

/// What opening a backup found: the accounts it would add, and how many the vault already has.
class ImportPreview {
  ImportPreview(this.toAdd, this.duplicates);

  final List<OtpAccount> toAdd;
  final int duplicates;

  /// Wipes the decrypted accounts.
  void wipe() {
    for (final a in toAdd) {
      a.wipe();
    }
  }
}

/// Export and import of the encrypted backup. Both start with a fresh biometric; the passphrase is used for one derivation and is not kept. A saved seed reaches a file only through [BackupCodec]; nothing here shows it.
class BackupService {
  BackupService({
    required this.lock,
    required this.vault,
    required this.files,
    BackupCodec? codec,
    DateTime Function()? clock,
  })  : codec = codec ?? BackupCodec(),
        _clock = clock ?? DateTime.now;

  final LockController lock;
  final TotpVault vault;
  final BackupFiles files;
  final BackupCodec codec;
  final DateTime Function() _clock;

  /// Makes the backup of every account in the vault and offers it through the share sheet. Returns how many accounts it holds.
  Future<int> export(String passphrase, String confirmation) async {
    final verdict = Passphrase.check(passphrase, confirmation: confirmation);
    if (!verdict.accepted) throw BackupRefused(verdict.problem!);
    if (!await lock.reauthenticate('Create a backup of your accounts')) {
      throw const BackupRefused(
          'The fingerprint or face check did not pass. No backup was made.');
    }
    final infos = await vault.list();
    if (infos.isEmpty) {
      throw const BackupRefused('There are no accounts to back up yet.');
    }
    final accounts = <OtpAccount>[];
    try {
      for (final info in infos) {
        accounts.add(await vault.readSecret(info.id));
      }
      final file = await codec.encrypt(accounts, passphrase);
      await files.share(file, BackupCodec.fileName(_clock()));
      return accounts.length;
    } finally {
      for (final a in accounts) {
        a.wipe();
      }
    }
  }

  /// Opens a backup the user picks: a fresh biometric first, then the picker, then the passphrase. Null when the picker was closed. [existing] are the accounts already in the app (for the duplicate count); the preview's accounts are the caller's to wipe.
  Future<ImportPreview?> open(String passphrase,
      {List<OtpAccount> existing = const []}) async {
    if (!await lock.reauthenticate('Restore accounts from a backup')) {
      throw const BackupRefused(
          'The fingerprint or face check did not pass. Nothing was read.');
    }
    return _read(passphrase, existing);
  }

  /// The same as [open], for the screen shown after the phone's biometrics changed: the app is locked then, so the fresh biometric comes from the lock's own restore check.
  Future<ImportPreview?> openAfterBiometricsChanged(String passphrase) async {
    if (!await lock.verifyForRestore()) {
      throw const BackupRefused(
          'The fingerprint or face check did not pass. Nothing was read.');
    }
    return _read(passphrase, const []);
  }

  Future<ImportPreview?> _read(
      String passphrase, List<OtpAccount> existing) async {
    final Uint8List? file = await files.pick();
    if (file == null) return null;
    final opened = await codec.decrypt(file, passphrase);
    final fresh = <OtpAccount>[];
    var duplicates = 0;
    for (final a in opened) {
      final already = existing.any((e) => !e.isWiped && a.isDuplicateOf(e)) ||
          fresh.any((e) => a.isDuplicateOf(e));
      if (already) {
        duplicates++;
        a.wipe();
      } else {
        fresh.add(a);
      }
    }
    return ImportPreview(fresh, duplicates);
  }

  /// After the biometrics changed: the old vault cannot be opened, so it is replaced by a new one holding [preview]'s accounts, and the app goes back to its lock screen. Returns how many were restored.
  Future<int> restoreIntoFreshVault(ImportPreview preview) async {
    await vault.resetAll();
    var n = 0;
    try {
      for (final a in preview.toAdd) {
        await vault.add(a);
        n++;
      }
    } finally {
      preview.wipe();
    }
    lock.markBiometricsRestored();
    return n;
  }
}
