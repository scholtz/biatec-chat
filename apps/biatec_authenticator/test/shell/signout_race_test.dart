import 'dart:async';

import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../approvals/support.dart';
import 'app_shell_test.dart' show ShellRig;

final _record = EnrollmentRecord(
    kid: 'dev-1',
    deviceToken: 'dev-token',
    activeAt: DateTime.utc(2020),
    attestation: 'att',
    recoveryCodeShown: true);

/// Holds each read's answer until [gate] completes, when one is set.
class _SlowStore extends MemoryEnrollmentStore {
  Completer<void>? gate;

  @override
  Future<EnrollmentRecord?> read() async {
    final record = await super.read();
    await gate?.future;
    return record;
  }
}

/// Reads fail while [fail] is set.
class _FailingReads extends MemoryEnrollmentStore {
  bool fail = false;

  @override
  Future<EnrollmentRecord?> read() async {
    if (fail) throw StateError('keystore locked');
    return super.read();
  }
}

EnrollmentController _controller(EnrollmentStore store) => EnrollmentController(
      keys: SoftwareKeyStore(deviceTestKey()),
      store: ObservableEnrollmentStore(store),
      client: OidcEnrollmentClient(baseUrl: 'http://oidc.test'),
    );

void main() {
  test('a record dropped on sign-out is not written back by a refresh',
      () async {
    final store = MemoryEnrollmentStore();
    await store.write(_record);
    final c = _controller(store);
    await c.refresh();
    expect(c.state.phase, EnrollmentPhase.active);

    await store.clear();
    c.signOut();
    expect(c.state.phase, EnrollmentPhase.notEnrolled,
        reason: 'at once, without reading anything');
    await c.refresh();
    expect(await store.read(), isNull);
    expect(c.state.phase, EnrollmentPhase.notEnrolled);

    // A new enrollment in the store is picked up again.
    await store.write(_record);
    await c.refresh();
    expect(c.state.phase, EnrollmentPhase.active);
    c.dispose();
  });

  test('a refresh that read the record before the sign-out does not undo it',
      () async {
    final store = _SlowStore();
    await store.write(_record);
    final c = _controller(store);
    await c.refresh();
    expect(c.state.phase, EnrollmentPhase.active);

    store.gate = Completer<void>();
    final running = c.refresh(); // reads the record, then waits
    await store.clear();
    c.signOut();
    store.gate!.complete();
    await running;
    expect(c.state.phase, EnrollmentPhase.notEnrolled);
    expect(await store.read(), isNull);
    c.dispose();
  });

  test('a store read that fails once does not sign an enrolled device out',
      () async {
    final store = _FailingReads();
    await store.write(_record);
    final c = _controller(store);
    await c.refresh();
    expect(c.state.phase, EnrollmentPhase.active);
    store.fail = true;
    await c.refresh();
    expect(c.readFailed, isTrue);
    expect(c.state.phase, EnrollmentPhase.active);
    c.dispose();
  });

  testWidgets('a queue that finds no shell keeps its original time',
      (tester) async {
    final rig = ShellRig();
    var now = DateTime.utc(2026, 10, 24, 12);
    rig.shell.clock = () => now;
    rig.shell.openApproval(rig.approvals, rig.requestId);
    now = now.add(const Duration(minutes: 1));
    rig.shell.openQueued(); // no shell yet: stays queued, at its first time
    now = now.add(const Duration(minutes: 1, seconds: 30));
    await rig.pump(tester);
    await tester.pump(const Duration(seconds: 1));
    expect(find.byType(ApprovalScreen), findsNothing,
        reason: 'it waited longer than the time to live in all');
    await rig.dispose(tester);
  });

  testWidgets('the list and badge empty when the device is signed out',
      (tester) async {
    final rig = ShellRig();
    final signal = ValueNotifier(0);
    final counts = <int>[];
    await tester.pumpWidget(MaterialApp(
        home: PendingScreen(
            services: rig.approvals,
            pollEvery: const Duration(hours: 1),
            refreshOn: signal,
            onCount: counts.add)));
    await tester.pump();
    await tester.pump();
    expect(counts, [1]);

    await rig.settings.enrollment.clear();
    signal.value++;
    await tester.pump();
    await tester.pump();
    expect(counts.last, 0);
    expect(find.byKey(const Key('pending-signed-out')), findsOneWidget);
    expect(find.byKey(const Key('pending-error')), findsNothing);
    expect(find.byKey(const Key('pending-stale')), findsNothing);
    expect(find.byKey(Key('pending-${rig.requestId}')), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('cards that went away with their shell can be opened again',
      (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await tester.pump(const Duration(seconds: 1));
    // The shell is taken down with the card still open: no pop is delivered.
    await rig.dispose(tester);

    await rig.pump(tester);
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await tester.pump(const Duration(seconds: 1));
    expect(find.byType(ApprovalScreen), findsOneWidget);
    await rig.dispose(tester);
  });

  test('a shell that is gone starts the next one on Approvals', () {
    final c = ShellController()..select(AppTab.clients);
    c.reset();
    expect(c.selected, AppTab.approvals);
    c.dispose();
  });

  testWidgets('a second tap for a card that is open does not open it twice',
      (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await tester.pump(const Duration(seconds: 1));
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await tester.pump(const Duration(seconds: 1));
    expect(find.byType(ApprovalScreen, skipOffstage: false), findsOneWidget);
    await rig.dispose(tester);
  });

  testWidgets(
      'a card covered by a screen that can be opened again is uncovered, and is the same card',
      (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await tester.pump(const Duration(seconds: 1));
    final card = tester.state(find.byType(ApprovalScreen));
    rig.shell.navigators[AppTab.approvals]!.currentState!.push(
        MaterialPageRoute<void>(
            settings:
                const RouteSettings(name: ShellController.recoveryCodeRoute),
            builder: (_) => const Scaffold(body: Text('on top'))));
    await tester.pump(const Duration(seconds: 1));
    rig.shell.select(AppTab.devices);
    await tester.pump();

    rig.shell.openApproval(rig.approvals, rig.requestId);
    // The tab's tickers start again with the frame that shows it, so the pop animation runs from the next one.
    for (var i = 0; i < 3; i++) {
      await tester.pump(const Duration(seconds: 1));
    }
    expect(rig.shell.selected, AppTab.approvals);
    expect(tester.state(find.byType(ApprovalScreen)), same(card),
        reason: 'what the user typed in it is kept');
    expect(find.text('on top', skipOffstage: false), findsNothing);
    await rig.dispose(tester);
  });

  for (final (what, name) in [
    ('another card', '${ApprovalScreen.routePrefix}ciba_other'),
    ('a screen not known to reopen', null),
  ]) {
    testWidgets('a card covered by $what is left where it is',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester);
      rig.shell.openApproval(rig.approvals, rig.requestId);
      await tester.pump(const Duration(seconds: 1));
      rig.shell.navigators[AppTab.approvals]!.currentState!.push(
          MaterialPageRoute<void>(
              settings: RouteSettings(name: name),
              builder: (_) => const Scaffold(body: Text('covering'))));
      await tester.pump(const Duration(seconds: 1));
      rig.shell.select(AppTab.devices);
      await tester.pump();

      rig.shell.openApproval(rig.approvals, rig.requestId);
      for (var i = 0; i < 3; i++) {
        await tester.pump(const Duration(seconds: 1));
      }
      expect(rig.shell.selected, AppTab.approvals);
      expect(find.text('covering'), findsOneWidget);
      expect(find.byType(ApprovalScreen, skipOffstage: false), findsOneWidget);
      await rig.dispose(tester);
    });
  }

  testWidgets('a card already on top is only focused', (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await tester.pump(const Duration(seconds: 1));
    final card = tester.state(find.byType(ApprovalScreen));
    rig.shell.select(AppTab.devices);
    await tester.pump();
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await tester.pump(const Duration(seconds: 1));
    expect(rig.shell.selected, AppTab.approvals);
    expect(tester.state(find.byType(ApprovalScreen)), same(card));
    await rig.dispose(tester);
  });

  testWidgets('focusing Approvals leaves an open card where it is',
      (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await tester.pump(const Duration(seconds: 1));
    rig.shell.select(AppTab.devices);
    await tester.pump();
    rig.shell.focusApprovals();
    await tester.pump(const Duration(seconds: 1));
    expect(rig.shell.selected, AppTab.approvals);
    expect(find.byType(ApprovalScreen), findsOneWidget);
    await rig.dispose(tester);
  });

  test('coming back after a long time reloads the showing tab and the list',
      () {
    final c = ShellController();
    var now = DateTime.utc(2026, 10, 24, 12);
    c.clock = () => now;
    c.select(AppTab.devices);
    final generation = c.reloads[AppTab.devices]!.value;
    final requests = c.requestsChanged.value;

    c.appPaused();
    now = now.add(const Duration(seconds: 5));
    c.appResumed();
    expect(c.reloads[AppTab.devices]!.value, generation,
        reason: 'a short break changes nothing');
    expect(c.requestsChanged.value, requests);

    c.appPaused();
    now = now.add(ShellController.reloadAfter);
    c.appResumed();
    expect(c.reloads[AppTab.devices]!.value, generation + 1);
    expect(c.requestsChanged.value, requests + 1);
    c.dispose();
  });

  test('the stack follows a replaced route', () {
    final c = ShellController();
    final observer = c.approvalsObserver;
    Route<void> card(String id) => MaterialPageRoute<void>(
        settings: RouteSettings(name: '${ApprovalScreen.routePrefix}$id'),
        builder: (_) => const SizedBox());
    final a = card('a');
    final b = card('b');
    observer.didPush(a, null);
    observer.didReplace(newRoute: b, oldRoute: a);
    expect(c.openCards, ['b']);
    observer.didRemove(b, null);
    expect(c.openCards, isEmpty);
    c.dispose();
  });
}
