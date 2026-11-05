import 'dart:async';
import 'dart:io';

import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:biatec_authenticator/shell/app_lock_gate.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:biatec_authenticator/update/update_guard.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../lock/support.dart';

String read(String p) => File(p).readAsStringSync();

({
  LockController lock,
  FakeAuthenticator auth,
  MinimumBuildMonitor monitor,
  DateTime Function() now,
  void Function(Duration) advance
}) make({int build = 5, String expires = ''}) {
  var now = DateTime.utc(2026, 11, 5, 12);
  final monitor = MinimumBuildMonitor();
  final auth = FakeAuthenticator();
  final lock = LockController(
    authenticator: auth,
    store: MemoryLockStore(),
    updates: UpdateGuard(
        monitor: monitor, build: build, expires: expires, clock: () => now),
  );
  addTearDown(lock.dispose);
  return (
    lock: lock,
    auth: auth,
    monitor: monitor,
    now: () => now,
    advance: (d) => now = now.add(d),
  );
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('the minimum build', () {
    test('is read from the header, and only a higher number is kept', () {
      final m = MinimumBuildMonitor();
      var heard = 0;
      m.addListener(() => heard++);
      expect(m.required, 0);
      m.observe('7');
      expect(m.required, 7);
      m.observe('3');
      expect(m.required, 7, reason: 'a later, lower answer does not lower it');
      m.observe('7');
      m.observe(' 9 ');
      expect(m.required, 9);
      expect(heard, 2);
    });

    test('garbage is ignored', () {
      final m = MinimumBuildMonitor();
      for (final v in [
        null,
        '',
        'x',
        '-4',
        '1e3',
        '12.5',
        '99999999999',
        '1000001'
      ]) {
        m.observe(v);
      }
      expect(m.required, 0);
    });

    test('the wrapped client reports every answer, errors included', () async {
      final m = MinimumBuildMonitor();
      final client = m.wrap(MockClient((r) async => r.url.path == '/bad'
          ? http.Response('{}', 500, headers: {'x-biatec-min-app-build': '12'})
          : http.Response('{}', 200,
              headers: {'x-biatec-min-app-build': '4'})));
      await client.get(Uri.parse('https://oidc.example/ok'));
      expect(m.required, 4);
      await client.get(Uri.parse('https://oidc.example/bad'));
      expect(m.required, 12);
      client.close();
    });
  });

  group('the guard', () {
    test('lets a build through until it is older than the minimum', () {
      final m = MinimumBuildMonitor();
      final g = UpdateGuard(monitor: m, build: 5);
      expect(g.blocked, isNull);
      m.observe('5');
      expect(g.blocked, isNull, reason: 'the same build is the minimum');
      m.observe('6');
      expect(g.blocked, UpdateReason.tooOld);
    });

    test('a build expires on its date, in UTC', () {
      var now = DateTime.utc(2027, 6, 29, 23, 59);
      final g = UpdateGuard(
          monitor: MinimumBuildMonitor(),
          build: 1,
          expires: '2027-06-30',
          clock: () => now);
      expect(g.blocked, isNull);
      now = DateTime.utc(2027, 6, 30);
      expect(g.blocked, UpdateReason.expired);
    });

    test(
        'a date that cannot be read closes the build at once, so the typo shows',
        () {
      final g = UpdateGuard(
          monitor: MinimumBuildMonitor(), build: 1, expires: 'soon');
      expect(g.expiresOn, isNull);
      expect(g.blocked, UpdateReason.expired);
    });

    test('too old comes before expired', () {
      final m = MinimumBuildMonitor()..observe('9');
      final g = UpdateGuard(monitor: m, build: 1, expires: '2020-01-01');
      expect(g.blocked, UpdateReason.tooOld);
    });

    test('the default build number is the one in pubspec.yaml', () {
      final version = RegExp(r'^version:\s*\S+\+(\d+)', multiLine: true)
          .firstMatch(read('pubspec.yaml'))!;
      expect(appBuild, int.parse(version.group(1)!));
    });
  });

  group('the secure area stays closed for an old build', () {
    test('a build older than the minimum is never asked for a fingerprint',
        () async {
      final r = make(build: 5);
      r.monitor.observe('6');
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.updateRequired);
      expect(r.auth.prompts, 0);
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.updateRequired);
      expect(r.lock.updateReason, UpdateReason.tooOld);
    });

    test('a current build opens as before', () async {
      final r = make(build: 5);
      r.monitor.observe('5');
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.unlocked);
    });

    test(
        'an answer that arrives while the app is open closes it and clears memory',
        () async {
      final r = make(build: 5);
      await r.lock.unlock();
      var cleared = 0;
      r.lock.addLockListener(() => cleared++);
      r.monitor.observe('8');
      expect(r.lock.state, AppLockState.updateRequired);
      expect(cleared, greaterThan(0));
      r.lock.lock();
      expect(r.lock.state, AppLockState.updateRequired,
          reason: 'locking keeps it');
    });

    test('an approval is refused and nothing is prompted', () async {
      final r = make(build: 5);
      await r.lock.unlock();
      final prompts = r.auth.prompts;
      r.monitor.observe('8');
      expect(await r.lock.reauthenticate('Approve this action'), isFalse);
      expect(r.auth.prompts, prompts);
    });

    test('an expiry reached while the app is open closes it when it comes back',
        () async {
      final r = make(build: 5, expires: '2026-11-06');
      await r.lock.unlock();
      r.advance(const Duration(days: 2));
      r.lock.appPaused();
      r.lock.appResumed();
      expect(r.lock.state, AppLockState.updateRequired);
      expect(r.lock.updateReason, UpdateReason.expired);
    });

    test('another closed state is not replaced by this one', () async {
      final r = make(build: 5);
      await r.lock.unlock();
      r.lock.markBiometricsChanged();
      r.monitor.observe('8');
      // The update state wins only over open or waiting states; the changed-biometrics screen has its own instructions.
      expect(r.lock.state, AppLockState.biometricsChanged);
    });

    test(
        'an unlock that is waiting is not allowed to replace the update screen',
        () async {
      final r = make(build: 5);
      r.auth.statusResult = BiometricStatus.noneEnrolled;
      final gate = Completer<void>();
      final slow = _SlowStatus(r.auth, gate);
      final lock = LockController(
        authenticator: slow,
        store: MemoryLockStore(),
        updates: UpdateGuard(monitor: r.monitor, build: 5),
      );
      addTearDown(lock.dispose);
      final opening = lock.unlock();
      await Future<void>.delayed(Duration.zero);
      expect(lock.state, AppLockState.unlocking);
      r.monitor.observe('9');
      expect(lock.state, AppLockState.updateRequired);
      gate.complete();
      await opening;
      expect(lock.state, AppLockState.updateRequired);
    });

    test(
        'the expiry date passing while the app sits open closes it on the next idle check',
        () async {
      final r = make(build: 5, expires: '2026-11-06');
      await r.lock.unlock();
      r.advance(const Duration(days: 2));
      r.lock.checkIdle();
      expect(r.lock.state, AppLockState.updateRequired);
    });
  });

  group('the minimum survives a restart', () {
    test('a saved minimum is loaded, and a raised one is saved', () async {
      final store = _MemoryMinStore()..value = 8;
      final m = MinimumBuildMonitor(store: store);
      expect(m.required, 0);
      await m.load();
      expect(m.required, 8);
      expect(UpdateGuard(monitor: m, build: 5).blocked, UpdateReason.tooOld);
      m.observe('11');
      await Future<void>.delayed(Duration.zero);
      expect(store.value, 11);
      m.observe('4');
      expect(store.value, 11, reason: 'a lower answer is not saved');
    });

    test('storage that cannot be read or written leaves things as they were',
        () async {
      final store = _MemoryMinStore()..fail = true;
      final m = MinimumBuildMonitor(store: store);
      await m.load();
      expect(m.required, 0);
      m.observe('6');
      expect(m.required, 6,
          reason: 'held for this run even if it cannot be saved');
    });

    test('a garbage saved value is not trusted', () async {
      final store = _MemoryMinStore()..value = 99999999;
      final m = MinimumBuildMonitor(store: store);
      await m.load();
      expect(m.required, 0);
    });
  });

  group('the screen', () {
    Future<void> pump(WidgetTester tester, LockController lock,
        {bool dark = false,
        double text = 1,
        Size size = const Size(360, 800)}) async {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1.0;
      tester.platformDispatcher.textScaleFactorTestValue = text;
      addTearDown(tester.view.reset);
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      await tester.pumpWidget(MaterialApp(
          theme: buildTheme(dark ? Brightness.dark : Brightness.light),
          home: AppLockGate(lock: lock, child: const Text('secure'))));
      await tester.pump();
      await tester.pump();
    }

    testWidgets('shows the update screen and never the secure area',
        (tester) async {
      final r = make(build: 5);
      r.monitor.observe('6');
      await pump(tester, r.lock);
      expect(find.byKey(const Key('update-required')), findsOneWidget);
      expect(find.text('secure'), findsNothing);
      expect(find.textContaining('older than the oldest one'), findsOneWidget);
      expect(find.byType(FilledButton), findsNothing,
          reason: 'no way in from here');
      expect(find.byType(TextField), findsNothing);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('an expired build says so in its own words', (tester) async {
      final r = make(build: 5, expires: '2020-01-01');
      await pump(tester, r.lock);
      expect(find.textContaining('end of its time'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('a message that arrives while the app is shown replaces it',
        (tester) async {
      final r = make(build: 5);
      await pump(tester, r.lock);
      await tester.pump();
      expect(find.text('secure'), findsOneWidget);
      r.monitor.observe('9');
      await tester.pump();
      expect(find.byKey(const Key('update-required')), findsOneWidget);
      expect(find.text('secure'), findsNothing);
      await tester.pumpWidget(const SizedBox());
    });

    for (final dark in [false, true]) {
      testWidgets('holds at 3x text, ${dark ? 'dark' : 'light'}',
          (tester) async {
        final semantics = tester.ensureSemantics();
        final r = make(build: 5);
        r.monitor.observe('6');
        await pump(tester, r.lock,
            dark: dark, text: 3, size: const Size(320, 568));
        expect(tester.takeException(), isNull);
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        semantics.dispose();
        await tester.pumpWidget(const SizedBox());
      });
    }
  });

  group('platform minimums and wiring', () {
    test('Android requires at least API 28, with the reason in the build file',
        () {
      final gradle = read('android/app/build.gradle.kts');
      expect(gradle, contains('minSdk = maxOf(28, flutter.minSdkVersion)'));
      expect(gradle, contains('StrongBox'));
    });

    test('iOS requires at least 15.0 in every configuration', () {
      final project = read('ios/Runner.xcodeproj/project.pbxproj');
      final targets = RegExp(r'IPHONEOS_DEPLOYMENT_TARGET = ([\d.]+);')
          .allMatches(project)
          .map((m) => double.parse(m.group(1)!))
          .toList();
      expect(targets, isNotEmpty);
      expect(targets.every((t) => t >= 15.0), isTrue);
    });

    test(
        'the pinned client reports to the monitor, and main hands the guard to the lock',
        () {
      expect(read('lib/security/pinning/pinned_http.dart'),
          matches(RegExp(r'MinimumBuildMonitor\.instance\s*\.wrap\(')));
      final main = read('lib/main.dart');
      expect('updates: UpdateGuard.forApp()'.allMatches(main).length, 2);
    });

    test(
        'the minimum cannot be lowered by a later answer, so the header cannot reopen the app',
        () {
      final m = MinimumBuildMonitor()..observe('10');
      m.observe('1');
      expect(UpdateGuard(monitor: m, build: 5).blocked, UpdateReason.tooOld);
    });
  });
}

class _SlowStatus implements BiometricAuthenticator {
  _SlowStatus(this.inner, this.gate);
  final FakeAuthenticator inner;
  final Completer<void> gate;

  @override
  Stream<void> get screenLocked => inner.screenLocked;

  @override
  Future<BiometricStatus> status() async {
    await gate.future;
    return inner.status();
  }

  @override
  Future<BiometricOutcome> authenticate(String reason) =>
      inner.authenticate(reason);
}

class _MemoryMinStore implements MinimumBuildStore {
  int value = 0;
  bool fail = false;

  @override
  Future<int> read() async {
    if (fail) throw StateError('unreadable');
    return value;
  }

  @override
  Future<void> write(int v) async {
    if (fail) throw StateError('full');
    value = v;
  }
}
