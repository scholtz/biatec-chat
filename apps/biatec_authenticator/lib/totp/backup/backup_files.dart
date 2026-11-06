import 'dart:io';
import 'dart:typed_data';

import 'package:file_selector/file_selector.dart';
import 'package:path_provider/path_provider.dart';
import 'package:share_plus/share_plus.dart';

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
      await SharePlus.instance.share(ShareParams(
        files: [
          XFile(file.path, mimeType: 'application/octet-stream', name: fileName)
        ],
      ));
      return true;
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
    return chosen == null
        ? null
        : Uint8List.fromList(await chosen.readAsBytes());
  }
}
