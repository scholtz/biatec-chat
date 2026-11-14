import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/privacy/phone_eraser.dart';
import 'package:biatec_authenticator/privacy/privacy_screen.dart';
import 'package:biatec_authenticator/privacy/storage_inventory.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:biatec_authenticator/totp/vault/vault_storage.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../codes/support.dart';
import '../shell/app_shell_test.dart' show ShellRig;
import '../totp/vault/support.dart' show account;

class Order {
  final calls = <String>[];
}

class RecordingWipe implements SecureWipe {
  RecordingWipe(this.order, {this.fail = false});
  final Order order;
  final bool fail;

  @override
  Future<void> deleteAll() async {
    order.calls.add('secure');
    if (fail) throw StateError('keychain locked');
  }
}

class RecordingEnrollment extends MemoryEnrollmentStore {
  RecordingEnrollment(this.order);
  final Order order;

  @override
  Future<void> clear() async {
    order.calls.add('enrollment');
    await super.clear();
  }
}

class RecordingDeviceKeys implements DeviceKeyStore {
  RecordingDeviceKeys(this.order, {this.fail = false});
  final Order order;
  final bool fail;
  final deleted = <String>[];
  int keysDeleted = 0;

  @override
  Future<void> deleteKeys() async {
    order.calls.add('device-key');
    if (fail) throw StateError('keystore unavailable');
    keysDeleted++;
  }

  /// The single key of an install from before the two keys.
  @override
  Future<void> deleteLegacyKey(String alias) async => deleted.add(alias);

  @override
  Future<DeviceKeys> generateKeys() => throw UnimplementedError();

  @override
  Future<DeviceKey?> loadApproval() async => null;

  @override
  Future<DeviceKey?> loadPresence() async => null;

  @override
  Future<KeysState> keysState() async => KeysState.missing;

  @override
  Future<Uint8List> signPresence(Uint8List data) =>
      throw UnimplementedError();

  @override
  Future<Uint8List> signApproval(Uint8List data,
          {required String promptTitle, required String promptSubtitle}) =>
      throw UnimplementedError();
}

class Rig {
  Rig._(this.codes, this.order, this.enrollment, this.deviceKeys, this.wipe);

  static Future<Rig> create(
      {bool failKeys = false, bool failSecure = false}) async {
    final codes = await CodesRig.create();
    final order = Order();
    final enrollment = RecordingEnrollment(order);
    await enrollment.writeAlias('device-alias');
    await enrollment.write(EnrollmentRecord(presenceKid: 'p-kid', presenceAttestation: 'p.p.s', 
        kid: 'device-alias',
        deviceToken: 't',
        activeAt: DateTime.utc(2020),
        attestation: 'a'));
    return Rig._(
        codes,
        order,
        enrollment,
        RecordingDeviceKeys(order, fail: failKeys),
        RecordingWipe(order, fail: failSecure));
  }

  final CodesRig codes;
  final Order order;
  final RecordingEnrollment enrollment;
  final RecordingDeviceKeys deviceKeys;
  final RecordingWipe wipe;

  PhoneEraser get eraser => PhoneEraser(
        lock: codes.lock,
        vaultKeys: codes.keys,
        vaultStorage: codes.storage,
        deviceKeys: deviceKeys,
        enrollment: enrollment,
        secure: wipe,
      );
}

Future<void> pumpScreen(WidgetTester tester, Widget screen,
    {bool dark = false,
    double text = 1,
    Size size = const Size(360, 3000)}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  tester.platformDispatcher.textScaleFactorTestValue = text;
  addTearDown(tester.view.reset);
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  await tester.pumpWidget(MaterialApp(
      theme: buildTheme(dark ? Brightness.dark : Brightness.light),
      home: screen));
  await tester.pumpAndSettle();
}

/// The Erase button is at the end of a long list, which builds its rows as they scroll into view.
Future<void> tapErase(WidgetTester tester) async {
  await tester.dragUntilVisible(find.byKey(const Key('erase-everything')),
      find.byKey(const Key('privacy-list')), const Offset(0, -300));
  await tester.pumpAndSettle();
  await tester.tap(find.byKey(const Key('erase-everything')));
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('what the app stores is all listed', () {
    test('every storage key in the source is named in the inventory', () {
      final listed = {
        for (final i in storedOnPhone) ...i.keys,
      };
      final found = <String, String>{};
      for (final f in Directory('lib')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.dart'))) {
        final path = f.path.replaceAll('\\', '/');
        if (path.contains('lib/privacy/')) continue;
        for (final m in RegExp(r"'(biatec\.[A-Za-z0-9.]+)'")
            .allMatches(f.readAsStringSync())) {
          found[m.group(1)!] = path;
        }
      }
      expect(found, isNotEmpty);
      for (final e in found.entries) {
        expect(listed, contains(e.key),
            reason:
                '${e.key} (in ${e.value}) is stored but the Privacy screen does not say so: add it to lib/privacy/storage_inventory.dart');
      }
    });

    test('and every listed key is really used', () {
      final source = [
        for (final f in Directory('lib')
            .listSync(recursive: true)
            .whereType<File>()
            .where((f) =>
                f.path.endsWith('.dart') &&
                !f.path.replaceAll('\\', '/').contains('lib/privacy/')))
          f.readAsStringSync()
      ].join('\n');
      for (final i in storedOnPhone) {
        for (final k in i.keys) {
          expect(source, contains("'$k'"), reason: '$k is listed but not used');
        }
      }
    });

    test('nothing stored is in a file other than the vault file', () {
      final source = File('lib/codes/codes_services.dart').readAsStringSync();
      expect(source, contains('biatec_vault.bin'));
    });
  });

  group('the Privacy screen', () {
    testWidgets('says what is stored, sent and never collected',
        (tester) async {
      await pumpScreen(tester, const PrivacyScreen());
      for (final i in [
        ...storedOnPhone,
        ...sentToBiatec,
        ...sentToPushService
      ]) {
        expect(find.text(i.title), findsOneWidget, reason: i.title);
      }
      for (final t in neverCollected) {
        expect(find.text(t), findsOneWidget, reason: t);
      }
      expect(find.text('Stored on this phone'), findsOneWidget);
      expect(find.text('Sent to Biatec OIDC'), findsOneWidget);
      expect(find.byKey(const Key('erase-everything')), findsNothing,
          reason: 'no eraser, no button');
    });

    for (final dark in [false, true]) {
      testWidgets('holds at 3x text, ${dark ? 'dark' : 'light'}',
          (tester) async {
        final semantics = tester.ensureSemantics();
        final rig = await Rig.create();
        await pumpScreen(tester, PrivacyScreen(eraser: rig.eraser),
            dark: dark, text: 3, size: const Size(320, 9000));
        expect(tester.takeException(), isNull);
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        semantics.dispose();
      });
    }
  });

  group('erase everything', () {
    test('needs a fresh biometric, and a refusal removes nothing', () async {
      final rig = await Rig.create();
      await rig.codes.addAccount(account(seed: 1));
      rig.codes.auth.outcome = BiometricOutcome.failed;
      expect(await rig.eraser.erase(), isNull);
      expect(rig.order.calls, isEmpty);
      expect(rig.codes.storage.bytes, isNotNull);
      expect(await rig.enrollment.read(), isNotNull);
      expect(rig.codes.auth.reasons.last, 'Erase everything on this phone');
    });

    test('is refused while the app is locked', () async {
      final rig = await Rig.create();
      rig.codes.lock.lock();
      expect(await rig.eraser.erase(), isNull);
      expect(rig.order.calls, isEmpty);
    });

    test(
        'removes the accounts, the keys, the device key, the saved data and the enrollment',
        () async {
      final rig = await Rig.create();
      await rig.codes.addAccount(account(seed: 1));
      var cleared = 0;
      rig.codes.lock.addLockListener(() => cleared++);
      final result = await rig.eraser.erase();
      expect(result, isNotNull);
      expect(result!.complete, isTrue);
      expect(rig.codes.storage.bytes, isNull, reason: 'the vault file');
      expect(rig.codes.keys.sessionsEnded, greaterThan(0));
      expect(rig.deviceKeys.deleted, ['device-alias']);
      expect(rig.order.calls, contains('secure'));
      expect(await rig.enrollment.read(), isNull);
      expect(await rig.enrollment.readAlias(), isNull);
      expect(cleared, greaterThan(0), reason: 'the codes leave memory');
    });

    test(
        'the enrollment goes last, so the app moves on only when the rest is gone',
        () async {
      final rig = await Rig.create();
      await rig.eraser.erase();
      expect(rig.order.calls, ['device-key', 'secure', 'enrollment']);
    });

    test(
        'a failure in the first steps leaves the alias and the enrollment, so it can be tried again',
        () async {
      final rig = await Rig.create(failKeys: true);
      final result = await rig.eraser.erase();
      expect(result!.complete, isFalse);
      expect(result.failed, ['The device key']);
      expect(rig.codes.storage.bytes, isNull,
          reason: 'the steps that could run did');
      expect(rig.order.calls, ['device-key'],
          reason: 'nothing after the failure ran');
      expect(await rig.enrollment.readAlias(), 'device-alias');
      expect(await rig.enrollment.read(), isNotNull,
          reason: 'not signed out with a key on the phone');
      for (final f in result.failed) {
        expect(f, isNot(contains('keystore')), reason: 'no platform text');
      }
    });

    test(
        'a failure of the storage wipe is named, and the device is signed out anyway',
        () async {
      final rig = await Rig.create(failSecure: true);
      final result = await rig.eraser.erase();
      expect(result!.failed, ['Settings, history and saved data']);
      expect(await rig.enrollment.read(), isNull);
    });

    test('what must outlive an erase is put back after the wipe', () async {
      final rig = await Rig.create();
      var restored = 0;
      final eraser = PhoneEraser(
        lock: rig.codes.lock,
        vaultKeys: rig.codes.keys,
        vaultStorage: rig.codes.storage,
        deviceKeys: rig.deviceKeys,
        enrollment: rig.enrollment,
        secure: rig.wipe,
        afterWipe: () async {
          rig.order.calls.add('restore');
          restored++;
        },
      );
      await eraser.erase();
      expect(restored, 1);
      expect(
          rig.order.calls, ['device-key', 'secure', 'restore', 'enrollment']);
    });

    test('a hook that throws does not break the erase', () async {
      final rig = await Rig.create();
      rig.codes.lock.addLockListener(() => throw StateError('boom'));
      final result = await rig.eraser.erase();
      expect(result!.complete, isTrue);
    });

    test('a step that never answers is given up on, and the rest still runs',
        () async {
      final rig = await Rig.create();
      final hanging = PhoneEraser(
        lock: rig.codes.lock,
        vaultKeys: rig.codes.keys,
        vaultStorage: rig.codes.storage,
        deviceKeys: rig.deviceKeys,
        enrollment: rig.enrollment,
        secure: _Hangs(),
        stepTimeout: const Duration(milliseconds: 50),
      );
      final result = await hanging.erase();
      expect(result!.failed, ['Settings, history and saved data']);
      expect(await rig.enrollment.read(), isNull);
    });

    test('the vault file and its half-written copy are both removed', () async {
      final dir = await Directory.systemTemp.createTemp('erase');
      addTearDown(() => dir.delete(recursive: true));
      final file = File('${dir.path}/biatec_vault.bin')..writeAsStringSync('x');
      final temp = File('${file.path}.tmp')..writeAsStringSync('y');
      await FileVaultStorage(file).delete();
      expect(file.existsSync(), isFalse);
      expect(temp.existsSync(), isFalse);
      await FileVaultStorage(file).delete(); // nothing there is not an error
    });
  });

  group('the screen action', () {
    testWidgets('asks first, and keeping the data erases nothing',
        (tester) async {
      final rig = await Rig.create();
      await pumpScreen(tester, PrivacyScreen(eraser: rig.eraser));
      await tapErase(tester);
      await tester.pumpAndSettle();
      expect(find.text('Erase everything on this phone?'), findsOneWidget);
      expect(find.byKey(const Key('confirm-warning')), findsOneWidget);
      await tester.tap(find.byKey(const Key('keep-data')));
      await tester.pumpAndSettle();
      expect(rig.order.calls, isEmpty);
    });

    testWidgets(
        'confirming erases, a refused fingerprint says nothing was erased',
        (tester) async {
      final rig = await Rig.create();
      rig.codes.auth.outcome = BiometricOutcome.failed;
      await pumpScreen(tester, PrivacyScreen(eraser: rig.eraser));
      await tapErase(tester);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('confirm-erase')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('erase-refused')), findsOneWidget);
      expect(rig.order.calls, isEmpty);

      rig.codes.auth.outcome = BiometricOutcome.success;
      await tapErase(tester);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('confirm-erase')));
      await tester.pumpAndSettle();
      expect(rig.order.calls, contains('enrollment'));
    });

    testWidgets('a failed step is shown with what to do', (tester) async {
      final rig = await Rig.create(failKeys: true);
      await pumpScreen(tester, PrivacyScreen(eraser: rig.eraser));
      await tapErase(tester);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('confirm-erase')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('erase-incomplete')), findsOneWidget);
      expect(find.textContaining('The device key'), findsWidgets);
      expect(find.textContaining('uninstall the app'), findsOneWidget);
    });
  });

  group('in the app', () {
    testWidgets(
        'Settings opens Privacy, and erasing takes the app back to enrollment',
        (tester) async {
      final semantics = tester.ensureSemantics();
      final rig = ShellRig();
      await rig.pump(tester, size: const Size(400, 3000));
      await tester.tap(find.descendant(
          of: find.byType(NavigationBar), matching: find.text('Settings')));
      await tester.pumpAndSettle();
      await tester.ensureVisible(find.byKey(const Key('about-privacy')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('about-privacy')));
      await tester.pumpAndSettle();
      expect(find.byType(PrivacyScreen), findsOneWidget);
      await tapErase(tester);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('confirm-erase')));
      await tester.pump();
      // The secure storage has no platform side in a test and never answers: the step is given up on after its time.
      await tester.pump(const Duration(seconds: 20));
      await tester.pumpAndSettle();
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);
      semantics.dispose();
      await rig.dispose(tester);
    });
  });
}

class _Hangs implements SecureWipe {
  @override
  Future<void> deleteAll() => Completer<void>().future;
}
