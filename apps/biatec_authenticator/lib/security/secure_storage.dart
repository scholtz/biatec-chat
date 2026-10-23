import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// The secure storage every part of the authenticator uses.
///
/// The iOS default accessibility of `flutter_secure_storage` is `unlocked`, which lets the keychain items travel in an encrypted backup and
/// be restored on another device. The device token, the enrollment record and the approval history are bound to this device, so they are kept
/// `unlocked_this_device`: never in a backup, never restored elsewhere (BCHAT-009). Android is unaffected by the option.
const FlutterSecureStorage deviceBoundStorage = FlutterSecureStorage(
  iOptions: IOSOptions(accessibility: KeychainAccessibility.unlocked_this_device),
);

/// The keys the authenticator keeps in [deviceBoundStorage].
const secureStorageKeys = [
  'biatec.enrollment',
  'biatec.enrollment.alias',
  'biatec.approvals.history',
];

const _migratedKey = 'biatec.storage.v2';

/// Items written before the accessibility was set keep the old one until they are written again, and an update does not necessarily change it.
/// So once, at start, each existing item is deleted and written back unchanged under the current options. Returns how many were rewritten
/// (0 when it already ran).
Future<int> rewriteWithDeviceBoundAccessibility(
    [FlutterSecureStorage storage = deviceBoundStorage]) async {
  var rewritten = 0;
  try {
    if (await storage.read(key: _migratedKey) != null) return 0;
    for (final key in secureStorageKeys) {
      final value = await storage.read(key: key);
      if (value == null) continue;
      await storage.delete(key: key);
      await storage.write(key: key, value: value);
      rewritten++;
    }
    await storage.write(key: _migratedKey, value: '1');
  } on Object {
    // The keychain can be unavailable (device locked, the app started by a push): start anyway and try again at the next launch.
    // An item deleted but not written back is the worst case, and it is a device-bound record the user can enroll again.
  }
  return rewritten;
}
