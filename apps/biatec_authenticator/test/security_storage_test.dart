import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/security/secure_storage.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

/// An in-memory storage that can be told to fail writes to one key.
class FlakyStorage extends FlutterSecureStorage {
  FlakyStorage(this.items);

  final Map<String, String> items;
  String? failWritesTo;
  bool unavailable = false;

  @override
  Future<String?> read({required String key, iOptions, aOptions, lOptions, webOptions, mOptions, wOptions}) async {
    if (unavailable) throw StateError('keychain locked');
    return items[key];
  }

  @override
  Future<void> write({required String key, required String? value, iOptions, aOptions, lOptions, webOptions, mOptions, wOptions}) async {
    if (unavailable || key == failWritesTo) throw StateError('write failed');
    if (value == null) {
      items.remove(key);
    } else {
      items[key] = value;
    }
  }

  @override
  Future<void> delete({required String key, iOptions, aOptions, lOptions, webOptions, mOptions, wOptions}) async {
    if (unavailable) throw StateError('keychain locked');
    items.remove(key);
  }
}

void main() {
  group('device-bound secure storage (BCHAT-009)', () {
    test('the shared storage asks for this-device-only keychain items', () {
      expect(deviceBoundStorage.iOptions.params['accessibility'], 'unlocked_this_device');
    });

    test('the stores keep their data under the keys the migration walks', () {
      expect(secureStorageKeys, containsAll([enrollmentStorageKey, enrollmentAliasStorageKey, historyStorageKey]));
      expect(SecureEnrollmentStore.new, returnsNormally);
      expect(SecureHistoryStore.new, returnsNormally);
    });

    test('existing items are rewritten once, unchanged, and the marker stops a second run', () async {
      final s = FlakyStorage({'biatec.enrollment': '{"a":1}', 'biatec.approvals.history': '[]', 'unrelated': 'keep'});
      expect(await rewriteWithDeviceBoundAccessibility(s), 2);
      expect(s.items, {'biatec.enrollment': '{"a":1}', 'biatec.approvals.history': '[]', 'unrelated': 'keep', 'biatec.storage.v2': '1'});
      expect(await rewriteWithDeviceBoundAccessibility(s), 0);
    });

    test('a fresh install has nothing to rewrite and is marked', () async {
      final s = FlakyStorage({});
      expect(await rewriteWithDeviceBoundAccessibility(s), 0);
      expect(s.items, {'biatec.storage.v2': '1'});
    });

    test('a write that fails after the delete loses nothing: the staging copy is restored at the next run, and the marker waits', () async {
      final s = FlakyStorage({'biatec.enrollment': 'SECRET-RECORD', 'biatec.approvals.history': '[]'});
      s.failWritesTo = 'biatec.enrollment';
      expect(await rewriteWithDeviceBoundAccessibility(s), 1, reason: 'the history item went through');
      expect(s.items.containsKey('biatec.storage.v2'), isFalse, reason: 'not marked while a key failed');
      expect(s.items['biatec.enrollment.migrating'], 'SECRET-RECORD', reason: 'the value survived in the staging item');

      s.failWritesTo = null;
      expect(await rewriteWithDeviceBoundAccessibility(s), 2);
      expect(s.items['biatec.enrollment'], 'SECRET-RECORD');
      expect(s.items.containsKey('biatec.enrollment.migrating'), isFalse);
      expect(s.items['biatec.storage.v2'], '1');
    });

    test('an unavailable keychain does not stop the start and marks nothing', () async {
      final s = FlakyStorage({'biatec.enrollment': 'x'})..unavailable = true;
      expect(await rewriteWithDeviceBoundAccessibility(s), 0);
      s.unavailable = false;
      expect(s.items, {'biatec.enrollment': 'x'});
    });
  });
}
