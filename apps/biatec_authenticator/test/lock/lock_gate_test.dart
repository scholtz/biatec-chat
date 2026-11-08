import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/history_screen.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/clients/clients_screen.dart';
import 'package:biatec_authenticator/codes/codes_screen.dart';
import 'package:biatec_authenticator/devices/devices_screen.dart';
import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:biatec_authenticator/lock/lock_screens.dart';
import 'package:biatec_authenticator/settings/settings_screen.dart';
import 'package:biatec_authenticator/shell/app_shell.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../shell/app_shell_test.dart' show ShellRig;
import 'support.dart';

LockController newLock(FakeAuthenticator auth, {DateTime Function()? clock}) {
  final lock = LockController(
      authenticator: auth, store: MemoryLockStore(), clock: clock);
  addTearDown(lock.dispose);
  return lock;
}

/// Finds none of the screens of the secure area.
void expectNoSecureArea(WidgetTester tester) {
  for (final type in [
    AppShell,
    NavigationBar,
    CodesScreen,
    PendingScreen,
    HistoryScreen,
    DevicesScreen,
    SettingsScreen,
    ClientsScreen,
    ApprovalScreen,
  ]) {
    expect(find.byType(type, skipOffstage: false), findsNothing,
        reason: '$type is not in the widget tree while locked');
  }
}

void main() {
  group('while locked the widget tree holds no secure-area screen', () {
    for (final outcome in [
      BiometricOutcome.failed,
      BiometricOutcome.cancelled,
      BiometricOutcome.lockout,
    ]) {
      testWidgets('a start that ends in $outcome', (tester) async {
        final auth = FakeAuthenticator()..outcome = outcome;
        final rig = ShellRig();
        await rig.pump(tester, lock: newLock(auth));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('lock-screen')), findsOneWidget);
        expectNoSecureArea(tester);
        // No count, name or code: the screen says only what it is.
        expect(find.text('Approvals'), findsNothing);
        expect(find.text('Codes'), findsNothing);
        await rig.dispose(tester);
      });
    }

    testWidgets('the lock screen has one button and no other way in',
        (tester) async {
      final auth = FakeAuthenticator()..outcome = BiometricOutcome.cancelled;
      final rig = ShellRig();
      await rig.pump(tester, lock: newLock(auth));
      await tester.pumpAndSettle();
      expect(find.byType(FilledButton), findsOneWidget);
      expect(find.byType(OutlinedButton), findsNothing);
      expect(find.byType(TextButton), findsNothing);
      expect(find.byType(TextField), findsNothing, reason: 'no PIN entry');
      await rig.dispose(tester);
    });

    testWidgets('a biometric success shows the app, and locking hides it again',
        (tester) async {
      final auth = FakeAuthenticator();
      final lock = newLock(auth);
      final rig = ShellRig();
      await rig.pump(tester, lock: lock);
      await tester.pumpAndSettle();
      expect(find.byType(NavigationBar), findsOneWidget);
      expect(find.byKey(const Key('lock-screen')), findsNothing);

      auth.outcome = BiometricOutcome.cancelled;
      lock.lock();
      await tester.pumpAndSettle();
      expectNoSecureArea(tester);
      expect(find.byKey(const Key('lock-screen')), findsOneWidget);

      auth.outcome = BiometricOutcome.success;
      await tester.tap(find.byKey(const Key('unlock')));
      await tester.pumpAndSettle();
      expect(find.byType(NavigationBar), findsOneWidget);
      await rig.dispose(tester);
    });

    testWidgets('a failed try says so and can be tried again', (tester) async {
      final auth = FakeAuthenticator()..outcome = BiometricOutcome.failed;
      final rig = ShellRig();
      await rig.pump(tester, lock: newLock(auth));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('lock-problem')), findsOneWidget);
      expect(find.textContaining('did not match'), findsOneWidget);
      auth.outcome = BiometricOutcome.success;
      await tester.tap(find.byKey(const Key('unlock')));
      await tester.pumpAndSettle();
      expect(find.byType(NavigationBar), findsOneWidget);
      await rig.dispose(tester);
    });

    testWidgets('the screen locking while the app is open locks it',
        (tester) async {
      final auth = FakeAuthenticator();
      final lock = newLock(auth);
      final rig = ShellRig();
      await rig.pump(tester, lock: lock);
      await tester.pumpAndSettle();
      auth.outcome = BiometricOutcome.cancelled;
      auth.screenLocks();
      await tester.pumpAndSettle();
      expectNoSecureArea(tester);
      await rig.dispose(tester);
    });

    testWidgets('coming back after the delay asks for the fingerprint again',
        (tester) async {
      final auth = FakeAuthenticator();
      var now = DateTime.utc(2026, 11, 2, 10);
      final lock = newLock(auth, clock: () => now);
      final rig = ShellRig();
      await rig.pump(tester, lock: lock);
      await tester.pumpAndSettle();
      final asked = auth.prompts;
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
      now = now.add(const Duration(seconds: 31));
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pumpAndSettle();
      expect(auth.prompts, asked + 1, reason: 'a prompt on coming back');
      expect(find.byType(NavigationBar), findsOneWidget,
          reason: 'the fake biometric passes');
      await rig.dispose(tester);
    });

    testWidgets('two idle minutes lock it, and touches keep it open',
        (tester) async {
      final auth = FakeAuthenticator();
      var now = DateTime.utc(2026, 11, 2, 10);
      final lock = newLock(auth, clock: () => now);
      final rig = ShellRig();
      await rig.pump(tester, lock: lock);
      await tester.pumpAndSettle();
      auth.outcome = BiometricOutcome.cancelled;

      now = now.add(const Duration(minutes: 1, seconds: 50));
      await tester.tapAt(const Offset(5, 5));
      now = now.add(const Duration(minutes: 1, seconds: 50));
      await tester.pump(const Duration(seconds: 6));
      expect(find.byType(NavigationBar), findsOneWidget,
          reason: 'a touch started the two minutes again');

      now = now.add(const Duration(minutes: 2));
      await tester.pump(const Duration(seconds: 6));
      await tester.pumpAndSettle();
      expectNoSecureArea(tester);
      await rig.dispose(tester);
    });
  });

  group('a tapped notification on a locked app', () {
    testWidgets(
        'shows the lock screen, and the card opens after the unlock; only the id is kept',
        (tester) async {
      final auth = FakeAuthenticator()..outcome = BiometricOutcome.cancelled;
      final lock = newLock(auth);
      final rig = ShellRig();
      await rig.pump(tester, lock: lock);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('lock-screen')), findsOneWidget);

      // The notification is tapped while locked.
      rig.shell.canQueue = true;
      rig.shell.openApproval(rig.approvals, rig.requestId);
      await tester.pumpAndSettle();
      expectNoSecureArea(tester);
      expect(find.byKey(const Key('lock-screen')), findsOneWidget);

      auth.outcome = BiometricOutcome.success;
      await tester.tap(find.byKey(const Key('unlock')));
      await tester.pumpAndSettle();
      expect(find.byType(ApprovalScreen), findsOneWidget,
          reason: 'the card opens once the user is in');
      await rig.dispose(tester);
    });
  });

  group('the other screens', () {
    testWidgets('no biometric enrolled says how to enroll, and nothing else',
        (tester) async {
      final auth = FakeAuthenticator()
        ..statusResult = BiometricStatus.noneEnrolled;
      final rig = ShellRig();
      await rig.pump(tester, lock: newLock(auth));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('no-biometric')), findsOneWidget);
      expect(find.textContaining('fingerprint or face'), findsWidgets);
      expect(find.textContaining('Settings'), findsWidgets,
          reason: 'it says where to enroll');
      expectNoSecureArea(tester);
      expect(find.byType(FilledButton), findsOneWidget,
          reason: 'only Check again');
      await rig.dispose(tester);
    });

    testWidgets('biometrics changed says what happened and what to do',
        (tester) async {
      final auth = FakeAuthenticator();
      final lock = newLock(auth);
      final rig = ShellRig();
      await rig.pump(tester, lock: lock);
      await tester.pumpAndSettle();
      lock.markBiometricsChanged();
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('biometrics-changed')), findsOneWidget);
      expect(find.textContaining('added or removed'), findsOneWidget);
      expect(find.textContaining('Restore your two-factor accounts'),
          findsOneWidget);
      expect(find.textContaining('Enroll this device again'), findsOneWidget);
      expectNoSecureArea(tester);
      // The one action is restoring a backup into a new vault: it opens no
      // account and shows no code.
      expect(find.byType(FilledButton), findsOneWidget,
          reason: 'only restore from a backup');
      expect(find.byKey(const Key('restore-from-backup')), findsOneWidget);
      await rig.dispose(tester);
    });

    testWidgets('compromised phone: said, nothing opens', (tester) async {
      final auth = FakeAuthenticator();
      final lock = newLock(auth);
      final rig = ShellRig();
      await rig.pump(tester, lock: lock);
      await tester.pumpAndSettle();
      lock.markCompromised();
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('compromised')), findsOneWidget);
      expectNoSecureArea(tester);
      await rig.dispose(tester);
    });

    for (final dark in [false, true]) {
      for (final screen in ['lock', 'no-biometric', 'changed', 'compromised']) {
        testWidgets('$screen holds at 3x text, ${dark ? 'dark' : 'light'}',
            (tester) async {
          final semantics = tester.ensureSemantics();
          tester.view.physicalSize = const Size(320, 568);
          tester.view.devicePixelRatio = 1.0;
          tester.platformDispatcher.textScaleFactorTestValue = 3;
          addTearDown(tester.view.reset);
          addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
          final auth = FakeAuthenticator()..outcome = BiometricOutcome.failed;
          final lock = newLock(auth);
          final Widget body = switch (screen) {
            'lock' => LockScreen(lock: lock),
            'no-biometric' => NoBiometricScreen(lock: lock),
            'changed' => const BiometricsChangedScreen(),
            _ => CompromisedScreen(lock: lock),
          };
          await tester.pumpWidget(MaterialApp(
              theme: buildTheme(dark ? Brightness.dark : Brightness.light),
              home: body));
          await tester.pumpAndSettle();
          expect(tester.takeException(), isNull);
          await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
          await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
          semantics.dispose();
        });
      }
    }
  });

  group('settings', () {
    testWidgets('the lock delay can be changed after a fresh biometric',
        (tester) async {
      final auth = FakeAuthenticator();
      final lock = newLock(auth);
      await lock.unlock();
      final rig = ShellRig();
      await rig.pump(tester, size: const Size(400, 2400), lock: lock);
      await tester.pumpAndSettle();
      await tester.tap(find.descendant(
          of: find.byType(NavigationBar), matching: find.text('Settings')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('lock-delay')), findsOneWidget);
      expect(find.textContaining('After 30 seconds'), findsOneWidget);
      await tester.tap(find.byKey(const Key('lock-delay')));
      await tester.pumpAndSettle();
      expect(find.text('After 5 minutes'), findsOneWidget);
      expect(find.textContaining('Never'), findsNothing);
      final before = auth.prompts;
      await tester.tap(find.byKey(const Key('delay-minutes5')));
      await tester.pumpAndSettle();
      expect(auth.prompts, before + 1);
      expect(lock.delay, LockDelay.minutes5);
      expect(find.textContaining('After 5 minutes'), findsOneWidget);
      await rig.dispose(tester);
    });
  });
}
