import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'app_shell_test.dart' show ShellRig;

final _active = EnrollmentRecord(
    kid: 'dev-1',
    deviceToken: 'dev-token',
    activeAt: DateTime.utc(2020),
    attestation: 'att',
    recoveryCodeShown: true);

/// A store that cannot be read until [broken] is cleared.
class _BrokenStore extends MemoryEnrollmentStore {
  bool broken = true;

  @override
  Future<EnrollmentRecord?> read() async {
    if (broken) throw StateError('keystore locked');
    return super.read();
  }
}

ApprovalServices _servicesWith(ShellRig rig, EnrollmentStore store) =>
    ApprovalServices(
      client: rig.approvals.client,
      keys: rig.approvals.keys,
      enrollment: store,
      gate: rig.approvals.gate,
      history: MemoryHistoryStore(),
      clock: rig.approvals.clock,
    );

Future<void> _pumpApp(
    WidgetTester tester, ShellRig rig, ApprovalServices approvals) async {
  tester.view.physicalSize = const Size(400, 900);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(AuthenticatorApp(
    config: AppConfig.resolve(''),
    approvals: approvals,
    settings: rig.settings.services,
    shell: rig.shell,
  ));
  await tester.pump();
  await tester.pump();
  await tester.pump();
}

void main() {
  group('signing out and storage failures', () {
    testWidgets(
        'a store that announces its change sends a signed-out device to enrollment at once',
        (tester) async {
      final rig = ShellRig();
      final store = ObservableEnrollmentStore(rig.settings.enrollment);
      await _pumpApp(tester, rig, _servicesWith(rig, store));
      expect(find.byType(NavigationBar), findsOneWidget);

      // Devices cancelling this device clears the store; no tab change follows.
      await store.clear();
      expect(store.clearCount, 1);
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);
      expect(await store.read(), isNull);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets(
        'a store that cannot be read gives a retry, not the enrollment flow',
        (tester) async {
      final rig = ShellRig();
      final broken = _BrokenStore();
      await broken.write(EnrollmentRecord(
          kid: 'dev-1',
          deviceToken: 'dev-token',
          activeAt: DateTime.utc(2020),
          attestation: 'att',
          recoveryCodeShown: true));
      await _pumpApp(tester, rig, _servicesWith(rig, broken));
      expect(find.text('Could not read this device'), findsOneWidget);
      expect(find.byType(EnrollmentScreen), findsNothing);

      broken.broken = false;
      await tester.tap(find.text('Try again'));
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.byType(NavigationBar), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });
  });

  testWidgets(
      'an enrollment kept in memory after a failed write is not mistaken for a sign-out',
      (tester) async {
    final rig = ShellRig();
    final inner = MemoryEnrollmentStore();
    await inner.write(_active);
    final store = ObservableEnrollmentStore(inner);
    await _pumpApp(tester, rig, _servicesWith(rig, store));
    expect(find.byType(NavigationBar), findsOneWidget);

    // The stored copy is gone without anyone clearing it (a write that never landed).
    await inner.clear();
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pump();
    await tester.pump();
    await tester.pump();
    expect(find.byType(NavigationBar), findsOneWidget);
    expect(find.byType(EnrollmentScreen), findsNothing);
    expect((await inner.read())?.kid, 'dev-1',
        reason: 'the controller wrote its record back');
    await tester.pumpWidget(const SizedBox());
  });

  group('ObservableEnrollmentStore', () {
    test('tells listeners about writes and clears, and passes the rest through',
        () async {
      final store = ObservableEnrollmentStore(MemoryEnrollmentStore());
      var n = 0;
      store.addListener(() => n++);
      final record = EnrollmentRecord(
          kid: 'k', deviceToken: 't', activeAt: DateTime.utc(2020));
      await store.write(record);
      expect(n, 1);
      expect((await store.read())?.kid, 'k');
      await store.writeAlias('alias');
      expect(await store.readAlias(), 'alias');
      await store.clearAlias();
      expect(await store.readAlias(), isNull);
      expect(n, 1, reason: 'aliases are not the enrollment');
      await store.clear();
      expect(n, 2);
      expect(store.clearCount, 1);
      expect(await store.read(), isNull);
      store.dispose();
    });
  });

  group('queued notification', () {
    testWidgets('one that waited too long is not opened', (tester) async {
      final rig = ShellRig();
      var now = DateTime.utc(2026, 10, 24, 12);
      rig.shell.clock = () => now;
      rig.shell.openApproval(rig.approvals, rig.requestId);
      now = now.add(ShellController.queuedOpenTtl + const Duration(seconds: 1));
      await rig.pump(tester);
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsNothing);
      await rig.dispose(tester);
    });

    testWidgets('one that was dropped is not opened', (tester) async {
      final rig = ShellRig();
      rig.shell.openApproval(rig.approvals, rig.requestId);
      rig.shell.dropQueuedOpen();
      await rig.pump(tester);
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsNothing);
      await rig.dispose(tester);
    });

    testWidgets('a launch that ends in enrollment forgets it', (tester) async {
      final rig = ShellRig(enrolled: false);
      rig.shell.openApproval(rig.approvals, rig.requestId);
      await rig.pump(tester);
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      // Were it still queued, the shell would open it as soon as it is shown.
      rig.shell.openQueued();
      expect(rig.shell.navigators[AppTab.approvals]!.currentState, isNull);
      await rig.dispose(tester);
    });
  });

  group('layout', () {
    testWidgets('a phone on its side keeps the bar: the rail needs height',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester, size: const Size(900, 400));
      expect(find.byType(NavigationBar), findsOneWidget);
      expect(find.byType(NavigationRail), findsNothing);
      await rig.dispose(tester);
    });

    testWidgets('the bar steps aside while the keyboard is open',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester);
      expect(find.byType(NavigationBar), findsOneWidget);
      tester.view.viewInsets = const FakeViewPadding(bottom: 300);
      addTearDown(tester.view.resetViewInsets);
      await tester.pump();
      expect(find.byType(NavigationBar), findsNothing);
      tester.view.resetViewInsets();
      await tester.pump();
      expect(find.byType(NavigationBar), findsOneWidget);
      await rig.dispose(tester);
    });
  });

  testWidgets(
      'status colors follow the brightness when the theme has no extension',
      (tester) async {
    late StatusColors seen;
    await tester.pumpWidget(MaterialApp(
        theme: ThemeData(brightness: Brightness.dark),
        home: Builder(builder: (context) {
          seen = context.statusColors;
          return const SizedBox();
        })));
    expect(seen.danger, StatusColors.dark.danger);
  });
}
