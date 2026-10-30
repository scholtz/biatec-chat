import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'app_shell_test.dart' show ShellRig;

/// Fails its first refresh, like a keystore that is not ready yet.
class _FlakyEnrollment extends EnrollmentController {
  _FlakyEnrollment(ShellRig rig)
      : super(
          keys: rig.approvals.keys,
          store: rig.approvals.enrollment,
          client: OidcEnrollmentClient(baseUrl: 'http://oidc.test'),
        );

  int calls = 0;

  @override
  Future<void> refresh() async {
    if (calls++ == 0) throw StateError('keystore not ready');
    return super.refresh();
  }
}

void main() {
  testWidgets(
      'a notification tapped before the shell exists opens once it does',
      (tester) async {
    final rig = ShellRig();
    // The app was started by the tap: nothing is mounted yet.
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await rig.pump(tester);
    await tester.pump(const Duration(seconds: 1));
    final screen = tester.widget<ApprovalScreen>(find.byType(ApprovalScreen));
    expect(screen.authReqId, rig.requestId);
    expect(rig.shell.selected, AppTab.approvals);
    expect(find.byType(NavigationBar), findsOneWidget);
    // It is opened once, not again on the next shell.
    rig.shell.openQueued();
    await tester.pump(const Duration(seconds: 1));
    expect(find.byType(ApprovalScreen), findsOneWidget);
    await rig.dispose(tester);
  });

  testWidgets(
      'a count left by an earlier shell is cleared when a new one appears',
      (tester) async {
    final rig = ShellRig(pending: 0);
    rig.shell.pendingCount.value = 7;
    await rig.pump(tester);
    expect(rig.shell.pendingCount.value, 0);
    await rig.dispose(tester);
  });

  testWidgets('the pending list polls fast on its tab and slowly on the others',
      (tester) async {
    final rig = ShellRig();
    final active = ValueNotifier(true);
    var polls = 0;
    await tester.pumpWidget(MaterialApp(
        home: PendingScreen(
            services: rig.approvals,
            pollEvery: const Duration(seconds: 5),
            backgroundEvery: const Duration(seconds: 20),
            active: active,
            onCount: (_) => polls++)));
    await tester.pump();
    await tester.pump();
    final first = polls;
    expect(first, 1);

    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(polls, first + 1);

    active.value = false;
    await tester.pump();
    await tester.pump(const Duration(seconds: 15));
    await tester.pump();
    expect(polls, first + 1, reason: 'not at the fast pace on another tab');
    await tester.pump(const Duration(seconds: 6));
    await tester.pump();
    expect(polls, first + 2, reason: 'still often enough to keep the badge');

    active.value = true;
    await tester.pump();
    await tester.pump();
    expect(polls, first + 3, reason: 'refreshes when it shows again');
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(polls, first + 4, reason: 'and polls fast again');
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a list with a card open on top does not poll or refresh',
      (tester) async {
    final rig = ShellRig();
    final active = ValueNotifier(true);
    final navigator = GlobalKey<NavigatorState>();
    var polls = 0;
    await tester.pumpWidget(MaterialApp(
        navigatorKey: navigator,
        home: PendingScreen(
            services: rig.approvals,
            pollEvery: const Duration(seconds: 5),
            active: active,
            onCount: (_) => polls++)));
    await tester.pump();
    await tester.pump();
    final before = polls;
    navigator.currentState!.push(MaterialPageRoute<void>(
        builder: (_) => const Scaffold(body: Text('a card'))));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 20));
    expect(polls, before, reason: 'nobody sees the list');
    // Switching tabs and back with the card still open does not refresh behind it.
    active.value = false;
    await tester.pump();
    active.value = true;
    await tester.pump();
    await tester.pump(const Duration(seconds: 20));
    expect(polls, before);
    navigator.currentState!.pop();
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(polls, before + 1, reason: 'polls again once the list is back');
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a failing enrollment check does not stop the app starting',
      (tester) async {
    final rig = ShellRig(enrolled: false);
    final enrollment = _FlakyEnrollment(rig);
    tester.view.physicalSize = const Size(400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(AuthenticatorApp(
      config: AppConfig.resolve(''),
      approvals: rig.approvals,
      settings: rig.settings.services,
      shell: rig.shell,
      enrollment: enrollment,
    ));
    await tester.pump();
    await tester.pump();
    expect(tester.takeException(), isNull);
    expect(enrollment.calls, greaterThanOrEqualTo(1));
    expect(find.byType(EnrollmentScreen), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    enrollment.dispose();
  });
}
