import 'dart:async';
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/history_screen.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/shell/app_gate.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:biatec_authenticator/ui/ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../approvals/approval_test.dart' show firstVector;
import '../settings/support.dart';
import 'app_shell_test.dart' show ShellRig;

/// A controller whose first refresh never answers until [release] is called.
class _SlowEnrollment extends EnrollmentController {
  _SlowEnrollment(ShellRig rig)
      : super(
          keys: rig.approvals.keys,
          store: rig.approvals.enrollment,
          client: OidcEnrollmentClient(baseUrl: 'http://oidc.test'),
        );

  final _gate = Completer<void>();
  void release() => _gate.complete();

  @override
  Future<void> refresh() async {
    if (!_gate.isCompleted) await _gate.future;
    return super.refresh();
  }
}

void main() {
  group('AppGate', () {
    testWidgets(
        'a keystore that does not answer ends in a retry, not an endless spinner',
        (tester) async {
      final rig = ShellRig();
      final enrollment = _SlowEnrollment(rig);
      tester.view.physicalSize = const Size(400, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          home: AppGate(
        config: AppConfig.resolve(''),
        approvals: rig.approvals,
        settings: rig.settings.services,
        shell: rig.shell,
        enrollment: enrollment,
        startupTimeout: const Duration(seconds: 3),
      )));
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      await tester.pump(const Duration(seconds: 4));
      expect(find.byType(ErrorState), findsOneWidget);
      expect(find.text('Still checking this device'), findsOneWidget);
      expect(find.text('Try again'), findsOneWidget);

      // Trying again waits again, and an answer that arrives at last opens the app.
      await tester.tap(find.text('Try again'));
      await tester.pump();
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      enrollment.release();
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.byType(NavigationBar), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets(
        'a new shell shows the tab the controller is on, and polls for it',
        (tester) async {
      final rig = ShellRig();
      rig.shell.select(AppTab.history);
      await rig.pump(tester);
      expect(find.byType(HistoryScreen), findsOneWidget);
      expect(rig.shell.approvalsActive.value, isFalse);
      await rig.dispose(tester);
    });
  });

  group('the Approvals badge when the connection fails', () {
    testWidgets('keeps the last known count, dropping requests that expired',
        (tester) async {
      final request =
          Map<String, Object?>.from(firstVector()['request'] as Map);
      final expiresAt = request['expiry'] as int?;
      var online = true;
      var nowSeconds = 1792425620;
      final settings = SettingsRig();
      final approvals = ApprovalServices(
        client: ApprovalClient(
            baseUrl: 'http://oidc.test',
            client: MockClient((req) async {
              if (!online) throw http.ClientException('network is unreachable');
              if (req.url.path == '/devices/requests') {
                return http.Response(
                    jsonEncode({
                      'requests': [request]
                    }),
                    200);
              }
              return http.Response('{"error":"not_found"}', 404);
            })),
        keys: settings.keys,
        enrollment: settings.enrollment,
        gate: settings.gate,
        history: MemoryHistoryStore(),
        clock: () =>
            DateTime.fromMillisecondsSinceEpoch(nowSeconds * 1000, isUtc: true),
      );
      final counts = <int>[];
      await tester.pumpWidget(MaterialApp(
          home: PendingScreen(
              services: approvals,
              pollEvery: const Duration(seconds: 5),
              onCount: counts.add)));
      await tester.pump();
      await tester.pump();
      expect(counts, [1]);

      online = false;
      await tester.pump(const Duration(seconds: 5));
      await tester.pump();
      expect(counts.last, 1, reason: 'offline: the last known state stands');
      expect(find.byKey(const Key('pending-error')), findsOneWidget);

      // The request expires while offline: it is no longer counted.
      expect(expiresAt, isNotNull);
      nowSeconds = expiresAt! + 1;
      await tester.pump(const Duration(seconds: 5));
      await tester.pump();
      expect(counts.last, 0);
      await tester.pumpWidget(const SizedBox());
    });
  });
}
