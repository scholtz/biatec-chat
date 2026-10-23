import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// The secure storage every part of the authenticator uses.
///
/// The iOS default accessibility of `flutter_secure_storage` is `unlocked`, which lets the keychain items travel in an encrypted backup and
/// be restored on another device. The device token, the enrollment record and the approval history are bound to this device, so they are kept
/// `unlocked_this_device`: never in a backup, never restored elsewhere (BCHAT-009). Android is unaffected by the option.
const FlutterSecureStorage deviceBoundStorage = FlutterSecureStorage(
  iOptions: IOSOptions(accessibility: KeychainAccessibility.unlocked_this_device),
);

/// The keys the authenticator keeps in [deviceBoundStorage]. The stores use these constants, and the migration below walks this list, so a
/// key added here is migrated too.
const enrollmentStorageKey = 'biatec.enrollment';
const enrollmentAliasStorageKey = 'biatec.enrollment.alias';
const historyStorageKey = 'biatec.approvals.history';
const secureStorageKeys = [
  enrollmentStorageKey,
  enrollmentAliasStorageKey,
  historyStorageKey,
];

const _migratedKey = 'biatec.storage.v2';
const _stagingSuffix = '.migrating';

/// Items written before the accessibility was set keep the old one until they are written again, and an update does not necessarily change it.
/// So once, at start, each existing item is written again under the current options. Nothing is lost if that fails half way: the value is first
/// copied to a staging item, then the original is deleted and written back, then the staging item goes; a leftover staging item (the app
/// died, the keychain was locked) is restored on the next run. Each key stands alone. The marker is written only when every key went through.
/// Returns how many items were rewritten.
Future<int> rewriteWithDeviceBoundAccessibility(
    [FlutterSecureStorage storage = deviceBoundStorage]) async {
  var rewritten = 0;
  var clean = true;
  try {
    if (await storage.read(key: _migratedKey) != null) return 0;
  } on Object {
    return 0; // the keychain is unavailable (device locked, the app started by a push): start anyway, try again at the next launch
  }
  for (final key in secureStorageKeys) {
    try {
      final staging = key + _stagingSuffix;
      final value = await storage.read(key: key) ?? await storage.read(key: staging);
      if (value == null) continue;
      await storage.write(key: staging, value: value);
      await storage.delete(key: key);
      await storage.write(key: key, value: value);
      await storage.delete(key: staging);
      rewritten++;
    } on Object {
      clean = false; // the staging copy, if it was made, is restored at the next launch
    }
  }
  if (clean) {
    try {
      await storage.write(key: _migratedKey, value: '1');
    } on Object {
      // not marked: the next launch rewrites the items again, which is harmless
    }
  }
  return rewritten;
}
