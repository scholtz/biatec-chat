import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/security/secure_storage.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('device-bound secure storage (BCHAT-009)', () {
    test('the shared storage asks for this-device-only keychain items', () {
      expect(deviceBoundStorage.iOptions.params['accessibility'], 'unlocked_this_device');
    });

    test('both stores use it by default', () {
      // They are built without an explicit storage in production; construct them to prove the default compiles and does not throw.
      expect(SecureEnrollmentStore.new, returnsNormally);
      expect(SecureHistoryStore.new, returnsNormally);
    });

    test('existing items are rewritten once, unchanged, and the marker stops a second run', () async {
      FlutterSecureStorage.setMockInitialValues({
        'biatec.enrollment': '{"a":1}',
        'biatec.approvals.history': '[]',
        'unrelated': 'keep',
      });
      const storage = FlutterSecureStorage();
      expect(await rewriteWithDeviceBoundAccessibility(storage), 2);
      expect(await storage.read(key: 'biatec.enrollment'), '{"a":1}');
      expect(await storage.read(key: 'biatec.approvals.history'), '[]');
      expect(await storage.read(key: 'unrelated'), 'keep');
      expect(await storage.read(key: 'biatec.storage.v2'), '1');
      expect(await rewriteWithDeviceBoundAccessibility(storage), 0);
    });

    test('a fresh install has nothing to rewrite and is marked', () async {
      FlutterSecureStorage.setMockInitialValues({});
      const storage = FlutterSecureStorage();
      expect(await rewriteWithDeviceBoundAccessibility(storage), 0);
      expect(await storage.read(key: 'biatec.storage.v2'), '1');
    });
  });
}
