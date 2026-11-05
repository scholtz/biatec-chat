import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../../security/secure_storage.dart';

/// Where the sealed vault bytes live.
abstract class VaultStorage {
  /// The file as it is, or null when there is none yet.
  Future<Uint8List?> read();

  /// Replaces the file. Atomic as far as the file system makes a rename: the old file or the new one is read back, and a torn file is refused by the vault's own checks.
  Future<void> write(Uint8List bytes);

  /// Removes the file (and any half-written copy). Nothing there is not an error.
  Future<void> delete();
}

/// A file in the app's private storage. Written to a temporary file in the same folder, flushed, and renamed over the real one, so a crash leaves a whole file.
class FileVaultStorage implements VaultStorage {
  FileVaultStorage(this.file);

  final File file;

  @override
  Future<Uint8List?> read() async {
    if (!await file.exists()) return null;
    return file.readAsBytes();
  }

  @override
  Future<void> write(Uint8List bytes) async {
    await file.parent.create(recursive: true);
    final temp = File('${file.path}.tmp');
    try {
      await temp.writeAsBytes(bytes, flush: true);
      await temp.rename(file.path);
    } on Object {
      // No half-written copy is left lying around.
      if (await temp.exists()) await temp.delete();
      rethrow;
    }
  }

  @override
  Future<void> delete() async {
    for (final f in [file, File('${file.path}.tmp')]) {
      if (await f.exists()) await f.delete();
    }
  }
}

/// The highest vault version this phone has written, kept outside the vault file so that putting an older copy of the file back (a restored backup, a copied folder) is noticed.
abstract class VaultVersionStore {
  /// 0 when nothing was written yet.
  Future<int> read();
  Future<void> write(int version);
}

/// The version counter in the keychain or Android's encrypted preferences (device-bound, never in a backup).
class SecureVaultVersionStore implements VaultVersionStore {
  const SecureVaultVersionStore([this._storage = deviceBoundStorage]);

  static const _key = 'biatec.vault.version';
  final FlutterSecureStorage _storage;

  @override
  Future<int> read() async =>
      int.tryParse(await _storage.read(key: _key) ?? '') ?? 0;

  @override
  Future<void> write(int version) =>
      _storage.write(key: _key, value: '$version');
}
