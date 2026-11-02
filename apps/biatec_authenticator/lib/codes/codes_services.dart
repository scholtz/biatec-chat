import 'dart:io';
import 'dart:typed_data';

import 'package:path_provider/path_provider.dart';

import '../lock/lock_controller.dart';
import '../security/secure_storage.dart';
import '../totp/vault/totp_vault.dart';
import '../totp/vault/vault_key_store.dart';
import '../totp/vault/vault_storage.dart';
import 'sensitive_clipboard.dart';
import 'server_clock.dart';

/// What the Codes tab keeps on this phone besides the accounts: whether codes are hidden until touched, and the latest time seen.
abstract class CodesPrefs {
  Future<bool> readTapToReveal();
  Future<void> writeTapToReveal(bool value);

  /// The latest time the list showed codes at, so a clock reset to the past is noticed (see `OtpGenerator.clockIsPlausible`).
  Future<DateTime?> readLatestSeen();
  Future<void> writeLatestSeen(DateTime value);
}

class SecureCodesPrefs implements CodesPrefs {
  const SecureCodesPrefs();

  static const _reveal = 'biatec.codes.tapToReveal';
  static const _seen = 'biatec.codes.latestSeen';

  @override
  Future<bool> readTapToReveal() async =>
      await deviceBoundStorage.read(key: _reveal) == '1';

  @override
  Future<void> writeTapToReveal(bool value) =>
      deviceBoundStorage.write(key: _reveal, value: value ? '1' : '0');

  @override
  Future<DateTime?> readLatestSeen() async {
    final ms = int.tryParse(await deviceBoundStorage.read(key: _seen) ?? '');
    return ms == null
        ? null
        : DateTime.fromMillisecondsSinceEpoch(ms, isUtc: true);
  }

  @override
  Future<void> writeLatestSeen(DateTime value) => deviceBoundStorage.write(
      key: _seen, value: '${value.toUtc().millisecondsSinceEpoch}');
}

/// The vault file in the app's private folder, found when first used. The folder is outside every backup (see the platform hardening).
class AppSupportVaultStorage implements VaultStorage {
  FileVaultStorage? _file;

  Future<FileVaultStorage> _open() async => _file ??= FileVaultStorage(File(
      '${(await getApplicationSupportDirectory()).path}/biatec_vault.bin'));

  @override
  Future<Uint8List?> read() async => (await _open()).read();

  @override
  Future<void> write(Uint8List bytes) async => (await _open()).write(bytes);
}

/// Everything the Codes tab needs, built once and replaced with fakes in tests.
class CodesServices {
  CodesServices({
    required this.vault,
    required this.lock,
    required this.clipboard,
    ServerClock? serverClock,
    DateTime Function()? clock,
    CodesPrefs? prefs,
  })  : serverClock = serverClock ?? ServerClock.instance,
        clock = clock ?? DateTime.now,
        prefs = prefs ?? const SecureCodesPrefs() {
    // Locking ends the platform's memory of the last biometric, so the next secret read asks again.
    lock.addLockListener(() => vault.keys.endSession());
  }

  /// The platform pieces: hardware-backed vault, the app's lock, a sensitive clipboard.
  factory CodesServices.platform(LockController lock) => CodesServices(
        vault: TotpVault(
          keys: const PlatformVaultKeyStore(),
          storage: AppSupportVaultStorage(),
          versions: const SecureVaultVersionStore(),
        ),
        lock: lock,
        clipboard: PlatformSensitiveClipboard(),
      );

  final TotpVault vault;
  final LockController lock;
  final SensitiveClipboard clipboard;
  final ServerClock serverClock;
  final DateTime Function() clock;
  final CodesPrefs prefs;
}
