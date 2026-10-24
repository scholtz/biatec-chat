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
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
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
      enrollment: settings.enrollment,
      gate: FakeGate(),
      history: MemoryHistoryStore(),
      clock: () =>
          DateTime.fromMillisecondsSinceEpoch(1792425620 * 1000, isUtc: true),
    );
  }

  final int pending;
  final SettingsRig settings;
  late final ApprovalServices approvals;
  final shell = ShellController();

  String get requestId =>
      (firstVector()['request'] as Map)['auth_req_id'] as String;

  Future<void> pump(WidgetTester tester,
      {Size size = const Size(400, 900)}) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(AuthenticatorApp(
      config: AppConfig.resolve(''),
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
    testWidgets('opens on Approvals with five labelled destinations',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester);
      final bar = tester.widget<NavigationBar>(find.byType(NavigationBar));
      expect(bar.destinations, hasLength(5));
      expect(bar.selectedIndex, 0);
      for (final tab in AppTab.values) {
        expect(inBar(tab.label), findsOneWidget, reason: tab.label);
        expect(find.byTooltip(tab.label), findsOneWidget, reason: tab.label);
      }
      // The selected destination is also marked by a different (filled) icon, not by color alone.
      expect(find.byIcon(Icons.fact_check), findsOneWidget);
      expect(find.byIcon(Icons.receipt_long_outlined), findsOneWidget);
      expect(find.byType(PendingScreen), findsOneWidget);
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

      // Choosing the tab that is showing returns it to its first screen.
      await tester.tap(inBar('Approvals'));
      await tester.pump(const Duration(seconds: 1));
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsNothing);
      expect(find.byType(PendingScreen), findsOneWidget);
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
      expect(rig.shell.selected, AppTab.approvals);
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
        'an enrolled device that must still save its recovery code stays in enrollment',
        (tester) async {
      final rig = ShellRig();
      await rig.settings.enrollment.write(EnrollmentRecord(
          kid: 'dev-1',
          deviceToken: FakeSettingsOidc.token,
          activeAt: DateTime.utc(2020),
          attestation: 'att'));
      await rig.pump(tester);
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);
      await rig.dispose(tester);
    });
  });

  group('ShellController', () {
    test('select, showApprovals and the badge notify listeners', () {
      final c = ShellController();
      var n = 0;
      c.addListener(() => n++);
      c.select(AppTab.clients);
      expect(c.selected, AppTab.clients);
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
}
