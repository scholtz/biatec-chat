import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' show Rect;

import 'package:file_selector/file_selector.dart';
import 'package:path_provider/path_provider.dart';
import 'package:share_plus/share_plus.dart';

import 'backup_format.dart';

/// Where a backup file goes to and comes from. The real one is the system share sheet and the system file picker; tests use a fake.
abstract class BackupFiles {
  /// Offers [bytes] to the user as a file called [fileName] through the system share sheet. True when the sheet was shown.
  Future<bool> share(Uint8List bytes, String fileName);

  /// Lets the user pick a backup file. Null when they closed the picker.
  Future<Uint8List?> pick();
}

class SystemBackupFiles implements BackupFiles {
  const SystemBackupFiles();

  @override
  Future<bool> share(Uint8List bytes, String fileName) async {
    // The file exists only while the share sheet runs, in the app's temporary folder, and is removed afterwards (the backup is encrypted, but nothing needs to linger).
    final dir = await getTemporaryDirectory();
    final file = File('${dir.path}/$fileName');
    try {
      await file.writeAsBytes(bytes, flush: true);
      final result = await SharePlus.instance.share(ShareParams(
        // iPad needs somewhere to anchor the share popover.
        sharePositionOrigin: const Rect.fromLTWH(0, 0, 1, 1),
        files: [
          XFile(file.path, mimeType: 'application/octet-stream', name: fileName)
        ],
      ));
      // Some platforms cannot say what the user chose (unavailable); only a closed sheet is a refusal.
      return result.status != ShareResultStatus.dismissed;
    } finally {
      try {
        if (await file.exists()) await file.delete();
      } on Object {
        // Left for the system to clear with the temporary folder.
      }
    }
  }

  @override
  Future<Uint8List?> pick() async {
    final chosen = await openFile(acceptedTypeGroups: const [
      XTypeGroup(
        label: 'Biatec Authenticator backup',
        extensions: ['biatecbackup'],
        mimeTypes: ['application/octet-stream'],
        uniformTypeIdentifiers: ['public.data'],
      ),
    ]);
    if (chosen == null) return null;
    // A file larger than any backup is not read into memory: empty bytes are refused by the codec like any other bad file.
    if (await chosen.length() > const BackupPolicy().maxFileBytes) {
      return Uint8List(0);
    }
    return Uint8List.fromList(await chosen.readAsBytes());
  }
}
