import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/history_screen.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/shell/app_shell.dart'
    show kRailMinHeight, railHeightNeeded;
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../approvals/approval_test.dart' show firstVector;
import '../settings/support.dart';

/// The app with an enrolled, active device and [pending] open requests.
class ShellRig {
  ShellRig({this.pending = 1, bool enrolled = true})
      : settings = SettingsRig(
            record: enrolled
                ? EnrollmentRecord(
                    kid: 'dev-1',
                    deviceToken: FakeSettingsOidc.token,
                    activeAt: DateTime.utc(2020),
                    attestation: 'att',
                    recoveryCodeShown: true)
                : null) {
    if (!enrolled) settings.enrollment.clear();
    final request = firstVector()['request'] as Map;
    approvals = ApprovalServices(
      client: ApprovalClient(
          baseUrl: 'http://oidc.test',
          client: MockClient((req) async {
            if (req.url.path == '/devices/requests') {
              return http.Response(
                  jsonEncode({
                    'requests': [for (var i = 0; i < pending; i++) request]
                  }),
                  200);
            }
            if (req.url.path.startsWith('/consent/requests/')) {
              return http.Response(jsonEncode(request), 200);
            }
            return http.Response('{"error":"not_found"}', 404);
          })),
      keys: settings.keys,
      enrollment: ObservableEnrollmentStore(settings.enrollment),
      gate: FakeGate(),
      history: MemoryHistoryStore(),
      clock: () =>
          DateTime.fromMillisecondsSinceEpoch(1792425620 * 1000, isUtc: true),
    );
  }

  final int pending;
  final SettingsRig settings;
  late final ApprovalServices approvals;

  /// The same services on another enrollment store.
  ApprovalServices approvalsWith(EnrollmentStore store) => ApprovalServices(
        client: approvals.client,
        keys: approvals.keys,
        enrollment: store is ObservableEnrollmentStore
            ? store
            : ObservableEnrollmentStore(store),
        gate: approvals.gate,
        history: approvals.history,
        clock: approvals.clock,
      );
  final shell = ShellController();

  String get requestId =>
      (firstVector()['request'] as Map)['auth_req_id'] as String;

  Future<void> pump(WidgetTester tester,
      {Size size = const Size(400, 900), AppConfig? config}) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(AuthenticatorApp(
      config: config ?? AppConfig.resolve(''),
      approvals: approvals,
      settings: settings.services,
      shell: shell,
    ));
    await tester.pump();
    await tester.pump();
  }

  /// Stops the pending list's polling timer, which would outlive the test.
  Future<void> dispose(WidgetTester tester) =>
      tester.pumpWidget(const SizedBox());
}

Finder inBar(String label) =>
    find.descendant(of: find.byType(NavigationBar), matching: find.text(label));

void main() {
  group('navigation shell', () {
    testWidgets(
        'shows five labelled destinations in the spec order, and opens on Approvals when a request is waiting',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester);
      final bar = tester.widget<NavigationBar>(find.byType(NavigationBar));
      expect(bar.destinations, hasLength(5));
      expect(bar.selectedIndex, AppTab.approvals.index);
      expect(AppTab.values.map((t) => t.label),
          ['Codes', 'Approvals', 'History', 'Devices', 'Settings']);
      expect([
        for (final d in bar.destinations) (d as NavigationDestination).label
      ], [
        'Codes',
        'Approvals',
        'History',
        'Devices',
        'Settings'
      ]);
      for (final tab in AppTab.values) {
        expect(inBar(tab.label), findsOneWidget, reason: tab.label);
        expect(find.byTooltip(tab.label), findsOneWidget, reason: tab.label);
      }
      expect(find.byIcon(Icons.pin_outlined), findsOneWidget);
      // The selected destination is also marked by a different (filled) icon, not by color alone.
      expect(find.byIcon(Icons.fact_check), findsOneWidget);
      expect(find.byIcon(Icons.receipt_long_outlined), findsOneWidget);
      expect(find.byType(PendingScreen), findsOneWidget);
      await rig.dispose(tester);
    });

    testWidgets('with nothing waiting, the shell opens on Codes',
        (tester) async {
      final rig = ShellRig(pending: 0);
      await rig.pump(tester);
      expect(rig.shell.selected, AppTab.codes);
      expect(find.byKey(const Key('codes-placeholder')), findsOneWidget);
      expect(
          tester
              .widget<NavigationBar>(find.byType(NavigationBar))
              .selectedIndex,
          0);
      expect(find.byIcon(Icons.pin), findsOneWidget);
      await rig.dispose(tester);
    });

    testWidgets(
        'the first load moves to Approvals only if nothing was chosen before',
        (tester) async {
      final c = ShellController();
      c.pendingLoaded(2);
      expect(c.selected, AppTab.approvals);
      expect(c.pendingCount.value, 2);
      // Only the first load decides: a later one that finds a request leaves the tab alone.
      final later = ShellController();
      later.pendingLoaded(0);
      later.pendingLoaded(2);
      expect(later.selected, AppTab.codes);
      expect(later.pendingCount.value, 2);
      later.dispose();
      // A tab the user chose is never taken from them.
      final chosen = ShellController()..select(AppTab.devices);
      chosen.pendingLoaded(3);
      expect(chosen.selected, AppTab.devices);
      // Tapping Codes on purpose is a choice too.
      final codes = ShellController()..select(AppTab.codes);
      codes.pendingLoaded(1);
      expect(codes.selected, AppTab.codes);
      c.dispose();
      chosen.dispose();
      codes.dispose();
    });

    test('a first load that comes after the window does not choose the tab', () {
      var now = DateTime.utc(2026, 11, 1, 12);
      final c = ShellController()..clock = () => now;
      c.sessionStarted();
      now = now
          .add(ShellController.firstLoadWindow + const Duration(seconds: 1));
      c.pendingLoaded(2);
      expect(c.selected, AppTab.codes);
      expect(c.pendingCount.value, 2);
      // Within the window it does.
      final quick = ShellController()..clock = () => now;
      quick.sessionStarted();
      quick.pendingLoaded(2);
      expect(quick.selected, AppTab.approvals);
      c.dispose();
      quick.dispose();
    });

    test('Approvals is not marked active before it is shown', () {
      final c = ShellController();
      expect(c.approvalsActive.value, isFalse);
      c.dispose();
    });

    testWidgets('a request that arrives later does not pull the user off Codes',
        (tester) async {
      final rig = ShellRig(pending: 0);
      await rig.pump(tester);
      rig.shell.pendingLoaded(1);
      await tester.pump();
      expect(rig.shell.selected, AppTab.codes);
      await rig.dispose(tester);
    });

    testWidgets('the Approvals badge shows how many requests are pending',
        (tester) async {
      final handle = tester.ensureSemantics();
      final rig = ShellRig(pending: 2);
      await rig.pump(tester);
      expect(rig.shell.pendingCount.value, 2);
      final badge = tester.widget<Badge>(find.byType(Badge));
      expect(badge.isLabelVisible, isTrue);
      expect((badge.label as Text).data, '2');
      expect(find.bySemanticsLabel(RegExp('2 pending')), findsOneWidget);
      await rig.dispose(tester);
      handle.dispose();
    });

    testWidgets('a tab is built when first shown, and keeps its state after',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester);
      expect(find.byType(HistoryScreen, skipOffstage: false), findsNothing);
      await tester.tap(inBar('History'));
      await tester.pumpAndSettle();
      expect(find.byType(HistoryScreen), findsOneWidget);
      expect(rig.shell.selected, AppTab.history);
      await tester.tap(inBar('Approvals'));
      await tester.pumpAndSettle();
      expect(find.byType(HistoryScreen), findsNothing);
      expect(find.byType(HistoryScreen, skipOffstage: false), findsOneWidget);
      await rig.dispose(tester);
    });

    testWidgets('each tab keeps its own stack while another tab shows',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester);
      await tester.tap(find.byKey(Key('pending-${rig.requestId}')));
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsOneWidget);
      expect(find.byType(NavigationBar), findsOneWidget);

      await tester.tap(inBar('History'));
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsNothing);
      await tester.tap(inBar('Approvals'));
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsOneWidget);

      // Choosing the tab that is showing goes back towards its first screen, but an open card may be half decided: it stays.
      await tester.tap(inBar('Approvals'));
      await tester.pump(const Duration(seconds: 1));
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsOneWidget);

      // Other screens are closed.
      await tester.tap(inBar('History'));
      await tester.pump(const Duration(seconds: 1));
      rig.shell.navigators[AppTab.history]!.currentState!.push(
          MaterialPageRoute<void>(
              builder: (_) => const Scaffold(body: Text('detail'))));
      await tester.pump(const Duration(seconds: 1));
      expect(find.text('detail'), findsOneWidget);
      await tester.tap(inBar('History'));
      await tester.pump(const Duration(seconds: 1));
      await tester.pump(const Duration(seconds: 1));
      expect(find.text('detail'), findsNothing);
      expect(find.byType(HistoryScreen), findsOneWidget);
      await rig.dispose(tester);
    });

    testWidgets('back goes up inside a tab, then to the first tab',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester);
      await tester.tap(find.byKey(Key('pending-${rig.requestId}')));
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsOneWidget);

      await tester.binding.handlePopRoute();
      await tester.pump(const Duration(seconds: 1));
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsNothing);
      expect(rig.shell.selected, AppTab.approvals);

      rig.shell.select(AppTab.devices);
      await tester.pump(const Duration(seconds: 1));
      await tester.binding.handlePopRoute();
      await tester.pump(const Duration(seconds: 1));
      expect(rig.shell.selected, AppTab.codes);
      await rig.dispose(tester);
    });

    testWidgets('a wide window shows a navigation rail instead of the bar',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester, size: const Size(1100, 800));
      expect(find.byType(NavigationRail), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);
      for (final tab in AppTab.values) {
        expect(
            find.descendant(
                of: find.byType(NavigationRail),
                matching: find.text(tab.label)),
            findsOneWidget,
            reason: tab.label);
      }
      await tester.tap(find.descendant(
          of: find.byType(NavigationRail), matching: find.text('History')));
      await tester.pumpAndSettle();
      expect(find.byType(HistoryScreen), findsOneWidget);
      await rig.dispose(tester);
    });

    testWidgets('a phone-sized window shows the bar and no rail',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester, size: const Size(839, 900));
      expect(find.byType(NavigationBar), findsOneWidget);
      expect(find.byType(NavigationRail), findsNothing);
      await rig.dispose(tester);
    });

    testWidgets('the bar holds at 3x text and in dark mode', (tester) async {
      final rig = ShellRig();
      tester.platformDispatcher.textScaleFactorTestValue = 3.0;
      tester.platformDispatcher.platformBrightnessTestValue = Brightness.dark;
      addTearDown(tester.platformDispatcher.clearAllTestValues);
      await rig.pump(tester, size: const Size(360, 800));
      expect(find.byType(NavigationBar), findsOneWidget);
      expect(tester.takeException(), isNull);
      await rig.dispose(tester);
    });

    testWidgets('a device that is not enrolled sees enrollment, with no bar',
        (tester) async {
      final rig = ShellRig(enrolled: false);
      await rig.pump(tester);
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);
      expect(find.byType(NavigationRail), findsNothing);
      await rig.dispose(tester);
    });

    testWidgets(
        'a device that has not saved its recovery code is usable, with a notice that opens the code',
        (tester) async {
      final rig = ShellRig();
      await rig.settings.enrollment.write(EnrollmentRecord(
          kid: 'dev-1',
          deviceToken: FakeSettingsOidc.token,
          activeAt: DateTime.utc(2020),
          attestation: 'att'));
      await rig.pump(tester);
      expect(find.byType(NavigationBar), findsOneWidget);
      expect(find.byType(PendingScreen), findsOneWidget);
      expect(find.text('Save your recovery code'), findsOneWidget);

      await tester.tap(find.text('Show code'));
      // The code is being fetched, so a spinner runs and the tree never settles.
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      await rig.dispose(tester);
    });

    testWidgets(
        'the notice opens the code screen once, however often it is tapped',
        (tester) async {
      final rig = ShellRig();
      await rig.settings.enrollment.write(EnrollmentRecord(
          kid: 'dev-1',
          deviceToken: FakeSettingsOidc.token,
          activeAt: DateTime.utc(2020),
          attestation: 'att'));
      await rig.pump(tester);
      await tester.tap(find.text('Show code'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      await tester.tap(find.text('Show code'), warnIfMissed: false);
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      final nav = rig.shell.navigators[AppTab.approvals]!.currentState!;
      nav.pop();
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      expect(nav.canPop(), isFalse, reason: 'one screen was opened, not two');
      await rig.dispose(tester);
    });

    testWidgets('a device that saved it has no notice', (tester) async {
      final rig = ShellRig();
      await rig.pump(tester);
      expect(find.text('Save your recovery code'), findsNothing);
      await rig.dispose(tester);
    });
  });

  group('ShellController', () {
    test('select, showApprovals and the badge notify listeners', () {
      final c = ShellController();
      var n = 0;
      c.addListener(() => n++);
      c.select(AppTab.devices);
      expect(c.selected, AppTab.devices);
      c.showApprovals();
      expect(c.selected, AppTab.approvals);
      expect(n, 2);
      // No navigator is mounted: opening a card is a no-op, not a crash.
      c.openApproval(ShellRig().approvals, 'x');
      expect(c.selected, AppTab.approvals);
      c.dispose();
    });

    test('every destination has a label and two different icons', () {
      expect(AppTab.values.map((t) => t.label).toSet(), hasLength(5));
      for (final t in AppTab.values) {
        expect(t.icon, isNot(t.selectedIcon), reason: t.label);
      }
    });
  });

  testWidgets(
      'a build on the mock Biatec OIDC says so, one on a real server does not',
      (tester) async {
    final mock = ShellRig();
    await mock.pump(tester);
    expect(find.byKey(const Key('mock-notice')), findsOneWidget);
    expect(find.textContaining(AppConfig.mockOidcBaseUrl), findsOneWidget);
    expect(find.byType(NavigationBar), findsOneWidget);
    await mock.dispose(tester);

    final real = ShellRig();
    await real.pump(tester, config: AppConfig.resolve('https://oidc.example'));
    expect(find.byKey(const Key('mock-notice')), findsNothing);
    await real.dispose(tester);
  });

  testWidgets(
      'the rail needs more height at large text, so a short window keeps the bar',
      (tester) async {
    expect(railHeightNeeded(1), kRailMinHeight);
    expect(railHeightNeeded(3), greaterThan(kRailMinHeight));
    final rig = ShellRig();
    tester.platformDispatcher.textScaleFactorTestValue = 3.0;
    addTearDown(tester.platformDispatcher.clearAllTestValues);
    await rig.pump(tester, size: Size(1000, railHeightNeeded(3) - 20));
    expect(find.byType(NavigationBar), findsOneWidget);
    expect(find.byType(NavigationRail), findsNothing);
    await rig.dispose(tester);

    final tall = ShellRig();
    await tall.pump(tester, size: Size(1000, railHeightNeeded(3) + 20));
    expect(find.byType(NavigationRail), findsOneWidget);
    await tall.dispose(tester);
  });

  testWidgets('the bar grows with the text so its labels fit', (tester) async {
    final rig = ShellRig();
    await rig.pump(tester, size: const Size(400, 900));
    final normal = tester.getSize(find.byType(NavigationBar)).height;
    await rig.dispose(tester);

    final large = ShellRig();
    tester.platformDispatcher.textScaleFactorTestValue = 3.0;
    addTearDown(tester.platformDispatcher.clearAllTestValues);
    await large.pump(tester, size: const Size(400, 900));
    final big = tester.getSize(find.byType(NavigationBar)).height;
    expect(big, greaterThan(normal));
    expect(tester.takeException(), isNull);
    await large.dispose(tester);
  });

  testWidgets('a new shell after the old one went starts on Codes again',
      (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    rig.shell.select(AppTab.settings);
    await tester.pump();
    await tester.pumpWidget(const SizedBox());
    expect(rig.shell.selected, AppTab.codes);
    expect(rig.shell.approvalsActive.value, isFalse);
    // And it may move to Approvals again when its own first load finds a request.
    rig.shell.pendingLoaded(1);
    expect(rig.shell.selected, AppTab.approvals);
  });
}
