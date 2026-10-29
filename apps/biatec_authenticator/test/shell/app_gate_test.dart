import 'dart:async';
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/history_screen.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
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

/// A store whose reads do not answer until [release] is called.
class _HangingStore extends MemoryEnrollmentStore {
  final _gate = Completer<void>();
  void release() => _gate.complete();

  @override
  Future<EnrollmentRecord?> read() async {
    if (!_gate.isCompleted) await _gate.future;
    return super.read();
  }
}

/// A controller whose refresh (the part that may use the network) is slow.
/// Counts reads.
class _CountingReads extends MemoryEnrollmentStore {
  var reads = 0;

  @override
  Future<EnrollmentRecord?> read() {
    reads++;
    return super.read();
  }
}

class _SlowEnrollment extends EnrollmentController {
  _SlowEnrollment(ShellRig rig, {EnrollmentStore? store})
      : super(
          keys: rig.approvals.keys,
          store: store ?? rig.approvals.enrollment,
          client: OidcEnrollmentClient(baseUrl: 'http://oidc.test'),
        );

  final _gate = Completer<void>();
  void release() => _gate.complete();

  /// The state is known from the store at once; what follows (the recovery code, from the network) is slow.
  @override
  Future<void> refresh() async {
    await super.refresh();
    if (!_gate.isCompleted) await _gate.future;
  }
}

void main() {
  group('AppGate', () {
    testWidgets(
        'a keystore that does not answer ends in a retry, not an endless spinner',
        (tester) async {
      final rig = ShellRig();
      final store = _HangingStore();
      await store.write(EnrollmentRecord(
          kid: 'dev-1',
          deviceToken: 'dev-token',
          activeAt: DateTime.utc(2020),
          attestation: 'att',
          recoveryCodeShown: true));
      tester.view.physicalSize = const Size(400, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          home: AppGate(
        config: AppConfig.resolve(''),
        approvals: rig.approvalsWith(store),
        settings: rig.settings.services,
        shell: rig.shell,
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
      store.release();
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.byType(NavigationBar), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets(
        'slow work after the store was read (the network) is waited for, not blamed on storage',
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
      await tester.pump(const Duration(seconds: 10));
      expect(find.byType(ErrorState), findsNothing);
      expect(find.byType(NavigationBar), findsOneWidget,
          reason: 'the app opens as soon as the state is known');
      enrollment.release();
      await tester.pump();
      expect(find.byType(NavigationBar), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('start-up reads the stored enrollment once', (tester) async {
      final rig = ShellRig(enrolled: false);
      final store = _CountingReads();
      await tester.pumpWidget(MaterialApp(
          home: AppGate(
        config: AppConfig.resolve(''),
        approvals: rig.approvalsWith(store),
        settings: rig.settings.services,
        shell: rig.shell,
      )));
      await tester.pump();
      await tester.pump();
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      expect(store.reads, lessThanOrEqualTo(2),
          reason: 'the gate leaves reading to the controller; the enrollment '
              'screen refreshes once more');
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets(
        'a sign-out while the start-up refresh is still at the network is not a storage problem',
        (tester) async {
      final rig = ShellRig();
      final store = ObservableEnrollmentStore(rig.settings.enrollment);
      final enrollment = _SlowEnrollment(rig, store: store);
      await tester.pumpWidget(MaterialApp(
          home: AppGate(
        config: AppConfig.resolve(''),
        approvals: rig.approvalsWith(store),
        settings: rig.settings.services,
        shell: rig.shell,
        enrollment: enrollment,
      )));
      await tester.pump();
      expect(find.byType(NavigationBar), findsOneWidget);
      await store.clear();
      enrollment.release();
      await tester.pump();
      await tester.pump();
      expect(find.byType(ErrorState), findsNothing);
      expect(find.byType(EnrollmentScreen), findsOneWidget);
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
        enrollment: ObservableEnrollmentStore(settings.enrollment),
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
      expect(find.byKey(const Key('pending-stale')), findsOneWidget,
          reason: 'a banner over the list that is still shown');

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
