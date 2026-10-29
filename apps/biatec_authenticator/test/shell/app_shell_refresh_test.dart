import 'dart:async';

import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/history_screen.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/push/push_controller.dart';
import 'package:biatec_authenticator/security/security_screen.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:biatec_authenticator/shell/tab_reload.dart';
import 'package:biatec_authenticator/ui/ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'app_shell_test.dart' show ShellRig, inBar;

HistoryEntry _entry(String id) => HistoryEntry(
    authReqId: id,
    decision: 'approve',
    summary: 'Send 1 ALGO',
    clientLabel: 'Biatec Chat',
    decidedAt: DateTime.utc(2026, 10, 24, 12),
    receipt: const {'signed': {}});

final _record = EnrollmentRecord(
    kid: 'dev-1',
    deviceToken: 'dev-token',
    activeAt: DateTime.utc(2020),
    attestation: 'att',
    recoveryCodeShown: true);

/// Answers a read with what was stored when it was asked, and only after [hold] (when set) completes.
class _SlowStore extends MemoryEnrollmentStore {
  Completer<void>? hold;

  @override
  Future<EnrollmentRecord?> read() async {
    final asked = await super.read();
    final h = hold;
    if (h != null) await h.future;
    return asked;
  }
}

/// History that cannot be read while [broken] is set.
class _FlakyHistory extends MemoryHistoryStore {
  bool broken = false;

  @override
  Future<List<HistoryEntry>> read() async {
    if (broken) throw StateError('storage locked');
    return super.read();
  }
}

/// Counts registrations asked for outside navigation events.
class _CountingPush extends PushController {
  _CountingPush(ShellRig rig)
      : super(
          enrollment: rig.approvals.enrollment,
          client: rig.approvals.client,
          onOpenRequest: (_) {},
        );

  int gateCalls = 0;

  @override
  Future<void> registerIfEnrolled([String? token]) {
    gateCalls++;
    return super.registerIfEnrolled(token);
  }
}

void main() {
  testWidgets('a tab left for a while loads afresh when shown again',
      (tester) async {
    final rig = ShellRig();
    var now = DateTime.utc(2026, 10, 24, 12);
    rig.shell.clock = () => now;
    final history = rig.approvals.history as MemoryHistoryStore;
    await rig.pump(tester);
    await tester.tap(inBar('History'));
    await tester.pumpAndSettle();
    expect(find.text('No approvals yet'), findsOneWidget);
    final screen = tester.state(find.byType(HistoryScreen));

    // A decision is signed while another tab shows.
    await tester.tap(inBar('Approvals'));
    await tester.pump(const Duration(seconds: 1));
    await history.add(_entry('ciba_new'));
    now = now.add(ShellController.reloadAfter);
    await tester.tap(inBar('History'));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('history-ciba_new')), findsOneWidget);
    expect(tester.state(find.byType(HistoryScreen)), same(screen),
        reason: 'reloaded in place, not rebuilt');
    await rig.dispose(tester);
  });

  testWidgets('a reload that fails keeps the history that is showing',
      (tester) async {
    final history = _FlakyHistory();
    await history.add(_entry('ciba_old'));
    final reload = ValueNotifier(0);
    await tester.pumpWidget(MaterialApp(
        home: TabReload(
            reload: reload, child: HistoryScreen(history: history))));
    await tester.pump();
    expect(find.byKey(const Key('history-ciba_old')), findsOneWidget);
    history.broken = true;
    reload.value++;
    await tester.pump();
    await tester.pump();
    expect(find.byKey(const Key('history-ciba_old')), findsOneWidget);
    expect(find.text('History could not be read'), findsNothing);
    expect(find.byKey(const Key('history-stale')), findsOneWidget,
        reason: 'the user is told it is not fresh');
    history.broken = false;
    await tester.tap(find.text('Try again'));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('history-stale')), findsNothing);
    expect(find.byKey(const Key('history-ciba_old')), findsOneWidget);
  });

  testWidgets('the settings tabs share one controller and load once',
      (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    final before = rig.settings.oidc.deviceListCalls;
    for (final tab in ['Devices', 'Clients', 'Settings']) {
      await tester.tap(inBar(tab));
      await tester.pumpAndSettle();
    }
    expect(rig.settings.oidc.deviceListCalls - before, 1);
    await rig.dispose(tester);
  });

  testWidgets('tabs that are not showing cannot take the keyboard focus',
      (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    await tester.tap(inBar('Settings'));
    await tester.pumpAndSettle();
    await tester.tap(inBar('Approvals'));
    await tester.pumpAndSettle();
    bool excluded(Type screen) => tester
        .widgetList<ExcludeFocus>(find.ancestor(
            of: find.byType(screen, skipOffstage: false),
            matching: find.byType(ExcludeFocus, skipOffstage: false)))
        .any((w) => w.excluding);
    expect(excluded(SecurityScreen), isTrue);
    expect(excluded(PendingScreen), isFalse);
    await rig.dispose(tester);
  });

  testWidgets('the empty list can be pulled to refresh', (tester) async {
    final rig = ShellRig(pending: 0);
    var loads = 0;
    await tester.pumpWidget(MaterialApp(
        home: PendingScreen(
            services: rig.approvals,
            pollEvery: const Duration(hours: 1),
            onCount: (_) => loads++)));
    await tester.pump();
    await tester.pump();
    expect(find.text('No pending approvals'), findsOneWidget);
    final before = loads;
    await tester.fling(
        find.text('No pending approvals'), const Offset(0, 400), 1000);
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    await tester.pump(const Duration(seconds: 1));
    expect(loads, greaterThan(before));
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a quick switch keeps the tab as it was', (tester) async {
    final rig = ShellRig();
    var now = DateTime.utc(2026, 10, 24, 12);
    rig.shell.clock = () => now;
    await rig.pump(tester);
    await tester.tap(inBar('Devices'));
    await tester.pumpAndSettle();
    final generation = rig.shell.reloads[AppTab.devices]!.value;
    final history = rig.shell.reloads[AppTab.history]!.value;
    await tester.tap(inBar('History'));
    await tester.pumpAndSettle();
    now = now.add(const Duration(seconds: 5));
    await tester.tap(inBar('Devices'));
    await tester.pumpAndSettle();
    expect(rig.shell.reloads[AppTab.devices]!.value, generation);
    // History is local: it is read again on every visit, so a decision just made is in it.
    await tester.tap(inBar('History'));
    await tester.pumpAndSettle();
    expect(rig.shell.reloads[AppTab.history]!.value, history + 1);
    await rig.dispose(tester);
  });

  testWidgets('choosing the tab that shows does not reload it', (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    await tester.tap(inBar('History'));
    await tester.pumpAndSettle();
    final generation = rig.shell.reloads[AppTab.history]!.value;
    await tester.tap(inBar('History'));
    await tester.pumpAndSettle();
    expect(rig.shell.reloads[AppTab.history]!.value, generation);
    await rig.dispose(tester);
  });

  testWidgets('tabs that are not showing have their tickers off',
      (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    await tester.tap(inBar('History'));
    await tester.pumpAndSettle();
    bool tickersOn(Finder f) => TickerMode.valuesOf(tester.element(f)).enabled;
    expect(tickersOn(find.byType(HistoryScreen)), isTrue);
    expect(tickersOn(find.byType(PendingScreen, skipOffstage: false)), isFalse);
    await rig.dispose(tester);
  });

  testWidgets('the list refreshes when a card opened from outside it closes',
      (tester) async {
    final rig = ShellRig();
    await rig.pump(tester);
    final before = rig.shell.requestsChanged.value;
    rig.shell.openApproval(rig.approvals, rig.requestId);
    await tester.pump(const Duration(seconds: 1));
    expect(find.byType(ApprovalScreen), findsOneWidget);
    rig.shell.navigators[AppTab.approvals]!.currentState!.pop();
    await tester.pump(const Duration(seconds: 1));
    await tester.pump(const Duration(seconds: 1));
    expect(rig.shell.requestsChanged.value, before + 1);
    await rig.dispose(tester);
  });

  testWidgets(
      'the badge keeps counting while a card is open and another tab shows',
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
            backgroundEvery: const Duration(seconds: 20),
            active: active,
            onCount: (_) => polls++)));
    await tester.pump();
    await tester.pump();
    navigator.currentState!.push(MaterialPageRoute<void>(
        builder: (_) => const Scaffold(body: Text('a card'))));
    await tester.pumpAndSettle();
    active.value = false;
    await tester.pump();
    final before = polls;
    await tester.pump(const Duration(seconds: 21));
    await tester.pump();
    expect(polls, before + 1);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('the list does not poll while the app is in the background',
      (tester) async {
    final rig = ShellRig();
    final foreground = ValueNotifier(true);
    var polls = 0;
    await tester.pumpWidget(MaterialApp(
        home: PendingScreen(
            services: rig.approvals,
            pollEvery: const Duration(seconds: 5),
            foreground: foreground,
            onCount: (_) => polls++)));
    await tester.pump();
    await tester.pump();
    foreground.value = false;
    await tester.pump();
    final before = polls;
    await tester.pump(const Duration(minutes: 2));
    await tester.pump();
    expect(polls, before);
    foreground.value = true;
    await tester.pump(const Duration(seconds: 6));
    await tester.pump();
    expect(polls, greaterThan(before));
    await tester.pumpWidget(const SizedBox());
  });

  test('the app going to the background and back is passed on', () {
    final c = ShellController();
    c.appPaused();
    expect(c.foreground.value, isFalse);
    c.appResumed();
    expect(c.foreground.value, isTrue);
    c.dispose();
  });

  testWidgets('a refresh signal makes the list load again', (tester) async {
    final rig = ShellRig();
    final signal = ValueNotifier(0);
    var loads = 0;
    await tester.pumpWidget(MaterialApp(
        home: PendingScreen(
            services: rig.approvals,
            pollEvery: const Duration(hours: 1),
            refreshOn: signal,
            onCount: (_) => loads++)));
    await tester.pump();
    await tester.pump();
    expect(loads, 1);
    signal.value++;
    await tester.pump();
    await tester.pump();
    expect(loads, 2);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a sign-out that happens while a check is reading is still seen',
      (tester) async {
    final rig = ShellRig();
    final inner = _SlowStore();
    await inner.write(_record);
    final store = ObservableEnrollmentStore(inner);
    tester.view.physicalSize = const Size(400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(AuthenticatorApp(
      config: AppConfig.resolve(''),
      approvals: rig.approvalsWith(store),
      settings: rig.settings.services,
      shell: rig.shell,
    ));
    await tester.pump();
    await tester.pump();
    await tester.pump();
    expect(find.byType(NavigationBar), findsOneWidget);

    // A check starts and waits for its read (a write notifies the gate).
    inner.hold = Completer<void>();
    await store.write(_record);
    await tester.pump();
    // The device signs itself out while that read is still pending.
    await store.clear();
    await tester.pump();
    inner.hold!.complete();
    await tester.pump();
    await tester.pump();
    await tester.pump();
    await tester.pump();
    expect(find.byType(EnrollmentScreen), findsOneWidget);
    expect(await inner.read(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('push is registered when the device becomes active',
      (tester) async {
    final rig = ShellRig();
    final push = _CountingPush(rig);
    tester.view.physicalSize = const Size(400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(AuthenticatorApp(
      config: AppConfig.resolve(''),
      approvals: rig.approvals,
      settings: rig.settings.services,
      shell: rig.shell,
      push: push,
    ));
    await tester.pump();
    await tester.pump();
    expect(push.gateCalls, greaterThanOrEqualTo(1));
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('the gate says whether a tapped notification may wait',
      (tester) async {
    final enrolled = ShellRig();
    await enrolled.pump(tester);
    expect(enrolled.shell.canQueue, isTrue);
    await enrolled.dispose(tester);

    final fresh = ShellRig(enrolled: false);
    await fresh.pump(tester);
    expect(fresh.shell.canQueue, isFalse);
    fresh.shell.openApproval(fresh.approvals, fresh.requestId);
    expect(fresh.shell.navigators[AppTab.approvals]!.currentState, isNull);
    await fresh.dispose(tester);
  });

  testWidgets(
      'states scroll when there is little room, so retry stays reachable',
      (tester) async {
    tester.view.physicalSize = const Size(320, 260);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    var retries = 0;
    await tester.pumpWidget(MaterialApp(
      builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: const TextScaler.linear(3)),
          child: child!),
      home: Scaffold(
        body: ErrorState(
          message: 'The secure storage on this device is slow to answer.',
          onRetry: () => retries++,
        ),
      ),
    ));
    expect(tester.takeException(), isNull);
    await tester.ensureVisible(find.text('Try again'));
    await tester.tap(find.text('Try again'));
    expect(retries, 1);
  });
}
