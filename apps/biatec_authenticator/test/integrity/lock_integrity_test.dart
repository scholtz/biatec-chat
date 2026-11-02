import 'dart:io';

import 'package:biatec_authenticator/integrity/attestation.dart';
import 'package:biatec_authenticator/integrity/integrity.dart';
import 'package:biatec_authenticator/integrity/integrity_checker.dart';
import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:biatec_authenticator/lock/lock_screens.dart';
import 'package:biatec_authenticator/shell/app_lock_gate.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import '../lock/support.dart';

class Scripted implements IntegrityChecker {
  IntegrityResult result = const IntegrityResult.healthy();
  int calls = 0;

  @override
  Future<IntegrityResult> check() async {
    calls++;
    return result;
  }
}

const compromised = IntegrityResult({IntegrityReason.deviceModified});

({
  LockController lock,
  FakeAuthenticator auth,
  Scripted checker,
  IntegrityGuard guard
}) make({required bool failClosed, DateTime Function()? clock}) {
  final auth = FakeAuthenticator();
  final checker = Scripted();
  final guard = IntegrityGuard(checker, clock: clock);
  final lock = LockController(
    authenticator: auth,
    store: MemoryLockStore(),
    clock: clock,
    integrity: guard,
    failClosed: failClosed,
  );
  addTearDown(lock.dispose);
  return (lock: lock, auth: auth, checker: checker, guard: guard);
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('release: fails closed', () {
    test('a compromised phone is never asked for a fingerprint and never opens',
        () async {
      final r = make(failClosed: true);
      r.checker.result = compromised;
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.compromised);
      expect(r.auth.prompts, 0);
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.compromised);
      expect(r.lock.isUnlocked, isFalse);
    });

    test('a healthy phone opens as before', () async {
      final r = make(failClosed: true);
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.unlocked);
      expect(r.lock.integrityWarning, isEmpty);
    });

    test('the default is closed', () {
      final lock = LockController(
          authenticator: FakeAuthenticator(), store: MemoryLockStore());
      addTearDown(lock.dispose);
      expect(lock.failClosed, isTrue);
    });

    test(
        'a tool attached while the app is open closes it when the app comes back',
        () async {
      final r = make(failClosed: true);
      await r.lock.unlock();
      var cleared = 0;
      r.lock.addLockListener(() => cleared++);
      r.checker.result = compromised;
      r.lock.appPaused();
      r.lock.appResumed();
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);
      expect(r.lock.state, AppLockState.compromised);
      expect(cleared, greaterThan(0), reason: 'the secrets leave memory');
    });

    test(
        'an approval re-runs the check first and is refused on a compromised phone',
        () async {
      var now = DateTime.utc(2026, 11, 2, 10);
      final r = make(failClosed: true, clock: () => now);
      await r.lock.unlock();
      final before = r.checker.calls;
      final prompts = r.auth.prompts;
      expect(await r.lock.reauthenticate('Approve this action'), isTrue);
      expect(r.checker.calls, before + 1,
          reason: 'a new check, not the cached one');

      r.checker.result = compromised;
      expect(await r.lock.reauthenticate('Approve this action'), isFalse);
      expect(r.auth.prompts, prompts + 1,
          reason: 'no prompt for the refused one');
      expect(r.lock.state, AppLockState.compromised);
    });

    test('checking again lets a phone through once it passes', () async {
      final r = make(failClosed: true);
      r.checker.result = compromised;
      await r.lock.unlock();
      expect(r.lock.canRecheckIntegrity, isTrue);
      await r.lock.recheckIntegrity();
      expect(r.lock.state, AppLockState.compromised, reason: 'still failing');

      r.checker.result = const IntegrityResult.healthy();
      await r.lock.recheckIntegrity();
      expect(r.lock.state, AppLockState.unlocked,
          reason: 'it still had to pass the fingerprint');
      expect(r.auth.prompts, 1);
    });

    test('a closed state from another cause cannot be rechecked away',
        () async {
      final r = make(failClosed: true);
      await r.lock.unlock();
      r.lock.markCompromised();
      expect(r.lock.canRecheckIntegrity, isFalse);
      await r.lock.recheckIntegrity();
      expect(r.lock.state, AppLockState.compromised);
    });
  });

  group('debug: warns only', () {
    test('a compromised phone opens, with a warning', () async {
      final r = make(failClosed: false);
      r.checker.result = compromised;
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.unlocked);
      expect(r.lock.integrityWarning, {IntegrityReason.deviceModified});
    });

    test('the warning goes when the phone passes', () async {
      final r = make(failClosed: false);
      r.checker.result = compromised;
      await r.lock.unlock();
      r.checker.result = const IntegrityResult.healthy();
      expect(await r.lock.reauthenticate('Approve'), isTrue);
      expect(r.lock.integrityWarning, isEmpty);
    });

    test('an approval still goes through', () async {
      final r = make(failClosed: false);
      r.checker.result = compromised;
      await r.lock.unlock();
      expect(await r.lock.reauthenticate('Approve'), isTrue);
    });
  });

  group('caching', () {
    test(
        'a start and an unlock share one check; resume and approval refresh it',
        () async {
      var now = DateTime.utc(2026, 11, 2, 10);
      final r = make(failClosed: true, clock: () => now);
      await r.lock.unlock();
      expect(r.checker.calls, 1);
      r.lock.lock();
      now = now.add(const Duration(seconds: 5));
      await r.lock.unlock();
      expect(r.checker.calls, 1, reason: 'cached for a short time');

      r.lock.appPaused();
      r.lock.appResumed();
      await Future<void>.delayed(Duration.zero);
      expect(r.checker.calls, 2, reason: 'resume asks again');
      await r.lock.reauthenticate('Approve');
      expect(r.checker.calls, 3, reason: 'so does an approval');
    });
  });

  group('the screens', () {
    Future<void> pumpGate(WidgetTester tester, LockController lock,
        {bool dark = false}) async {
      tester.view.physicalSize = const Size(360, 800);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          theme: buildTheme(dark ? Brightness.dark : Brightness.light),
          home: AppLockGate(lock: lock, child: const Text('secure'))));
      await tester.pump();
      await tester.pump();
    }

    testWidgets('release: the reason screen shows and the secure area does not',
        (tester) async {
      final r = make(failClosed: true);
      r.checker.result = compromised;
      await pumpGate(tester, r.lock);
      expect(find.byKey(const Key('compromised')), findsOneWidget);
      expect(find.text('secure'), findsNothing);
      expect(find.byKey(const Key('integrity-recheck')), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('the reason screen does not name what fired', (tester) async {
      final r = make(failClosed: true);
      r.checker.result = const IntegrityResult(
          {IntegrityReason.deviceModified, IntegrityReason.toolsAttached});
      await pumpGate(tester, r.lock);
      final shown = [
        for (final t in tester.widgetList<Text>(find.byType(Text))) t.data ?? ''
      ].join(' ').toLowerCase();
      for (final word in [
        'root',
        'jailbreak',
        'magisk',
        'frida',
        'xposed',
        'debugger',
        'emulator',
        'simulator',
        'signature',
        'certificate',
        'su binary',
        'hook',
        'tamper',
      ]) {
        expect(shown, isNot(contains(word)), reason: word);
      }
      expect(shown, contains('did not pass a safety check'));
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('debug: the lock screen warns and the app still opens',
        (tester) async {
      final r = make(failClosed: false);
      r.checker.result = compromised;
      r.auth.outcome = BiometricOutcome.cancelled;
      await pumpGate(tester, r.lock);
      expect(find.byKey(const Key('lock-screen')), findsOneWidget);
      expect(find.byKey(const Key('integrity-warning')), findsOneWidget);
      r.auth.outcome = BiometricOutcome.success;
      await tester.tap(find.byKey(const Key('unlock')));
      await tester.pump();
      await tester.pump();
      expect(find.text('secure'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    for (final dark in [false, true]) {
      testWidgets(
          'the reason screen holds at 3x text, ${dark ? 'dark' : 'light'}',
          (tester) async {
        final semantics = tester.ensureSemantics();
        tester.platformDispatcher.textScaleFactorTestValue = 3;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        final r = make(failClosed: true);
        r.checker.result = compromised;
        tester.view.physicalSize = const Size(320, 568);
        tester.view.devicePixelRatio = 1.0;
        addTearDown(tester.view.reset);
        await r.lock.unlock();
        await tester.pumpWidget(MaterialApp(
            theme: buildTheme(dark ? Brightness.dark : Brightness.light),
            home: CompromisedScreen(lock: r.lock)));
        await tester.pump();
        expect(tester.takeException(), isNull);
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        semantics.dispose();
        await tester.pumpWidget(const SizedBox());
      });
    }
  });

  group('attestation hook', () {
    const channel = MethodChannel('io.biatec.authenticator/attestation');
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    tearDown(() => messenger.setMockMethodCallHandler(channel, null));

    test('asks the platform with the nonce and returns the token', () async {
      MethodCall? seen;
      messenger.setMockMethodCallHandler(channel, (c) async {
        seen = c;
        return {'kind': 'app_attest', 'token': 'abc', 'keyId': 'k1'};
      });
      final token = await const PlatformAttestationProvider().token('nonce-1');
      expect((seen!.arguments as Map)['nonce'], 'nonce-1');
      expect(token!.kind, 'app_attest');
      expect(token.keyId, 'k1');
      expect(token.toString(), isNot(contains('abc')));
    });

    test('a platform that cannot attest gives no token, never an error',
        () async {
      messenger.setMockMethodCallHandler(channel, (c) async {
        throw PlatformException(code: 'unavailable');
      });
      expect(await const PlatformAttestationProvider().token('n'), isNull);
      messenger.setMockMethodCallHandler(channel, (c) async => {'kind': 'x'});
      expect(await const PlatformAttestationProvider().token('n'), isNull);
    });
  });

  group('platform wiring', () {
    String read(String p) => File(p).readAsStringSync();

    test('the natives exist and are registered', () {
      expect(
          read(
              'android/app/src/main/kotlin/io/biatec/authenticator/MainActivity.kt'),
          contains('IntegrityChannel('));
      expect(read('ios/Runner/AppDelegate.swift'),
          contains('IntegrityChannel.register('));
      final project = read('ios/Runner.xcodeproj/project.pbxproj');
      expect('IntegrityChannel.swift in Sources */,'.allMatches(project).length,
          1);
      expect(project,
          contains('CODE_SIGN_ENTITLEMENTS = Runner/Runner.entitlements;'));
      expect(read('ios/Runner/Runner.entitlements'),
          contains('appattest-environment'));
    });

    test('Android reads the signing certificate and refuses a multi-signer app',
        () {
      final kt = read(
          'android/app/src/main/kotlin/io/biatec/authenticator/IntegrityChannel.kt');
      expect(kt, contains('GET_SIGNING_CERTIFICATES'));
      expect(kt, contains('hasMultipleSigners'));
      expect(kt, contains('SHA-256'));
      expect(kt, contains('Debug.isDebuggerConnected'));
    });

    test('iOS reads the debugger flag and the loaded images', () {
      final sw = read('ios/Runner/IntegrityChannel.swift');
      expect(sw, contains('P_TRACED'));
      expect(sw, contains('_dyld_get_image_name'));
      expect(sw, contains('DCAppAttestService'));
    });

    test('the main entry fails closed in release and warns otherwise', () {
      final main = read('lib/main.dart');
      expect('failClosed: kReleaseMode'.allMatches(main).length, 2);
      expect(main, contains('IntegrityGuard(platformIntegrityChecker())'));
      final lock = read('lib/lock/lock_controller.dart');
      expect(lock, isNot(contains('kDebugMode')));
      expect(lock, isNot(contains('kReleaseMode')));
      expect(lock, contains('this.failClosed = true'));
    });

    test('the release certificate digest is configuration', () {
      expect(read('lib/integrity/platform_integrity.dart'),
          contains("String.fromEnvironment('RELEASE_CERT_SHA256')"));
    });
  });
}
